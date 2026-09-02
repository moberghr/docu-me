using System.CommandLine;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Markdown;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using DocuMe.Core.Status;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume status</c> — PLAN.md §6.6. The dashboard's data as a local terminal report, plus the
/// <c>doctor</c>-lite checks: credential variables set, Node present, render script present, token
/// accepted, space reachable, state consistent with the file tree.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It writes nothing, anywhere.</strong> Not the state file, not Confluence — §6.5's
/// <c>dashboard</c> is the command that publishes a page, and this one is a terminal report whose only
/// side effect is stdout. The single request it makes is one space lookup, and only when credentials
/// are in the environment and <c>--offline</c> was not passed.
/// </para>
/// <para>
/// <strong>The table and <c>--json</c> come from one <see cref="StatusReport"/></strong>, so they cannot
/// drift. §10's skills end by pasting <c>docume status --json</c> into a PR body, which makes the JSON a
/// consumed contract rather than a debug aid; in that mode nothing but JSON reaches stdout.
/// </para>
/// </remarks>
internal static class StatusCommand
{
    /// <summary>Where <c>docume init</c> scaffolds the state file, relative to the wiki root (§5.3).</summary>
    private const string DefaultStateFile = "_meta/state.json";

    public static Command Build()
    {
        var configOption = new Option<string>("--config")
        {
            Description = "Path to docume.json. Its directory is the repo root wiki.root resolves against.",
            DefaultValueFactory = _ => ConfigLoader.DefaultFileName,
        };
        var stateOption = new Option<string>("--state")
        {
            Description = $"Path to state.json. Defaults to <wiki.root>/{DefaultStateFile}; a missing "
                + "file means nothing has been published, so every page reads as unpublished.",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Print the report as JSON and nothing else, for a PR body or a CI step.",
        };
        var offlineOption = new Option<bool>("--offline")
        {
            Description = "Skip the one Confluence request (the token and space probe). Everything else "
                + "in this report is computed locally anyway.",
        };
        var failOnDriftOption = new Option<bool>("--fail-on-drift")
        {
            Description = "Exit non-zero when the published wiki differs from the repo — unpublished, "
                + "changed, moved pages, or orphans in state. Without it the command is advisory and "
                + "always exits 0.",
        };

        const string description =
            "Report what is published, what drifted, and whether this repo is set up to publish. "
            + "Writes nothing.";

        var command = new Command("status", description)
        {
            configOption,
            stateOption,
            jsonOption,
            offlineOption,
            failOnDriftOption,
        };

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetValue(configOption)!,
            parseResult.GetValue(stateOption),
            parseResult.GetValue(jsonOption),
            parseResult.GetValue(offlineOption),
            parseResult.GetValue(failOnDriftOption),
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string configPath,
        string? statePath,
        bool json,
        bool offline,
        bool failOnDrift,
        CancellationToken cancellationToken)
    {
        var fullConfigPath = Path.GetFullPath(configPath);

        DocumeConfig config;
        try
        {
            config = ConfigLoader.Load(fullConfigPath);
        }
        catch (ConfigNotFoundException ex)
        {
            return Fail(ex.Message, json);
        }
        catch (ConfigValidationException ex)
        {
            return Fail(ex.Message, json);
        }
        catch (JsonException ex)
        {
            return Fail($"{fullConfigPath} is not valid JSON: {ex.Message}", json);
        }

        var repoRoot = Path.GetDirectoryName(fullConfigPath) ?? Directory.GetCurrentDirectory();
        var wikiRoot = Path.GetFullPath(Path.Combine(repoRoot, config.Wiki.Root));
        var resolvedStatePath = statePath is { Length: > 0 }
            ? Path.GetFullPath(statePath)
            : Path.Combine(wikiRoot, DefaultStateFile.Replace('/', Path.DirectorySeparatorChar));

        WikiTree tree;
        try
        {
            tree = WikiTree.Load(wikiRoot, config.Wiki);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Fail(ex.Message, json);
        }
        catch (WikiTreeException ex)
        {
            // The one condition this command reports by failing rather than in a row: without a tree
            // there is nothing to compare against Confluence, so the tree's own errors ARE the report.
            return FailTree(ex, json);
        }

        var stateExists = File.Exists(resolvedStatePath);

        DocumeState state;
        try
        {
            state = stateExists ? StateStore.Load(resolvedStatePath) : new DocumeState();
        }
        catch (StateVersionException ex)
        {
            return Fail(ex.Message, json);
        }
        catch (JsonException ex)
        {
            return Fail($"{resolvedStatePath} is not valid JSON: {ex.Message}", json);
        }

        var paths = new StatusPaths
        {
            ConfigPath = fullConfigPath,
            WikiRoot = wikiRoot,
            StatePath = resolvedStatePath,
            StateFileExists = stateExists,
        };

        var probes = await ProbeAsync(config, repoRoot, offline, cancellationToken).ConfigureAwait(false);
        var report = StatusModel.Build(paths, config, tree, state, probes);

        if (json)
        {
            // Console.WriteLine, not AnsiConsole: Spectre wraps to the terminal width, which would put
            // newlines inside JSON string values and hand a skill a body it cannot parse.
            Console.WriteLine(report.ToJson());
        }
        else
        {
            Render(report);
        }

        return failOnDrift && report.HasDrift ? 1 : 0;
    }

