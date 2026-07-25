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
/// <c>docume sync</c> — PLAN.md §6.3, plus §9 step 5. Two reads reconciled into the repo — the
/// <c>approved</c>/<c>stale</c> labels into <c>_meta/state.json</c> and each page's comments into
/// <c>_meta/feedback/inbox/</c> — and, only when asked, the replies that close the feedback loop.
/// Passing no flag runs the two reads, which is §6.3's documented default.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The two read halves write nothing to Confluence; <c>--reply</c> does.</strong> A sync is a
/// read plus repo writes: the human gesture is the label (§8) and the comment (§9). <c>--reply</c> is the
/// one exception and it is opt-in for that reason — it posts an answer under each triaged comment (§9
/// step 5) and closes the inline ones. Because it writes, it is the only half the protected-space lock
/// refuses outright (rule §1.4) rather than merely noting. No half reads page bodies: rule §9.1 makes the
/// repo the source of truth.
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
            Description = "Reconcile the approved/stale labels into state. Passing no half runs the two "
                + "read halves.",
        };
        var commentsOption = new Option<bool>("--comments")
        {
            Description = "Ingest page comments into the feedback inbox. Passing no half runs the two "
                + "read halves.",
        };
        var replyOption = new Option<bool>("--reply")
        {
            Description = "Post a reply under every triaged inbox item and resolve the inline comments "
                + "it answers. Writes to Confluence, so it never runs unless asked for.",
        };
        var outputDirOption = new Option<string>("--output-dir")
        {
            Description = "Where to write inbox items. Defaults to <wiki.root>/"
                + FeedbackInbox.RelativeDirectory + ".",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Report what would change in state.json and the inbox and what would be posted "
                + "to Confluence, and write none of it.",
        };

        const string description =
            "Read the approved/stale labels and the page comments out of Confluence and reconcile them "
            + "into state.json and the feedback inbox. Writes nothing to Confluence unless --reply is "
            + "passed; committing the result is the caller's job.";

        var command = new Command("sync", description)
        {
            configOption,
            stateOption,
            labelsOption,
            commentsOption,
            replyOption,
            outputDirOption,
            dryRunOption,
        };

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetValue(configOption)!,
            parseResult.GetValue(stateOption),
            new SyncHalves(
                parseResult.GetValue(labelsOption),
                parseResult.GetValue(commentsOption),
                parseResult.GetValue(replyOption)),
            parseResult.GetValue(outputDirOption),
            parseResult.GetValue(dryRunOption),
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string configPath,
        string? statePath,
        SyncHalves requested,
        string? outputDir,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // §6.3's "Default: both", extended by one rule rather than rewritten: naming any half selects
        // exactly the halves named, and naming none runs the two that only read. --reply is never in the
        // default set — a bare `sync` on a six-hourly cron must not post comments into Confluence.
        var syncLabels = requested.Labels || (!requested.Comments && !requested.Reply);
        var syncComments = requested.Comments || (!requested.Labels && !requested.Reply);
        var syncReply = requested.Reply;

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

        // The write lock is surfaced for the read halves and enforced for --reply. Reading labels and
        // comments out of a space this repo is not cleared to publish to is worth knowing about but is
        // not destructive, and `docume status` says it the same way. Posting a comment into that space
        // is a write, and the lock exists to stop writes (rule §1.4) — a refusal, not a note. --dry-run
        // is refused too: it would print a plan whose real run cannot happen.
        if (PublishGuard.WriteRefusal(config.Confluence, allowProtectedSpace: false) is { } refusal)
        {
            if (syncReply)
            {
                // Worded here rather than reusing the guard's message, which offers publish's per-run
                // --allow-protected-space override. `sync` deliberately has no such flag: a reply is a
                // comment posted into a space this repo was told to stay out of, and no single run of
                // that is worth waving through. The guard decides; this says what it means for --reply.
                return Fail(
                    $"confluence.spaceKey '{spaceKey}' is listed in confluence.protectedSpaces, and "
                    + "`sync --reply` posts comments into the space it is pointed at. Refused. There is "
                    + "no per-run override here — remove the entry from docume.json to go live. The "
                    + "--labels and --comments halves only read, and still work against it.");
            }

            AnsiConsole.MarkupLine($"[yellow]note[/] — {refusal.EscapeMarkup()}");
            AnsiConsole.MarkupLine(
                "[grey]Reading from it anyway: neither of these halves writes to Confluence.[/]");
        }

        using var client = ConfluenceClient.Create(new ConfluenceClientOptions { BaseUrl = baseUrl }, credentials);

        try
        {
            return await SyncAsync(
                    client,
                    config,
                    state,
                    new SyncPaths(resolvedStatePath, inboxPath, FeedbackInbox.ArchiveBeside(inboxPath)),
                    new SyncScope(new SyncHalves(syncLabels, syncComments, syncReply), spaceKey, dryRun),
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

        if (scope.Halves.Labels)
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

        if (scope.Halves.Comments)
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

        // Replies go last, after both reads: answering a comment before this run has finished reading
        // the comments would be replying to a conversation it has not caught up with. Its exit code is
        // held rather than returned, so a failed reply never costs the cursor moves the read half earned.
        var replyExit = 0;
        if (scope.Halves.Reply)
        {
            replyExit = await ReplyAsync(client, current, paths, scope.DryRun, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!changed)
        {
            if (!scope.Halves.Reply)
            {
                AnsiConsole.MarkupLine(
                    "[green]IN SYNC[/] — state and inbox already match Confluence. Nothing written.");
            }

            return replyExit;
        }

        if (scope.DryRun)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]--dry-run[/] — {paths.StatePath.EscapeMarkup()} and the inbox left alone.");

            return replyExit;
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

    /// <summary>
    /// The reply pass (§9 step 5): read the triaged items, post an answer under each comment, close the
    /// inline ones. The only half that writes to Confluence.
    /// </summary>
    /// <returns>0, or 1 when Confluence refused any part of the plan.</returns>
    private static async Task<int> ReplyAsync(
        ConfluenceClient client,
        DocumeState state,
        SyncPaths paths,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("Reading triaged feedback…");

        var directories = new[] { paths.InboxPath, paths.ArchivePath };
        var read = await FeedbackReplyReader.ReadAsync(client, state, directories, cancellationToken)
            .ConfigureAwait(false);

        var plan = FeedbackReplyPlanner.Plan(read.Observation);

        AnsiConsole.MarkupLine(
            $"[green]{read.ItemsRead}[/] item(s) in the inbox and archive; "
            + $"[green]{plan.Replies.Count}[/] awaiting a reply on [green]{read.PagesRead}[/] page(s)"
            + $"{Unreadable(read.PagesUnpublished)}.");

        RenderReplies(plan);

        if (!plan.HasChanges)
        {
            return 0;
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine(
                "[yellow]--dry-run[/] — nothing posted to Confluence and no item stamped.");

            return 0;
        }

        var result = await FeedbackReplyExecutor
            .ExecuteAsync(client, plan, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"Posted [green]{result.Posted}[/] repl(y/ies), resolved [green]{result.Resolved}[/] "
            + "inline comment(s).");

        return RenderReplyFailures(result);
    }

    /// <summary>
    /// What the reply pass would post and what it declined. Like the ingestion report, nothing here
    /// prints a comment body — and nothing prints the reply body either, which is composed from a fixed
    /// sentence plus the triage's own resolution text.
    /// </summary>
    private static void RenderReplies(FeedbackReplyPlan plan)
    {
        AnsiConsole.WriteLine();

        foreach (var reply in plan.Replies)
        {
            AnsiConsole.MarkupLine(
                $"  [green]reply[/] {reply.Page.EscapeMarkup()} [grey]({reply.Kind} comment "
                + $"{reply.CommentId.EscapeMarkup()}, {reply.Status.EscapeMarkup()})[/]"
                + Closing(reply.Resolve));
        }

        RenderReplySkips(plan);
    }

    /// <summary>
    /// Why a comment is or is not closed after the reply. Spelled out per reply rather than counted,
    /// because "answered but still showing as open" is the state a human has to finish by hand.
    /// </summary>
    private static string Closing(ReplyResolvePlan resolve) => resolve switch
    {
        ReplyResolvePlan.Planned => " [blue]+resolve[/]",
        ReplyResolvePlan.AlreadyResolved => " [grey](already resolved)[/]",
        ReplyResolvePlan.NotClosable => " [yellow](anchor is dangling — resolve it by hand)[/]",
        ReplyResolvePlan.NoVersion => " [yellow](no version in the response — resolve it by hand)[/]",
        _ => string.Empty,
    };

    /// <summary>
    /// Every item that gets no reply. The two routine reasons are counted; the rest are named, because
    /// each one is a reviewer whose comment was triaged and will never be answered without a human.
    /// </summary>
    private static void RenderReplySkips(FeedbackReplyPlan plan)
    {
        var untriaged = plan.SkippedCount(FeedbackReplySkipReason.NotTriaged);
        if (untriaged > 0)
        {
            AnsiConsole.MarkupLine($"  [grey]{untriaged} item(s) not triaged yet.[/]");
        }

        var answered = plan.SkippedCount(FeedbackReplySkipReason.AlreadyReplied);
        if (answered > 0)
        {
            AnsiConsole.MarkupLine($"  [grey]{answered} item(s) already answered.[/]");
        }

        var notable = plan.Skipped.Where(skip =>
            skip.Reason is not (FeedbackReplySkipReason.NotTriaged or FeedbackReplySkipReason.AlreadyReplied));

        foreach (var skip in notable)
        {
            var file = Path.GetFileName(skip.FilePath);
            AnsiConsole.MarkupLine(
                $"  [yellow]skipped[/] {file.EscapeMarkup()} [grey]({ExplainReply(skip.Reason)})[/]");
        }
    }

    private static string ExplainReply(FeedbackReplySkipReason reason) => reason switch
    {
        FeedbackReplySkipReason.Unreadable => "the file could not be parsed",
        FeedbackReplySkipReason.Unaddressable => "it names no page or no Confluence comment id",
        FeedbackReplySkipReason.PageNotPublished => "its page has never been published",
        FeedbackReplySkipReason.CommentGone => "the comment no longer exists in Confluence",
        FeedbackReplySkipReason.AlreadyReplied => "already answered",
        _ => "not triaged yet",
    };

    /// <summary>Reports what Confluence refused, and turns any of it into a non-zero exit.</summary>
    private static int RenderReplyFailures(FeedbackReplyResult result)
    {
        foreach (var failure in result.Failures)
        {
            var what = failure.Replied ? "resolve" : "reply to";
            AnsiConsole.MarkupLine(
                $"  [red]failed[/] to {what} comment {failure.CommentId.EscapeMarkup()} — "
                + failure.Detail.EscapeMarkup());
        }

        if (result.StoppedBecause is { Length: > 0 } stopped)
        {
            AnsiConsole.MarkupLine($"[red]{stopped.EscapeMarkup()}[/]");
        }

        return result.Failures.Count == 0 ? 0 : 1;
    }

    private static string Unpublished(int skipped)
        => skipped == 0 ? string.Empty : $" [grey]({skipped} page(s) not published yet, skipped)[/]";

    private static string Unreadable(int unpublished)
        => unpublished == 0
            ? string.Empty
            : $" [yellow]({unpublished} page(s) never published, so their items cannot be answered)[/]";

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

    /// <summary>
    /// Where this run reads and writes: one state file, one inbox directory, and the archive beside it
    /// (<see cref="FeedbackInbox.ArchiveBeside"/>) — which the reply pass reads because a triaged item is
    /// usually already there by the time its reply is due (§9 step 4).
    /// </summary>
    private sealed record SyncPaths(string StatePath, string InboxPath, string ArchivePath);

    /// <summary>
    /// Which halves a run performs. As parsed it is what the flags asked for; by the time it reaches
    /// <see cref="SyncScope"/> the defaulting rule has been applied.
    /// </summary>
    private sealed record SyncHalves(bool Labels, bool Comments, bool Reply);

    /// <summary>Which halves run, against which space, and whether anything may be written.</summary>
    private sealed record SyncScope(SyncHalves Halves, string? SpaceKey, bool DryRun);

    /// <summary>One half's outcome: the state it produced, and whether it changed anything.</summary>
    private sealed record SyncHalf(DocumeState State, bool Changed);
}
