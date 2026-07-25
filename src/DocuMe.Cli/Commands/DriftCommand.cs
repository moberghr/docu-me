using System.CommandLine;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Dashboard;
using DocuMe.Core.Drift;
using DocuMe.Core.Git;
using DocuMe.Core.Markdown;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using DocuMe.Core.Status;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume drift</c> — PLAN.md §6.4. Diffs two revisions, matches the changed files against every
/// page's <c>sources</c> globs, and reports which pages may no longer describe the code.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The report half reads nothing from Confluence.</strong> The whole answer is a <c>git diff</c>
/// plus a glob match, so a bare <c>drift</c> — and <c>--mark --dry-run</c> with it — needs no credentials,
/// no space and no network. Only <c>--mark</c> itself talks to Confluence.
/// </para>
/// <para>
/// <strong><c>--mark</c> writes labels, never page bodies</strong> (§6.4, rule §9.3). Staleness is a label
/// plus a state flag plus a dashboard row; editing a body to say "this may be out of date" would bump the
/// page version, which invalidates nothing but disturbs the approval history §8 keeps for audit.
/// </para>
/// <para>
/// <strong>Advisory: exit 0 even when pages drifted</strong>, unless <c>--fail-on-drift</c>. A team that
/// wants a blocking check opts in; a team that does not gets a comment on the PR and keeps merging. A
/// broken <em>run</em> is a different thing and always exits 1 — a bad baseline, or a label write that
/// failed, must not read as "no drift".
/// </para>
/// </remarks>
internal static class DriftCommand
{
    /// <summary>Where <c>docume init</c> scaffolds the state file, relative to the wiki root (§5.3).</summary>
    private const string DefaultStateFile = "_meta/state.json";

    private const string TableFormat = "table";
    private const string JsonFormat = "json";
    private const string CommentFormat = "github-comment";

    private static readonly string[] Formats = [TableFormat, JsonFormat, CommentFormat];