    /// <summary>
    /// The checks that touch the world (<see cref="StatusProbes"/>). Every one of them answers with a
    /// row rather than an exception, so a repo with no token and no network still gets a full report.
    /// </summary>
    private static async Task<IReadOnlyList<StatusCheck>> ProbeAsync(
        DocumeConfig config,
        string repoRoot,
        bool offline,
        CancellationToken cancellationToken)
    {
        var credentials = StatusProbes.Credentials();
        var node = await StatusProbes.NodeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var renderer = StatusProbes.Renderer(Path.GetFullPath(Path.Combine(repoRoot, config.Mermaid.Renderer)));
        var confluence = await ConfluenceAsync(config, credentials, offline, cancellationToken)
            .ConfigureAwait(false);

        return [credentials, node, renderer, confluence];
    }

    /// <summary>
    /// §6.6's "token valid? space reachable?", or the reason it was not asked.
    /// </summary>
    /// <remarks>
    /// Skipped rather than attempted when the credential variables are missing: a status command that
    /// hung or threw on a repo with no token would fail in exactly the situation it exists for, and
    /// "not checked" is the honest answer.
    /// </remarks>
    private static async Task<StatusCheck> ConfluenceAsync(
        DocumeConfig config,
        StatusCheck credentials,
        bool offline,
        CancellationToken cancellationToken)
    {
        if (offline)
        {
            return StatusProbes.NotChecked(
                StatusProbes.ConfluenceCheck, "--offline: nothing was read from Confluence.");
        }

        if (credentials.Outcome != StatusCheckOutcome.Ok)
        {
            const string noCredentials =
                "no credentials in the environment, so the token and the space were not probed. See the "
                + "credentials row above.";

            return StatusProbes.NotChecked(StatusProbes.ConfluenceCheck, noCredentials);
        }

        if (config.Confluence.SpaceKey is not { Length: > 0 } spaceKey)
        {
            const string noSpace =
                "confluence.spaceKey is not set in docume.json, so there is no space to probe (§5.1).";

            return new StatusCheck(StatusProbes.ConfluenceCheck, StatusCheckOutcome.Problem, noSpace);
        }

        if (!Uri.TryCreate(config.Confluence.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            var badUrl = $"confluence.baseUrl '{config.Confluence.BaseUrl}' is not an absolute URL. It "
                + "should look like https://your-site.atlassian.net/wiki (§5.1).";

            return new StatusCheck(StatusProbes.ConfluenceCheck, StatusCheckOutcome.Problem, badUrl);
        }

        // A tighter budget than a publish gets: one lookup that a human is waiting on should give up
        // quickly, where a bulk publish is right to sit through a rate limit.
        var options = new ConfluenceClientOptions
        {
            BaseUrl = baseUrl,
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.FromSeconds(1),
            Timeout = TimeSpan.FromSeconds(15),
        };

        // Safe because the credentials row above is Ok, which means both variables are set.
        using var client = ConfluenceClient.Create(options, ConfluenceCredentials.FromEnvironment());

        return await StatusProbes.SpaceAsync(client, spaceKey, cancellationToken).ConfigureAwait(false);
    }

    private static void Render(StatusReport report)
    {
        AnsiConsole.MarkupLine($"Config:    [blue]{report.ConfigPath.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"Wiki root: [blue]{report.WikiRoot.EscapeMarkup()}[/]");

        var stateSuffix = report.StateFileExists ? string.Empty : " [grey](not found)[/]";
        AnsiConsole.MarkupLine($"State:     [blue]{report.StatePath.EscapeMarkup()}[/]{stateSuffix}");
        AnsiConsole.MarkupLine($"Space:     [blue]{(report.SpaceKey ?? "?").EscapeMarkup()}[/]");
        RenderShas(report);

        RenderChecks(report);
        RenderPages(report);
        RenderCoverage(report);
        RenderOrphans(report);
        RenderFailures(report);
        RenderGaps(report);
        RenderVerdict(report);
    }

    private static void RenderShas(StatusReport report)
    {
        if (report.BaselineSha is { Length: > 0 } baseline)
        {
            AnsiConsole.MarkupLine($"Baseline:  [grey]{baseline.EscapeMarkup()}[/]");
        }

        if (report.LastPublishedSha is { Length: > 0 } published)
        {
            AnsiConsole.MarkupLine($"Published: [grey]{published.EscapeMarkup()}[/]");
        }
    }

    private static void RenderChecks(StatusReport report)
    {
        AnsiConsole.WriteLine();

        var table = new Table().AddColumn("Check").AddColumn(string.Empty).AddColumn("Detail");

        foreach (var check in report.Checks)
        {
            var detail = string.Equals(check.Name, StatusModel.StructureCheck, StringComparison.Ordinal)
                ? StructureDetail(check.Detail, report.Structure)
                : check.Detail.EscapeMarkup();

            table.AddRow(check.Name.EscapeMarkup(), Verdict(check.Outcome), detail);
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// The structure check's detail, with one line per finding under the summary
    /// (<c>docs/specs/2026-09-02-wiki-structure.md</c> §3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every finding, never the first N.</strong> The AurServices tree had fifteen of them, and a
    /// list that quietly stopped at five would read as a complete inventory when it was not — the same
    /// rule the per-page table follows.
    /// </para>
    /// <para>
    /// Each line names the file to create. That is the intervention: the fix was seventeen
    /// <c>README.md</c> files, and the reason nobody wrote them is that nothing ever asked for them by
    /// name.
    /// </para>
    /// </remarks>
    private static string StructureDetail(string summary, StructureReport? structure)
    {
        var lines = new List<string> { summary.EscapeMarkup() };

        foreach (var directory in structure?.OrphanedDirectories ?? [])
        {
            var where = directory.Directory.Length == 0 ? "<wiki root>" : directory.Directory;
            var under = directory.ResolvedParent ?? "<space root>";

            lines.Add(
                $"{where.EscapeMarkup()} ({directory.PageCount} page{(directory.PageCount == 1 ? string.Empty : "s")}) "
                + $"→ filed under {under.EscapeMarkup()}; create {directory.IndexPath.EscapeMarkup()}");
        }

        foreach (var parent in structure?.WideParents ?? [])
        {
            var who = parent.Parent ?? "<space root>";

            lines.Add($"{who.EscapeMarkup()} has {parent.ChildCount} children");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The per-page table (§6.6, §6.5's table). Every page, uncapped: a status report that quietly
    /// listed the first N pages would read as a complete inventory when it was not.
    /// </summary>
    /// <remarks>
    /// The approver and date columns appear only once some page has one. They are §6.5 columns that
    /// nothing populates until §6.3's label sync lands, and two permanently empty columns on every run
    /// would cost width to say nothing — <see cref="StatusReport.NotYetAvailable"/> says it once instead.
    /// The same goes for staleness, whose count is in the coverage line either way.
    /// </remarks>
    private static void RenderPages(StatusReport report)
    {
        if (report.Pages.Count == 0)
        {
            return;
        }

        var showApprover = report.Pages.Any(page => page.ApprovedBy is not null || page.ApprovedAt is not null);
        var showStale = report.StaleCount > 0;

        var table = new Table()
            .AddColumn("Page")
            .AddColumn("Sync")
            .AddColumn("Ver", column => column.RightAligned())
            .AddColumn("Att", column => column.RightAligned())
            .AddColumn("Approval");

        if (showApprover)
        {
            table.AddColumn("Approved by").AddColumn("Approved");
        }

        if (showStale)
        {
            table.AddColumn("Stale");
        }

        table.AddColumn("Confluence");

        foreach (var page in report.Pages)
        {
            var cells = new List<string>
            {
                page.Path.EscapeMarkup(),
                Sync(page.Sync),
                Number(page.PublishedVersion),
                $"{page.AttachmentCount}",
                Approval(page),
            };

            if (showApprover)
            {
                cells.Add(Text(page.ApprovedBy));
                cells.Add(Text(page.ApprovedAt));
            }

            if (showStale)
            {
                cells.Add(page.Stale ? "[yellow]stale[/]" : "[grey]—[/]");
            }

            cells.Add(Link(page));

            table.AddRow([.. cells]);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
    }

    private static void RenderCoverage(StatusReport report)
    {
        var percent = report.ApprovedPercent is { } value ? $" ({value}%)" : string.Empty;

        AnsiConsole.MarkupLine(
            $"Pages:     {report.PageCount} — [grey]{report.InSyncCount} in sync[/], "
            + $"[yellow]{report.DriftedCount} drifted[/], [green]{report.UnpublishedCount} unpublished[/], "
            + $"[aqua]{report.MovedCount} moved[/], [blue]{report.AttachmentsChangedCount} attachment "
            + "change(s)[/]");

        AnsiConsole.MarkupLine(
            $"Approvals: [green]{report.ApprovedCount} approved[/]{percent}, "
            + $"[yellow]{report.NeedsReviewCount} needs-review[/], "
            + $"[grey]{report.UnrecordedApprovalCount} with no record[/]");

        AnsiConsole.MarkupLine(
            $"Stale:     {report.StaleCount} [grey](as recorded in state; `docume drift --mark` is what "
            + "sets it, §6.4)[/]");
    }

    private static void RenderOrphans(StatusReport report)
    {
        if (report.Orphans.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]ORPHANS[/] — {report.Orphans.Count} state entr(ies) whose markdown file is gone; "
            + "still pages in Confluence until `docume publish --prune` deletes them");

        foreach (var path in report.Orphans)
        {
            AnsiConsole.MarkupLine($"  [grey]{path.EscapeMarkup()}[/]");
        }
    }

    private static void RenderFailures(StatusReport report)
    {
        if (report.Failures.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[red]PAGES THE CONVERTER REFUSES[/] — {report.Failures.Count}. No page publishes until every "
            + "page converts.");

        foreach (var failure in report.Failures)
        {
            AnsiConsole.MarkupLine($"  [red]•[/] {failure.Path.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"    [grey]{failure.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// What §6.5's dashboard shows that this build cannot compute yet
    /// (<see cref="StatusReport.NotYetAvailable"/>). Printed rather than left implicit: a missing column
    /// a reader has to notice is how a report gets trusted for something it never measured.
    /// </summary>
    private static void RenderGaps(StatusReport report)
    {
        if (report.NotYetAvailable.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]NOT REPORTED YET[/]");

        foreach (var gap in report.NotYetAvailable)
        {
            AnsiConsole.MarkupLine($"  [grey]• {gap.EscapeMarkup()}[/]");
        }
    }

    private static void RenderVerdict(StatusReport report)
    {
        AnsiConsole.WriteLine();

        if (report.WorstCheck == StatusCheckOutcome.Problem)
        {
            var problems = report.Checks.Count(check => check.Outcome == StatusCheckOutcome.Problem);
            AnsiConsole.MarkupLine(
                $"[red]PROBLEMS[/] — {problems} check(s) would stop a publish. Read the check table above.");
            return;
        }

        if (report.HasDrift)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]DRIFT[/] — {report.PageCount - report.InSyncCount} page(s) and "
                + $"{report.Orphans.Count} orphan(s) differ from what is published. "
                + "`docume publish --dry-run` shows exactly what a run would do.");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[green]IN SYNC[/] — {report.PageCount} page(s) published and unchanged. Nothing was written.");
    }

    private static string Verdict(StatusCheckOutcome outcome) => outcome switch
    {
        StatusCheckOutcome.Ok => "[green]ok[/]",
        StatusCheckOutcome.Warning => "[yellow]warn[/]",
        StatusCheckOutcome.Problem => "[red]problem[/]",
        _ => "[grey]not checked[/]",
    };

    private static string Sync(StatusSync sync) => sync switch
    {
        StatusSync.Unpublished => "[green]unpublished[/]",
        StatusSync.Drifted => "[yellow]drifted[/]",
        StatusSync.AttachmentsChanged => "[blue]attachments[/]",
        StatusSync.Moved => "[aqua]moved[/]",
        _ => "[grey]in sync[/]",
    };

    private static string Approval(StatusPage page)
    {
        if (page.Approval is not { Length: > 0 } status)
        {
            return "[grey]—[/]";
        }

        var version = page.ApprovedVersion is { } approved && approved != page.PublishedVersion
            ? $" [grey](v{approved})[/]"
            : string.Empty;

        return string.Equals(status, ApprovalStatus.Approved, StringComparison.Ordinal)
            ? $"[green]approved[/]{version}"
            : $"[yellow]{status.EscapeMarkup()}[/]{version}";
    }

    /// <summary>
    /// The page id as a terminal hyperlink when the config yields a URL, so a reader can open the page
    /// without copying an id into a search box.
    /// </summary>
    private static string Link(StatusPage page)
    {
        if (page.PageId is not { Length: > 0 } id)
        {
            return "[grey]—[/]";
        }

        return page.Url is { Length: > 0 } url
            ? $"[link={url.EscapeMarkup()}]{id.EscapeMarkup()}[/]"
            : $"[grey]{id.EscapeMarkup()}[/]";
    }

    private static string Number(int? value) => value is { } number ? $"{number}" : "[grey]—[/]";

    private static string Text(string? value) =>
        value is { Length: > 0 } text ? text.EscapeMarkup() : "[grey]—[/]";

    /// <summary>
    /// The tree's own errors, which stand in for the whole report when the tree cannot be loaded.
    /// </summary>
    private static int FailTree(WikiTreeException ex, bool json)
    {
        if (json)
        {
            Console.Error.WriteLine(
                $"The wiki tree cannot be read ({ex.Errors.Count} error(s)):");
            foreach (var error in ex.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[red]The wiki tree cannot be read, so there is nothing to compare against Confluence "
            + $"({ex.Errors.Count} error(s)):[/]");

        foreach (var error in ex.Errors)
        {
            AnsiConsole.MarkupLine($"  [red]•[/] {error.EscapeMarkup()}");
        }

        return 1;
    }

    /// <summary>
    /// An unusable input, which is a broken invocation rather than a status finding. In
    /// <c>--json</c> mode it goes to stderr, so a caller redirecting stdout still gets parseable JSON or
    /// an empty file with a non-zero exit — never prose where a document was promised.
    /// </summary>
    private static int Fail(string message, bool json)
    {
        if (json)
        {
            Console.Error.WriteLine(message);
            return 1;
        }

        AnsiConsole.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

        return 1;
    }
}
