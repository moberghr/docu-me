using System.CommandLine;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Dashboard;
using DocuMe.Core.Markdown;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using DocuMe.Core.Status;
using DocuMe.Core.Sync;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume dashboard</c> — PLAN.md §6.5. Regenerates the "Documentation Status" page in Confluence
/// from the state file plus the live labels: coverage stats, a row per page, and the marker legend.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The one command in M3 that writes to Confluence.</strong> <c>sync</c> and <c>status</c> read
/// and then write files, so a protected space is a note to them; here it is a refusal. The space lock
/// (CLAUDE.md §0.1, rule §1.4) guards writes, and this is a write.
/// </para>
/// <para>
/// <strong>It does not write the state file.</strong> The labels are read and reconciled
/// (<see cref="LabelSyncPlanner"/>) so the page agrees with Confluence as of this second even when the
/// cron <c>sync</c> has not committed yet — but the reconciled state stays in memory. §6.3 owns that
/// file; a second writer of the same field, running on a different schedule, is how two runs end up
/// disagreeing about who approved what.
/// </para>
/// <para>
/// <strong>The dashboard page is deliberately not recorded in <c>state.pages</c>.</strong> It is
/// machine-owned and has no markdown source, so a state entry would make it an orphan on the next
/// publish — and <c>publish --prune</c> deletes orphans (rule §9.6). It is found by title on every run
/// instead, which costs one read and cannot delete anything.
/// </para>
/// <para>
/// <strong>§6.5 says "full overwrite each run", and it is — with one deviation.</strong> When the
/// rendered body is byte-identical to the live one the update is skipped, because §6.4 refreshes this
/// page on every <c>drift --mark</c> and a version per run would bury its real history under
/// no-op revisions. Same bytes on the page either way; only the version counter differs.
/// </para>
/// </remarks>
internal static class DashboardCommand
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
            Description = $"Path to state.json. Defaults to <wiki.root>/{DefaultStateFile}. Read only — "
                + "the labels are reconciled in memory, and `docume sync --labels` is what writes them.",
        };
        var titleOption = new Option<string>("--title")
        {
            Description = "Title of the dashboard page. Defaults to dashboard.title in docume.json.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the storage format the run would publish and write nothing.",
        };
        var allowProtectedSpaceOption = new Option<bool>("--allow-protected-space")
        {
            Description = "Publish even though confluence.spaceKey is listed in "
                + "confluence.protectedSpaces. One run only; the list stays as it is.",
        };

        const string description =
            "Regenerate the Documentation Status page in Confluence from state plus the live labels. "
            + "The only M3 command that writes to Confluence.";

        var command = new Command("dashboard", description)
        {
            configOption,
            stateOption,
            titleOption,
            dryRunOption,
            allowProtectedSpaceOption,
        };

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetValue(configOption)!,
            parseResult.GetValue(stateOption),
            parseResult.GetValue(titleOption),
            parseResult.GetValue(dryRunOption),
            parseResult.GetValue(allowProtectedSpaceOption),
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string configPath,
        string? statePath,
        string? title,
        bool dryRun,
        bool allowProtectedSpace,
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
            return Fail(ex.Message);
        }
        catch (ConfigValidationException ex)
        {
            return Fail(ex.Message);
        }
        catch (JsonException ex)
        {
            return Fail($"{fullConfigPath} is not valid JSON: {ex.Message}");
        }

        if (config.Confluence.SpaceKey is not { Length: > 0 } spaceKey)
        {
            return Fail(
                "confluence.spaceKey is not set in docume.json, and both halves of this command need it: "
                + "a label search is scoped to a space, and so is the page the dashboard lands on (§6.5).");
        }

        var pageTitle = title is { Length: > 0 } ? title : config.Dashboard.Title;
        if (pageTitle is not { Length: > 0 })
        {
            return Fail(
                "dashboard.title is empty in docume.json and no --title was passed. The page is found by "
                + "title on every run (§6.5), so there is nothing to find or create.");
        }

        // The refusal happens before any request: a run that read the whole space and only then declined
        // to write would spend a rate-limit budget to learn what the config already said.
        var refusal = PublishGuard.WriteRefusal(config.Confluence, allowProtectedSpace);
        if (refusal is not null && !dryRun)
        {
            return Fail(refusal);
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
            return Fail(ex.Message);
        }
        catch (WikiTreeException ex)
        {
            return FailTree(ex);
        }

        var stateExists = File.Exists(resolvedStatePath);

        DocumeState state;
        try
        {
            state = stateExists ? StateStore.Load(resolvedStatePath) : new DocumeState();
        }
        catch (StateVersionException ex)
        {
            return Fail(ex.Message);
        }
        catch (JsonException ex)
        {
            return Fail($"{resolvedStatePath} is not valid JSON: {ex.Message}");
        }

        ConfluenceCredentials credentials;
        try
        {
            credentials = ConfluenceCredentials.FromEnvironment();
        }
        catch (ConfluenceCredentialsException ex)
        {
            return Fail(ex.Message);
        }

        if (!Uri.TryCreate(config.Confluence.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            return Fail(
                $"confluence.baseUrl '{config.Confluence.BaseUrl}' is not an absolute URL. It should look "
                + "like https://your-site.atlassian.net/wiki (PLAN.md §5.1).");
        }

        if (refusal is not null)
        {
            AnsiConsole.MarkupLine($"[yellow]note[/] — {refusal.EscapeMarkup()}");
            AnsiConsole.MarkupLine("[grey]--dry-run, so the plan is rendered anyway; a real run refuses.[/]");
        }

        var paths = new StatusPaths
        {
            ConfigPath = fullConfigPath,
            WikiRoot = wikiRoot,
            StatePath = resolvedStatePath,
            StateFileExists = stateExists,
        };

        using var client = ConfluenceClient.Create(new ConfluenceClientOptions { BaseUrl = baseUrl }, credentials);

        try
        {
            return await PublishAsync(
                    client,
                    config,
                    paths,
                    tree,
                    state,
                    spaceKey,
                    pageTitle,
                    dryRun,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConfluenceException ex)
        {
            return Fail(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Confluence is unreachable at {baseUrl}: {ex.Message}");
        }
    }

    /// <summary>
    /// The label read, the in-memory reconcile, the render, and the one upsert.
    /// </summary>
    private static async Task<int> PublishAsync(
        ConfluenceClient client,
        DocumeConfig config,
        StatusPaths paths,
        WikiTree tree,
        DocumeState state,
        string spaceKey,
        string pageTitle,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"Reading labels from [blue]{spaceKey.EscapeMarkup()}[/]…");

        var observedAt = DateTimeOffset.UtcNow;
        var read = await LabelReader
            .ReadAsync(client, config, state, spaceKey, observedAt, cancellationToken)
            .ConfigureAwait(false);

        var plan = LabelSyncPlanner.Plan(state, read.Observation);
        var reconciled = LabelSyncPlanner.Apply(state, plan);

        AnsiConsole.MarkupLine(
            $"[green]{read.ApprovedCount}[/] page(s) labelled "
            + $"[blue]{config.Labels.Approved.EscapeMarkup()}[/], "
            + $"[green]{read.StaleCount}[/] labelled [blue]{config.Labels.Stale.EscapeMarkup()}[/].");

        if (plan.HasChanges)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{plan.ChangeCount} label change(s) are reflected on the page but not written to "
                + $"{paths.StatePath.EscapeMarkup()} — `docume sync --labels` owns that file (§6.3).[/]");
        }

        var report = StatusModel.Build(paths, config, tree, reconciled);
        var body = new DashboardPage { Report = report, GeneratedAt = observedAt }.Render();

        RenderSummary(report);

        if (dryRun)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[yellow]--dry-run[/] — the storage format for [blue]{pageTitle.EscapeMarkup()}[/] "
                + "follows; nothing was written to Confluence.");
            AnsiConsole.WriteLine();

            // Console.WriteLine, not AnsiConsole: Spectre wraps to the terminal width, which would put
            // newlines inside the markup and hand a reader a body Confluence would reject.
            Console.WriteLine(body);

            return 0;
        }

        return await UpsertAsync(client, config, spaceKey, pageTitle, body, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Find-or-create by title, then overwrite (§6.5).
    /// </summary>
    /// <remarks>
    /// The read asks for the body so an unchanged page can be left alone — see the type's remarks on
    /// why that deviation from "full overwrite each run" is the right one. The comparison is ordinal
    /// over <see cref="DashboardPage.WithoutProvenance"/>: exact bytes, minus the one line carrying the
    /// run's own timestamp, which would otherwise make every run differ from every other. A body
    /// Confluence did not return at all compares against the empty string and so counts as changed.
    /// </remarks>
    private static async Task<int> UpsertAsync(
        ConfluenceClient client,
        DocumeConfig config,
        string spaceKey,
        string pageTitle,
        string body,
        CancellationToken cancellationToken)
    {
        var spaceId = await ResolveSpaceIdAsync(client, config.Confluence, spaceKey, cancellationToken)
            .ConfigureAwait(false);

        if (spaceId is null)
        {
            return Fail(
                $"Confluence has no space with key '{spaceKey}', or this account cannot see it. Check "
                + "confluence.spaceKey in docume.json (§5.1).");
        }

        var existing = await client
            .FindPageByTitleAsync(spaceId, pageTitle, includeBody: true, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var draft = new ConfluencePageDraft(spaceId, pageTitle, body, config.Confluence.RootPageId);
            var created = await client.CreatePageAsync(draft, cancellationToken).ConfigureAwait(false);

            AnsiConsole.MarkupLine(
                $"[green]CREATED[/] [blue]{pageTitle.EscapeMarkup()}[/] "
                + $"[grey](page {created.Id.EscapeMarkup()})[/]");

            return 0;
        }

        if (string.Equals(
                DashboardPage.WithoutProvenance(existing.Storage ?? string.Empty),
                DashboardPage.WithoutProvenance(body),
                StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine(
                $"[green]UNCHANGED[/] [blue]{pageTitle.EscapeMarkup()}[/] "
                + $"[grey](page {existing.Id.EscapeMarkup()}, v{existing.Version}) — the rendered body "
                + "matches what is published, so no version was spent.[/]");

            return 0;
        }

        var revision = new ConfluencePageRevision(
            existing.Id,
            pageTitle,
            body,
            existing.Version,
            VersionMessage: "docume dashboard");

        var updated = await client.UpdatePageAsync(revision, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"[green]UPDATED[/] [blue]{pageTitle.EscapeMarkup()}[/] "
            + $"[grey](page {updated.Id.EscapeMarkup()}, now v{updated.Version})[/]");

        return 0;
    }

    /// <summary>
    /// The numeric space id the v2 page endpoints want. A configured <c>confluence.spaceId</c> is
    /// trusted for the same reason <c>publish</c> trusts it: the config is committed and reviewed, and
    /// confirming it would cost a request per run to learn nothing.
    /// </summary>
    private static async Task<string?> ResolveSpaceIdAsync(
        ConfluenceClient client,
        ConfluenceConfig confluence,
        string spaceKey,
        CancellationToken cancellationToken)
    {
        if (confluence.SpaceId is { Length: > 0 } configured)
        {
            return configured;
        }

        var space = await client.FindSpaceByKeyAsync(spaceKey, cancellationToken).ConfigureAwait(false);

        return space?.Id;
    }

    /// <summary>
    /// The coverage numbers, in the terminal, before the write. What the page will say, said where the
    /// person who ran the command is looking.
    /// </summary>
    private static void RenderSummary(StatusReport report)
    {
        var percent = report.ApprovedPercent is { } value ? $" ({value}%)" : string.Empty;

        AnsiConsole.MarkupLine(
            $"Pages:     {report.PageCount} — [grey]{report.PublishedCount} published[/], "
            + $"[yellow]{report.PageCount - report.InSyncCount} differing from the repo[/]");

        AnsiConsole.MarkupLine(
            $"Approvals: [green]{report.ApprovedCount} approved[/]{percent}, "
            + $"[yellow]{report.NeedsReviewCount} needs-review[/], "
            + $"[grey]{report.UnrecordedApprovalCount} with no record[/]");

        AnsiConsole.MarkupLine($"Stale:     {report.StaleCount}");
    }

    /// <summary>
    /// The tree's own errors, which stand in for the whole page when the tree cannot be loaded. Nothing
    /// is published in that case: a dashboard rendered from half a tree would report pages as missing
    /// that are only unreadable.
    /// </summary>
    private static int FailTree(WikiTreeException ex)
    {
        AnsiConsole.MarkupLine(
            $"[red]The wiki tree cannot be read, so there is nothing to report on "
            + $"({ex.Errors.Count} error(s)):[/]");

        foreach (var error in ex.Errors)
        {
            AnsiConsole.MarkupLine($"  [red]•[/] {error.EscapeMarkup()}");
        }

        return 1;
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

        return 1;
    }
}
