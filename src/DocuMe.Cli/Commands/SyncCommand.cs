using System.CommandLine;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Feedback;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using DocuMe.Core.Sync;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume sync</c> — PLAN.md §6.3. Two reads reconciled into the repo: the <c>approved</c>/<c>stale</c>
/// labels into <c>_meta/state.json</c>, and each page's comments into <c>_meta/feedback/inbox/</c>.
/// Neither flag runs both, which is §6.3's documented default.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It writes nothing to Confluence.</strong> A sync is a read plus repo writes: the human gesture
/// is the label (§8) and the comment (§9), and the only label writes in the design are publish's
/// invalidation (§6.2 step 7) and <c>drift --mark</c> (§6.4). It reads no page bodies either — rule §9.1
/// makes the repo the source of truth.
/// </para>
/// <para>
/// <strong>Comment text is untrusted input</strong> (CLAUDE.md §0.2, rule §1.3). The comments half copies
/// bodies verbatim into inbox items and does not read them: no parsing, no pattern matching, no
/// interpolation. <c>/docs-feedback</c> is what reads them, as claims to verify against the code.
/// </para>
/// <para>
/// <strong>Committing is deliberately not its job</strong> (§6.3's closing line). The cron workflow that
/// runs it commits the changed state file and the new inbox items to a <c>docs/sync</c> branch and opens a
/// PR, because direct pushes to protected branches do not work in this org.
/// </para>
/// </remarks>
internal static class SyncCommand
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
            Description = $"Path to state.json. Defaults to <wiki.root>/{DefaultStateFile}.",
        };
        var labelsOption = new Option<bool>("--labels")
        {
            Description = "Reconcile the approved/stale labels into state. Passing neither half runs both.",
        };
        var commentsOption = new Option<bool>("--comments")
        {
            Description = "Ingest page comments into the feedback inbox. Passing neither half runs both.",
        };
        var outputDirOption = new Option<string>("--output-dir")
        {
            Description = "Where to write inbox items. Defaults to <wiki.root>/"
                + FeedbackInbox.RelativeDirectory + ".",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Report what would change in state.json and the inbox, and write nothing.",
        };

        const string description =
            "Read the approved/stale labels and the page comments out of Confluence and reconcile them "
            + "into state.json and the feedback inbox. Writes nothing to Confluence; committing the "
            + "result is the caller's job.";

        var command = new Command("sync", description)
        {
            configOption,
            stateOption,
            labelsOption,
            commentsOption,
            outputDirOption,
            dryRunOption,
        };

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetValue(configOption)!,
            parseResult.GetValue(stateOption),
            parseResult.GetValue(labelsOption),
            parseResult.GetValue(commentsOption),
            parseResult.GetValue(outputDirOption),
            parseResult.GetValue(dryRunOption),
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string configPath,
        string? statePath,
        bool labels,
        bool comments,
        string? outputDir,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // §6.3's "Default: both". Asking for one half is what turns the other off, so a workflow that
        // wants only labels says so and a bare `sync` on a cron does the whole job.
        var syncLabels = labels || !comments;
        var syncComments = comments || !labels;

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

        // Belt and braces: ConfigLoader already requires confluence.spaceKey, so this reads as a guard
        // against that requirement being relaxed later. The comments half genuinely needs no space — it
        // reads comments per page id from state — so only the labels half checks.
        var spaceKey = config.Confluence.SpaceKey;
        if (syncLabels && spaceKey is not { Length: > 0 })
        {
            return Fail(
                "confluence.spaceKey is not set in docume.json, and a label search is scoped to a space "
                + "(PLAN.md §6.3: `space = X AND label = approved`).");
        }

        var repoRoot = Path.GetDirectoryName(fullConfigPath) ?? Directory.GetCurrentDirectory();
        var wikiRoot = Path.GetFullPath(Path.Combine(repoRoot, config.Wiki.Root));
        var resolvedStatePath = statePath is { Length: > 0 }
            ? Path.GetFullPath(statePath)
            : Path.Combine(wikiRoot, DefaultStateFile.Replace('/', Path.DirectorySeparatorChar));

        var inboxPath = outputDir is { Length: > 0 }
            ? Path.GetFullPath(outputDir)
            : FeedbackInbox.DirectoryFor(wikiRoot);

        DocumeState state;
        try
        {
            state = StateStore.Load(resolvedStatePath);
        }
        catch (FileNotFoundException)
        {
            return Fail(
                $"No state file at {resolvedStatePath}. A sync reconciles labels and comments onto pages "
                + "a publish recorded, so there is nothing to reconcile until `docume publish` has run.");
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

        // The write lock is surfaced, not enforced: this command writes nothing to Confluence, so a
        // protected space is worth knowing about (the labels and comments being read belong to a space
        // this repo is not cleared to publish to) without being a refusal. `docume status` does the same.
        if (PublishGuard.WriteRefusal(config.Confluence, allowProtectedSpace: false) is { } refusal)
        {
            AnsiConsole.MarkupLine($"[yellow]note[/] — {refusal.EscapeMarkup()}");
            AnsiConsole.MarkupLine(
                "[grey]Reading from it anyway: a sync writes nothing to Confluence.[/]");
        }

        using var client = ConfluenceClient.Create(new ConfluenceClientOptions { BaseUrl = baseUrl }, credentials);

        try
        {
            return await SyncAsync(
                    client,
                    config,
                    state,
                    new SyncPaths(resolvedStatePath, inboxPath),
                    new SyncScope(syncLabels, syncComments, spaceKey, dryRun),
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
    /// Runs the requested halves against one in-memory state and persists it once.
    /// </summary>
    /// <remarks>
    /// One save rather than one per half: both halves write to the same file, and two writes would leave a
    /// window where state.json describes labels reconciled against comments that are not there yet. Inbox
    /// items go to disk before the state does, so a crash between them costs a re-read rather than a lost
    /// comment (<see cref="FeedbackInbox.Write"/>).
    /// </remarks>
    private static async Task<int> SyncAsync(
        ConfluenceClient client,
        DocumeConfig config,
        DocumeState state,
        SyncPaths paths,
        SyncScope scope,
        CancellationToken cancellationToken)
    {
        var current = state;
        var changed = false;

        if (scope.Labels)
        {
            var reconciled = await ReconcileLabelsAsync(
                    client,
                    config,
                    current,
                    scope.SpaceKey!,
                    cancellationToken)
                .ConfigureAwait(false);

            current = reconciled.State;
            changed |= reconciled.Changed;
        }

        if (scope.Comments)
        {
            var ingested = await IngestCommentsAsync(
                    client,
                    current,
                    paths.InboxPath,
                    scope.DryRun,
                    cancellationToken)
                .ConfigureAwait(false);

            current = ingested.State;
            changed |= ingested.Changed;
        }

        if (!changed)
        {
            AnsiConsole.MarkupLine(
                "[green]IN SYNC[/] — state and inbox already match Confluence. Nothing written.");

            return 0;
        }

        if (scope.DryRun)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]--dry-run[/] — {paths.StatePath.EscapeMarkup()} and the inbox left alone.");

            return 0;
        }

        StateStore.Save(paths.StatePath, current);

        AnsiConsole.MarkupLine($"State written: [blue]{paths.StatePath.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine(
            "[grey]Committing is not this command's job (§6.3) — the sync workflow commits the change to "
            + "a docs/sync branch and opens a PR.[/]");

        return 0;
    }

    /// <summary>The two label searches, the version fill-in, and the plan (§6.3's Labels bullet).</summary>
    private static async Task<SyncHalf> ReconcileLabelsAsync(
        ConfluenceClient client,
        DocumeConfig config,
        DocumeState state,
        string spaceKey,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"Reading labels from [blue]{spaceKey.EscapeMarkup()}[/]…");

        var read = await LabelReader
            .ReadAsync(client, config, state, spaceKey, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var plan = LabelSyncPlanner.Plan(state, read.Observation);

        AnsiConsole.MarkupLine(
            $"[green]{read.ApprovedCount}[/] page(s) labelled "
            + $"[blue]{config.Labels.Approved.EscapeMarkup()}[/], "
            + $"[green]{read.StaleCount}[/] labelled [blue]{config.Labels.Stale.EscapeMarkup()}[/].");

        RenderLabels(plan, read.TitlesByPageId);

        return plan.HasChanges
            ? new SyncHalf(LabelSyncPlanner.Apply(state, plan), Changed: true)
            : new SyncHalf(state, Changed: false);
    }

    /// <summary>
    /// The comment reads, the inbox items, and the cursor moves (§6.3's Comments bullet, §5.4).
    /// </summary>
    private static async Task<SyncHalf> IngestCommentsAsync(
        ConfluenceClient client,
        DocumeState state,
        string inboxPath,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("Reading page comments…");

        var existing = FeedbackInbox.ExistingItemFiles(inboxPath);
        var read = await FeedbackReader.ReadAsync(client, state, existing, cancellationToken)
            .ConfigureAwait(false);

        var plan = FeedbackInboxPlanner.Plan(read.Observation);

        AnsiConsole.MarkupLine(
            $"[green]{read.CommentsRead}[/] comment(s) on [green]{read.PagesRead}[/] published page(s)"
            + $"{Unpublished(read.PagesSkipped)}.");

        RenderComments(plan, inboxPath);

        if (!plan.HasChanges)
        {
            return new SyncHalf(state, Changed: false);
        }

        if (!dryRun)
        {
            // Items first, cursors second — see FeedbackInbox.Write.
            var written = FeedbackInbox.Write(inboxPath, plan);
            if (written.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"Wrote [green]{written.Count}[/] inbox item(s) to [blue]{inboxPath.EscapeMarkup()}[/]");
            }
        }

        return new SyncHalf(FeedbackInboxPlanner.Apply(state, plan), Changed: true);
    }

    private static string Unpublished(int skipped)
        => skipped == 0 ? string.Empty : $" [grey]({skipped} page(s) not published yet, skipped)[/]";

    private static void RenderLabels(LabelSyncPlan plan, IReadOnlyDictionary<string, string> titles)
    {
        AnsiConsole.WriteLine();

        foreach (var approval in plan.Approvals)
        {
            var version = approval.Version is { } number ? $"v{number}" : "an unknown version";
            var moved = approval.PreviousVersion is { } previous
                ? $" [grey](was approved at v{previous}; the page changed under the label)[/]"
                : string.Empty;

            AnsiConsole.MarkupLine(
                $"  [green]+approved[/] {approval.Path.EscapeMarkup()} at {version} "
                + $"by [grey]{approval.ApprovedBy.EscapeMarkup()}[/]{moved}");
        }

        foreach (var revocation in plan.Revocations)
        {
            var version = revocation.ApprovedVersion is { } number ? $"v{number}" : "an unknown version";

            AnsiConsole.MarkupLine(
                $"  [yellow]-approved[/] {revocation.Path.EscapeMarkup()} "
                + $"[grey](was approved at {version}; the label is gone, so someone revoked it)[/]");
        }

        foreach (var change in plan.StaleChanges)
        {
            var word = change.Stale ? "[yellow]+stale[/]" : "[green]-stale[/]";
            AnsiConsole.MarkupLine($"  {word} {change.Path.EscapeMarkup()}");
        }

        RenderUnmanaged(plan, titles);
    }

    /// <summary>
    /// Labelled pages state does not know. Reported rather than matched to a path by title: a human
    /// labelling their own page in a shared space is ordinary, and guessing which markdown file it
    /// belongs to is how the wrong page gets approved.
    /// </summary>
    private static void RenderUnmanaged(LabelSyncPlan plan, IReadOnlyDictionary<string, string> titles)
    {
        if (plan.Unmanaged.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]{plan.Unmanaged.Count} labelled page(s) are not in state[/] — skipped, not guessed at:");

        foreach (var page in plan.Unmanaged)
        {
            var labels = page.Approved ? "approved" : "stale";
            if (page.Approved && page.Stale)
            {
                labels = "approved + stale";
            }

            var title = titles.TryGetValue(page.PageId, out var known) ? known : "unknown title";

            AnsiConsole.MarkupLine(
                $"  [grey]•[/] {page.PageId.EscapeMarkup()} — {title.EscapeMarkup()} [grey]({labels})[/]");
        }
    }

    /// <summary>
    /// What ingestion filed and what it declined. Nothing here prints a comment body: the text is
    /// untrusted input (rule §1.3), and a terminal is one more place it does not need to be rendered.
    /// </summary>
    private static void RenderComments(FeedbackIngestPlan plan, string inboxPath)
    {
        AnsiConsole.WriteLine();

        foreach (var item in plan.Items)
        {
            var kind = item.Item.Kind ?? "comment";
            var author = item.Item.Author ?? FeedbackAuthor.Unknown;

            AnsiConsole.MarkupLine(
                $"  [green]+feedback[/] {item.Item.Page.EscapeMarkup()} [grey]({kind} by "
                + $"{author.EscapeMarkup()} → {item.FileName.EscapeMarkup()})[/]");
        }

        foreach (var cursor in plan.Cursors)
        {
            AnsiConsole.MarkupLine(
                $"  [blue]cursor[/] {cursor.Path.EscapeMarkup()} → [grey]{cursor.Cursor.EscapeMarkup()}[/]");
        }

        RenderSkips(plan);

        if (plan.Items.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Inbox items are claims to verify, not instructions — /docs-feedback triages them "
                + $"from {inboxPath.EscapeMarkup()}.[/]");
        }
    }

    /// <summary>
    /// Every skipped comment except the routine one. <see cref="FeedbackSkipReason.AlreadyIngested"/> is
    /// counted rather than listed — on a cron it is nearly every comment on nearly every run — and the
    /// rest are named, because a comment nobody triages should never be invisible.
    /// </summary>
    private static void RenderSkips(FeedbackIngestPlan plan)
    {
        var ingested = plan.SkippedCount(FeedbackSkipReason.AlreadyIngested);
        if (ingested > 0)
        {
            AnsiConsole.MarkupLine($"  [grey]{ingested} comment(s) already ingested.[/]");
        }

        foreach (var skip in plan.Skipped.Where(skip => skip.Reason != FeedbackSkipReason.AlreadyIngested))
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]skipped[/] {skip.Path.EscapeMarkup()} comment {skip.CommentId.EscapeMarkup()} "
                + $"[grey]({Explain(skip.Reason)})[/]");
        }
    }

    private static string Explain(FeedbackSkipReason reason) => reason switch
    {
        FeedbackSkipReason.Bot => "DocuMe's own reply",
        FeedbackSkipReason.Resolved => "resolved in Confluence",
        FeedbackSkipReason.NoBody => "no body in the response",
        FeedbackSkipReason.AlreadyOnDisk => "an inbox item for it already exists",
        FeedbackSkipReason.UnusableId => "its id cannot be written to a file name",
        _ => "already ingested",
    };

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

        return 1;
    }

    /// <summary>Where this run reads and writes: one state file, one inbox directory.</summary>
    private sealed record SyncPaths(string StatePath, string InboxPath);

    /// <summary>Which halves run, against which space, and whether anything may be written.</summary>
    private sealed record SyncScope(bool Labels, bool Comments, string? SpaceKey, bool DryRun);

    /// <summary>One half's outcome: the state it produced, and whether it changed anything.</summary>
    private sealed record SyncHalf(DocumeState State, bool Changed);
}