    public static Command Build()
    {
        var configOption = new Option<string>("--config")
        {
            Description = "Path to docume.json. Its directory is the repo root wiki.root and every "
                + "'sources' glob resolve against.",
            DefaultValueFactory = _ => ConfigLoader.DefaultFileName,
        };
        var stateOption = new Option<string>("--state")
        {
            Description = $"Path to state.json. Defaults to <wiki.root>/{DefaultStateFile}; read for its "
                + "baselineSha, and written only by --mark.",
        };
        var baselineOption = new Option<string>("--baseline")
        {
            Description = "Revision to diff from. Defaults to state.baselineSha — the commit the wiki "
                + "content was last generated against.",
        };
        var headOption = new Option<string>("--head")
        {
            Description = "Revision to diff to. Defaults to HEAD.",
            DefaultValueFactory = _ => "HEAD",
        };
        var formatOption = new Option<string>("--format")
        {
            Description = $"Output shape: {string.Join(" | ", Formats)}. 'github-comment' is a markdown "
                + "block for a PR comment; 'json' prints nothing else.",
            DefaultValueFactory = _ => TableFormat,
        };
        var failOnDriftOption = new Option<bool>("--fail-on-drift")
        {
            Description = "Exit 1 when any page is affected. Without it the command is advisory and "
                + "always exits 0.",
        };
        var markOption = new Option<bool>("--mark")
        {
            Description = "Add the stale label to every affected page in Confluence, set stale: true in "
                + "state, and refresh the dashboard. Labels only — never a page-body edit (§6.4).",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "With --mark: report what would be labelled and write nothing. Needs no "
                + "credentials — the plan is state plus the diff.",
        };
        var allowProtectedSpaceOption = new Option<bool>("--allow-protected-space")
        {
            Description = "With --mark: label pages even though confluence.spaceKey is listed in "
                + "confluence.protectedSpaces. One run only; the list stays as it is.",
        };

        const string description =
            "Report which wiki pages derive from code changed between two revisions. Reads only, unless "
            + "--mark.";

        var command = new Command("drift", description)
        {
            configOption,
            stateOption,
            baselineOption,
            headOption,
            formatOption,
            failOnDriftOption,
            markOption,
            dryRunOption,
            allowProtectedSpaceOption,
        };

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            new DriftOptions
            {
                ConfigPath = parseResult.GetValue(configOption)!,
                StatePath = parseResult.GetValue(stateOption),
                Baseline = parseResult.GetValue(baselineOption),
                Head = parseResult.GetValue(headOption)!,
                Format = parseResult.GetValue(formatOption)!,
                FailOnDrift = parseResult.GetValue(failOnDriftOption),
                Mark = parseResult.GetValue(markOption),
                DryRun = parseResult.GetValue(dryRunOption),
                AllowProtectedSpace = parseResult.GetValue(allowProtectedSpaceOption),
            },
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(DriftOptions options, CancellationToken cancellationToken)
    {
        // Validated before anything else, so a typo costs a message rather than a git process and a
        // tree load whose output then has nowhere to go. Reported on stderr because the one thing not
        // known here is which format the caller meant: a CI step piping stdout into a PR comment must
        // not post this sentence as the comment.
        if (!Formats.Contains(options.Format, StringComparer.Ordinal))
        {
            return Fail(
                $"--format '{options.Format}' is not one of: {string.Join(", ", Formats)}.",
                quiet: true);
        }

        var quiet = !string.Equals(options.Format, TableFormat, StringComparison.Ordinal);

        // A flag that quietly did nothing would be worse than an unknown-option error: both of these
        // only mean something for the write half, and a caller passing --dry-run to a read-only run has
        // misunderstood what it is about to do.
        if (!options.Mark && (options.DryRun || options.AllowProtectedSpace))
        {
            return Fail(
                "--dry-run and --allow-protected-space only mean something with --mark. Without it drift "
                + "writes nothing anyway, so there is no run to make dry and no space to unlock.",
                quiet);
        }

        var fullConfigPath = Path.GetFullPath(options.ConfigPath);

        DocumeConfig config;
        try
        {
            config = ConfigLoader.Load(fullConfigPath);
        }
        catch (ConfigNotFoundException ex)
        {
            return Fail(ex.Message, quiet);
        }
        catch (ConfigValidationException ex)
        {
            return Fail(ex.Message, quiet);
        }
        catch (JsonException ex)
        {
            return Fail($"{fullConfigPath} is not valid JSON: {ex.Message}", quiet);
        }

        // Everything the write half needs, resolved before the first request. The refusal in particular:
        // a run that read the whole space and only then declined to write would spend a rate-limit
        // budget to learn what the config already said.
        MarkTarget? target = null;
        if (options.Mark)
        {
            var resolved = ResolveMarkTarget(config, options, quiet);
            if (resolved.Failure is { } failure)
            {
                return failure;
            }

            target = resolved.Target;
        }

        var repoRoot = Path.GetDirectoryName(fullConfigPath) ?? Directory.GetCurrentDirectory();
        var wikiRoot = Path.GetFullPath(Path.Combine(repoRoot, config.Wiki.Root));
        var resolvedStatePath = options.StatePath is { Length: > 0 }
            ? Path.GetFullPath(options.StatePath)
            : Path.Combine(wikiRoot, DefaultStateFile.Replace('/', Path.DirectorySeparatorChar));

        var stateExists = File.Exists(resolvedStatePath);

        // --mark joins the affected pages onto state to find their page ids, so a missing state file is
        // not "nothing to mark" — it is a run that can never mark anything, and saying so beats
        // reporting every affected page as unpublished.
        if (options.Mark && !stateExists)
        {
            return Fail(
                $"No state file at {resolvedStatePath}. --mark labels pages a publish recorded, so there "
                + "is nothing to label until `docume publish` has run.",
                quiet);
        }

        DocumeState state;
        try
        {
            state = stateExists ? StateStore.Load(resolvedStatePath) : new DocumeState();
        }
        catch (StateVersionException ex)
        {
            return Fail(ex.Message, quiet);
        }
        catch (JsonException ex)
        {
            return Fail($"{resolvedStatePath} is not valid JSON: {ex.Message}", quiet);
        }

        var resolvedBaseline = options.Baseline is { Length: > 0 } ? options.Baseline : state.BaselineSha;
        if (resolvedBaseline is not { Length: > 0 })
        {
            // Loud rather than "assume the whole history": a baseline nobody supplied would otherwise
            // silently become an arbitrary one, and every answer after that is fiction.
            return Fail(
                $"No baseline to diff from. Pass --baseline <sha>, or set baselineSha in "
                + $"{resolvedStatePath} (§5.3 — the commit the wiki was generated against).",
                quiet);
        }

        WikiTree tree;
        try
        {
            tree = WikiTree.Load(wikiRoot, config.Wiki);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Fail(ex.Message, quiet);
        }
        catch (WikiTreeException ex)
        {
            return Fail(
                $"The wiki tree cannot be read, so there are no sources to match against "
                + $"({ex.Errors.Count} error(s)):{Environment.NewLine}  - "
                + string.Join($"{Environment.NewLine}  - ", ex.Errors),
                quiet);
        }

        IReadOnlyList<string> changed;
        try
        {
            changed = await GitRepository
                .ChangedFilesBetweenAsync(repoRoot, resolvedBaseline, options.Head, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return Fail(ex.Message, quiet);
        }

        var report = DriftPlanner.Plan(resolvedBaseline, options.Head, changed, tree.Pages);

        Report(report, options.Format, options.Mark);

        var driftExit = options.FailOnDrift && report.HasDrift ? 1 : 0;

        if (target is null)
        {
            return driftExit;
        }

        var paths = new StatusPaths
        {
            ConfigPath = fullConfigPath,
            WikiRoot = wikiRoot,
            StatePath = resolvedStatePath,
            StateFileExists = stateExists,
        };

        var markExit = await MarkAsync(
                target,
                config,
                paths,
                tree,
                state,
                report,
                options.DryRun,
                cancellationToken)
            .ConfigureAwait(false);

        // A failed write outranks the advisory verdict: exit 0 here would say "nothing to worry about"
        // about a run that did not do what it was asked.
        return markExit != 0 ? markExit : driftExit;
    }

    /// <summary>
    /// The write half: the plan, the label writes, the one state write, and the dashboard refresh (§6.4).
    /// </summary>
    private static async Task<int> MarkAsync(
        MarkTarget target,
        DocumeConfig config,
        StatusPaths paths,
        WikiTree tree,
        DocumeState state,
        DriftReport report,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var console = target.Console;
        var plan = DriftMarkPlanner.Plan(report, state);

        console.WriteLine();
        RenderSkips(console, plan);

        if (!plan.HasChanges)
        {
            console.MarkupLine(
                $"[green]NOTHING TO MARK[/] [grey]— no affected page needs the "
                + $"{config.Labels.Stale.EscapeMarkup()} label, so the dashboard was left alone too.[/]");

            return 0;
        }

        if (dryRun)
        {
            foreach (var page in plan.ToLabel)
            {
                console.MarkupLine(
                    $"  [yellow]would +{config.Labels.Stale.EscapeMarkup()}[/] {page.Path.EscapeMarkup()} "
                    + $"[grey](page {page.PageId.EscapeMarkup()})[/]");
            }

            console.MarkupLine(
                $"[yellow]--dry-run[/] — {plan.ChangeCount} label(s) planned, "
                + $"{paths.StatePath.EscapeMarkup()} and the dashboard left alone.");

            return 0;
        }

        ConfluenceCredentials credentials;
        try
        {
            credentials = ConfluenceCredentials.FromEnvironment();
        }
        catch (ConfluenceCredentialsException ex)
        {
            return Fail(ex.Message, target.Quiet);
        }

        if (!Uri.TryCreate(config.Confluence.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            return Fail(
                $"confluence.baseUrl '{config.Confluence.BaseUrl}' is not an absolute URL. It should look "
                + "like https://your-site.atlassian.net/wiki (PLAN.md §5.1).",
                target.Quiet);
        }

        using var client = ConfluenceClient.Create(
            new ConfluenceClientOptions { BaseUrl = baseUrl },
            credentials);

        var (marked, failure) = await LabelAsync(
                client,
                config,
                plan,
                state,
                target,
                baseUrl,
                cancellationToken)
            .ConfigureAwait(false);

        // Saved even when a write failed part-way: the labels that did land are facts, and state that
        // denied them would make the next run try to add them again.
        if (!ReferenceEquals(marked, state))
        {
            StateStore.Save(paths.StatePath, marked);
            console.MarkupLine($"State written: [blue]{paths.StatePath.EscapeMarkup()}[/]");
        }

        if (failure is not null)
        {
            return Fail(failure, target.Quiet);
        }

        return await RefreshDashboardAsync(
                client,
                config,
                paths,
                tree,
                marked,
                target,
                baseUrl,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One label write per page, each followed by its state flag, so a run that dies half-way leaves the
    /// two agreeing about the pages it got through.
    /// </summary>
    private static async Task<(DocumeState Marked, string? Failure)> LabelAsync(
        ConfluenceClient client,
        DocumeConfig config,
        DriftMarkPlan plan,
        DocumeState state,
        MarkTarget target,
        Uri baseUrl,
        CancellationToken cancellationToken)
    {
        // Hoisted out of the loop: CA1861 rejects a constant array as an argument, and the label is the
        // same one for every page anyway.
        string[] labels = [config.Labels.Stale];
        var marked = state;

        foreach (var page in plan.ToLabel)
        {
            try
            {
                await client.AddLabelsAsync(page.PageId, labels, cancellationToken).ConfigureAwait(false);
            }
            catch (ConfluenceException ex)
            {
                return (marked, ex.Message);
            }
            catch (HttpRequestException ex)
            {
                return (marked, $"Confluence is unreachable at {baseUrl}: {ex.Message}");
            }

            marked = StateUpdates.SetStale(marked, page.Path, stale: true);

            target.Console.MarkupLine(
                $"  [yellow]+{config.Labels.Stale.EscapeMarkup()}[/] {page.Path.EscapeMarkup()} "
                + $"[grey](page {page.PageId.EscapeMarkup()})[/]");
        }

        return (marked, null);
    }

    /// <summary>
    /// The dashboard refresh §6.4 asks for, through the same <see cref="DashboardPublisher"/> the
    /// <c>dashboard</c> command uses. Rendered from the just-marked state, so the page agrees with the
    /// labels this run wrote even before the cron <c>sync</c> commits them.
    /// </summary>
    private static async Task<int> RefreshDashboardAsync(
        ConfluenceClient client,
        DocumeConfig config,
        StatusPaths paths,
        WikiTree tree,
        DocumeState marked,
        MarkTarget target,
        Uri baseUrl,
        CancellationToken cancellationToken)
    {
        target.Console.MarkupLine(
            $"Refreshing [blue]{target.PageTitle.EscapeMarkup()}[/] in "
            + $"[blue]{target.SpaceKey.EscapeMarkup()}[/]…");

        try
        {
            var render = await DashboardPublisher
                .RenderAsync(
                    client,
                    config,
                    paths,
                    tree,
                    marked,
                    target.SpaceKey,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);

            var result = await DashboardPublisher
                .UpsertAsync(
                    client,
                    config.Confluence,
                    target.SpaceKey,
                    target.PageTitle,
                    render.Body,
                    cancellationToken)
                .ConfigureAwait(false);

            return DashboardOutput.Report(target.Console, result, target.SpaceKey, target.PageTitle);
        }
        catch (ConfluenceException ex)
        {
            return Fail(
                $"The labels were written, but the dashboard refresh failed: {ex.Message}",
                target.Quiet);
        }
        catch (HttpRequestException ex)
        {
            return Fail(
                $"The labels were written, but Confluence became unreachable at {baseUrl} before the "
                + $"dashboard refresh: {ex.Message}",
                target.Quiet);
        }
    }

    /// <summary>
    /// The affected pages <c>--mark</c> will not touch. Named rather than counted: an affected page with
    /// no published counterpart is the one case where a green-looking mark run silently covers less than
    /// the report it just printed.
    /// </summary>
    private static void RenderSkips(IAnsiConsole console, DriftMarkPlan plan)
    {
        foreach (var page in plan.AlreadyMarked)
        {
            console.MarkupLine(
                $"  [grey]=stale[/] {page.Path.EscapeMarkup()} [grey](already marked; no request "
                + "spent)[/]");
        }

        foreach (var page in plan.Unmarkable)
        {
            console.MarkupLine(
                $"  [yellow]skipped[/] {page.Path.EscapeMarkup()} "
                + $"[grey]— {page.Reason.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// What <c>--mark</c> writes to, and whether it may: the space, the dashboard page, the refusal, and
    /// where the write half's own log goes. All of it decided from the config alone, before the tree load
    /// and the diff — so a refusal costs zero requests and a misconfiguration costs no work.
    /// </summary>
    private static (MarkTarget? Target, int? Failure) ResolveMarkTarget(
        DocumeConfig config,
        DriftOptions options,
        bool quiet)
    {
        if (config.Confluence.SpaceKey is not { Length: > 0 } spaceKey)
        {
            return (null, Fail(
                "confluence.spaceKey is not set in docume.json, and --mark needs it: the dashboard it "
                + "refreshes is found in a space (§6.5).",
                quiet));
        }

        if (config.Dashboard.Title is not { Length: > 0 } pageTitle)
        {
            return (null, Fail(
                "dashboard.title is empty in docume.json, and --mark refreshes the dashboard (§6.4). The "
                + "page is found by title, so there is nothing to find or create.",
                quiet));
        }

        var refusal = PublishGuard.WriteRefusal(config.Confluence, options.AllowProtectedSpace);
        if (refusal is not null && !options.DryRun)
        {
            return (null, Fail(refusal, quiet));
        }

        // The write half's log goes to stderr in the machine formats, where stdout is a JSON document or
        // a PR comment body a CI step pipes somewhere: a "+stale" line in the middle of either is a
        // corrupt payload.
        var console = quiet
            ? AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) })
            : AnsiConsole.Console;

        if (refusal is not null)
        {
            console.MarkupLine($"[yellow]note[/] — {refusal.EscapeMarkup()}");
            console.MarkupLine("[grey]--dry-run, so the plan is printed anyway; a real run refuses.[/]");
        }

        // The credentials and the base URL are resolved later, next to the client they build: a dry run
        // needs neither, and making it demand a token would put the one wholly offline half of --mark
        // behind an online setup.
        return (
            new MarkTarget
            {
                SpaceKey = spaceKey,
                PageTitle = pageTitle,
                Console = console,
                Quiet = quiet,
            },
            null);
    }

    private static void Report(DriftReport report, string format, bool mark)
    {
        switch (format)
        {
            case JsonFormat:
                // Console.WriteLine, not AnsiConsole: Spectre wraps to the terminal width, which would
                // put newlines inside JSON string values and hand a CI step a body it cannot parse.
                Console.WriteLine(report.ToJson());
                break;

            case CommentFormat:
                // Console.Write: the block already ends in a newline, and a markdown comment posted
                // through an API is bytes rather than terminal output.
                Console.Write(DriftComment.Render(report));
                break;

            default:
                RenderTable(report, mark);
                break;
        }
    }

    private static void RenderTable(DriftReport report, bool mark)
    {
        AnsiConsole.MarkupLine($"Baseline: [grey]{report.Baseline.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"Head:     [grey]{report.Head.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"Changed:  [grey]{report.ChangedFileCount} file(s)[/]");

        RenderPages(report);
        RenderVerdict(report, mark);
    }

    /// <summary>
    /// The affected pages, uncapped: a drift report that quietly listed the first N pages would read as
    /// a complete answer when it was not. Only the per-pattern file lists are trimmed, and they say so.
    /// </summary>
    private static void RenderPages(DriftReport report)
    {
        if (!report.HasDrift)
        {
            return;
        }

        AnsiConsole.WriteLine();

        var table = new Table()
            .AddColumn("Page")
            .AddColumn("Path")
            .AddColumn("Files")
            .AddColumn("Matched patterns");

        foreach (var page in report.Pages)
        {
            table.AddRow(
                page.Title.EscapeMarkup(),
                page.Path.EscapeMarkup(),
                page.MatchedFileCount.ToString(),
                Patterns(page).EscapeMarkup());
        }

        AnsiConsole.Write(table);
    }

    private static string Patterns(DriftedPage page) => string.Join(
        Environment.NewLine,
        page.Matches.Select(match => $"{match.Pattern} ({match.Files.Count})"));

    private static void RenderVerdict(DriftReport report, bool mark)
    {
        AnsiConsole.WriteLine();

        if (report.SourcesUndeclared)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No page declares a 'sources:' glob, so drift can never be reported "
                + $"({report.PageCount} page(s) in the tree). Add 'sources:' to page frontmatter to "
                + $"link a page to the code it documents (§5.2).[/]");

            return;
        }

        if (!report.HasDrift)
        {
            AnsiConsole.MarkupLine(
                $"[green]No documented sources touched.[/] [grey]{report.PagesWithSourcesCount} of "
                + $"{report.PageCount} page(s) declare sources.[/]");

            return;
        }

        var advisory = mark
            ? "The mark plan follows."
            : "Advisory — nothing was changed or marked.";

        AnsiConsole.MarkupLine(
            $"[yellow]{report.AffectedCount} of {report.PagesWithSourcesCount} page(s) with declared "
            + $"sources may need review.[/] [grey]{advisory}[/]");
    }

    /// <summary>
    /// A failed run, which is never drift: exit 1 whatever <c>--fail-on-drift</c> says, and on stderr in
    /// the machine formats so a CI step never posts an error message as a PR comment body.
    /// </summary>
    private static int Fail(string message, bool quiet)
    {
        if (quiet)
        {
            Console.Error.WriteLine(message);

            return 1;
        }

        AnsiConsole.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

        return 1;
    }

    /// <summary>The parsed command line, gathered so the run reads as one object rather than nine arguments.</summary>
    private sealed record DriftOptions
    {
        public required string ConfigPath { get; init; }

        public string? StatePath { get; init; }

        public string? Baseline { get; init; }

        public required string Head { get; init; }

        public required string Format { get; init; }

        public bool FailOnDrift { get; init; }

        public bool Mark { get; init; }

        public bool DryRun { get; init; }

        public bool AllowProtectedSpace { get; init; }
    }

    /// <summary>
    /// What <c>--mark</c> writes to, decided from the config before the tree load and the diff. Its
    /// existence is the permission: a run that may not write never gets one.
    /// </summary>
    private sealed record MarkTarget
    {
        public required string SpaceKey { get; init; }

        public required string PageTitle { get; init; }

        /// <summary>Where the write half's log goes — stderr in the machine formats.</summary>
        public required IAnsiConsole Console { get; init; }

        /// <summary>Whether stdout is carrying a machine payload, which is what sends failures to stderr.</summary>
        public required bool Quiet { get; init; }
    }
}
