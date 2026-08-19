using System.Globalization;
using System.Net;
using System.Text;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Drift;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;
using DocuMe.Core.Sync;

namespace DocuMe.Core.Publishing;

/// <summary>
/// Renders one <c>```mermaid</c> fence body to SVG — <see cref="MermaidRenderer.RenderAsync"/> in a
/// real run (PLAN.md §6.2 step 3).
/// </summary>
/// <remarks>
/// A delegate rather than a dependency on <see cref="MermaidRenderer"/>, matching
/// <see cref="MermaidDiagramResolver"/>: the executor's own behavior is testable without Node, and
/// nothing about publishing needs to know that rendering means starting a process.
/// </remarks>
public delegate Task<MermaidDiagram> DiagramRenderer(string mermaidSource, CancellationToken cancellationToken);

/// <summary>
/// What a run needs to seal each page it publishes: the tree the <c>sources</c> globs are read against,
/// the globs themselves, and the moment the seal describes
/// (docs/specs/2026-08-19-sealed-source-verdicts.md §3.2).
/// </summary>
/// <remarks>
/// <para>
/// One record rather than three optional properties on <see cref="PublishExecutionOptions"/>, because a
/// half-configured seal is not a weaker seal — a root without globs matches nothing on every page, and a
/// whole wiki that sealed nothing while reporting success is worse than one that never tried. Present or
/// absent, never partly.
/// </para>
/// <para>
/// The globs arrive as a map rather than being read off the plan because a plan carries no frontmatter:
/// <see cref="PlannedPage"/> is the converter's output plus a decision, and <c>sources</c> is the wiki
/// tree's business (§5.2). A page missing from <see cref="SourcesByPath"/> declares none, so its globs
/// match nothing and <c>SealSources</c> records no verdict for it — quietly, since a page without
/// <c>sources</c> is invisible to <see cref="Drift.DriftPlanner.Plan"/> to begin with.
/// </para>
/// </remarks>
/// <param name="RepoRoot">
/// The directory <c>sources</c> globs are anchored at — the one holding <c>docume.json</c> (§5.1), which
/// is the repo root git reports changed files relative to. Anything else matches nothing, and a run whose
/// every page declines to seal is the visible shape that mistake takes.
/// </param>
/// <param name="SourcesByPath">
/// Wiki-relative markdown path → that page's declared <c>sources</c> globs
/// (<see cref="Markdown.PageFrontmatter.Sources"/>).
/// </param>
/// <param name="SealedAt">
/// When this run seals, supplied rather than read off a clock so the write path stays testable, exactly
/// as <see cref="Sync.LabelReader"/> takes its observation time. One value for the whole run, like the
/// banner's date: fifty pages sealed in one publish were sealed at one moment.
/// </param>
/// <param name="CandidateFiles">
/// Every file a page's globs may match, repo-relative with forward slashes — git's tracked files
/// (<see cref="Git.GitRepository.TrackedFilesAsync"/>). Supplied for the whole run rather than read per
/// page: one <c>ls-files</c> answers for an eighty-page wiki, and one list is one universe, which is the
/// property the seal needs (<see cref="Drift.SourcesFingerprint"/>, spec §3.1 as amended). Positional
/// like the other three because a list somebody forgot to pass is not a weaker seal — every page in the
/// wiki matches nothing, and the run seals not one of them.
/// </param>
public sealed record SourceSealing(
    string RepoRoot,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SourcesByPath,
    DateTimeOffset SealedAt,
    IReadOnlyCollection<string> CandidateFiles);

/// <summary>Per-run inputs the write path needs that a plan cannot carry.</summary>
public sealed record PublishExecutionOptions
{
    /// <summary>
    /// The repo commit this run publishes, stamped into state as <c>lastPublishedSha</c> (§6.2 step 8),
    /// or <c>null</c> to leave the previous value alone. Written only when the run finishes clean:
    /// "the wiki is published at this commit" is false if any page failed.
    /// </summary>
    public string? RepoSha { get; init; }

    /// <summary>
    /// Whether to reconcile each parent's child order once the pages are written (§6.2's post-pass).
    /// <c>--no-reorder</c> sets it false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run that wrote nothing skips the pass regardless: no page was created and none was moved, so no
    /// position changed, and a settled wiki should not pay a read per parent to be told so. The cost of
    /// that shortcut is narrow and deliberate — a tree somebody reordered by hand in Confluence stays
    /// reordered until the next run that writes something.
    /// </para>
    /// <para>
    /// It is <em>not</em> narrowed by <see cref="PublishOptions.Scope"/>, unlike every page write. Child
    /// order is a property of a whole sibling set, so reconciling only the scoped subset would be a
    /// different order than the tree asks for; and the pass writes no bodies and cannot revoke an
    /// approval (§9.2, the same reason a reparent is cheap), so the usual reason a scope holds a page
    /// back does not apply. A run that inserts one page in CI is exactly the case the post-pass exists
    /// for.
    /// </para>
    /// </remarks>
    public bool Reorder { get; init; } = true;

    /// <summary>
    /// Whether to look for unresolved inline comments on a page before overwriting its body (§6.2 step 6).
    /// <c>--no-comment-check</c> sets it false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check costs one read per page whose body this run rewrites, and nothing at all otherwise: a
    /// page being created has no comments yet, a skipped page is not touched, and neither a move nor an
    /// attachment-only publish changes the text a comment is anchored to.
    /// </para>
    /// <para>
    /// On by default because §6.2 asks for the warning by default. Off is offered for the bulk case — a
    /// full republish of an ~80-page wiki pays ~80 extra reads — and because this read has never run
    /// against a real tenant, so a way to switch it off without a rebuild is worth having.
    /// <see cref="BlockOnOpenComments"/> turns it back on regardless: blocking on a check that was
    /// skipped would be a promise the run cannot keep.
    /// </para>
    /// </remarks>
    public bool CheckOpenComments { get; init; } = true;

    /// <summary>
    /// Whether an unresolved inline comment holds its page back instead of warning about it (§6.2's
    /// <c>--block-on-open-comments</c>). Off by default, which is what §6.2 specifies: see
    /// <see cref="OpenCommentGuard"/> for why warn-and-proceed is the designed behavior rather than a
    /// leniency.
    /// </summary>
    public bool BlockOnOpenComments { get; init; }

    /// <summary>
    /// Whether a page whose approval this run revokes also gets §6.2 step 7's footer comment asking for a
    /// re-review. <c>--notify-reviewers</c> sets it true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default because §6.2 spells it "optionally", and because the cost is a notification to
    /// everyone watching the page rather than a page version: a bulk republish that revokes forty
    /// approvals would mail forty comments, and the dashboard (§6.5) already says the same thing without
    /// mailing anybody.
    /// </para>
    /// <para>
    /// It only ever fires where the label came off. An approval that was already <c>needs-review</c>, a
    /// page that changed while unapproved, and a move or attachment-only publish (§9.2) all leave nothing
    /// to re-review, so none of them is worth a reviewer's inbox.
    /// </para>
    /// </remarks>
    public bool NotifyReviewers { get; init; }

    /// <summary>
    /// What to seal each published page's sources against (<see cref="SourceSealing"/>), or <c>null</c>
    /// to publish without sealing.
    /// </summary>
    /// <remarks>
    /// Optional because a publish does not need a source tree to be correct: a run given none writes no
    /// seal and leaves every seal a previous run wrote exactly as it is (<c>StateUpdates.RecordPublish</c>),
    /// which is the same non-behaviour a wiki that has never published under this feature gets. The cost
    /// of switching it on is one hash pass over the files each written page's globs match — see the
    /// spec's risk 1 for the page that claims the whole tree.
    /// </remarks>
    public SourceSealing? Sealing { get; init; }
}

/// <summary>
/// Executes a <see cref="PublishReport"/>: creates and updates pages parents-first, uploads the
/// attachments whose bytes moved, revokes the approvals a content change invalidated, and returns
/// state ready to be persisted (PLAN.md §6.2 steps 5-8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It re-decides nothing.</strong> Every choice — create or update, which attachments,
/// whether approval falls — was made by <see cref="PublishPipeline"/> offline, so a real run and
/// <c>--dry-run</c> cannot drift apart. What this type adds is the four things a plan cannot know: the
/// page ids parent paths map onto, the bytes of an unrendered diagram, the version Confluence holds
/// right now, and what actually happened.
/// </para>
/// <para>
/// <strong>It reports rather than throws.</strong> Confluence and renderer trouble comes back as
/// <see cref="PublishOutcome.Failures"/> or <see cref="PublishOutcome.StoppedBecause"/>, never as an
/// exception, because the outcome carries the state that has to be persisted either way — a page id
/// earned by a create and then lost to an exception becomes a duplicate-title failure on the next run.
/// A broken invariant in the plan itself still throws: that is a bug, not a run-time condition.
/// </para>
/// <para>
/// <strong>Attachment bytes are materialized before the page is written.</strong> A diagram Node
/// refuses must fail its page <em>before</em> a create, or the run leaves behind a published page with
/// a broken image and no state entry naming it.
/// </para>
/// <para>
/// One instance per run: it caches rendered diagrams and asset bytes by attachment name, which is how
/// a diagram repeated across pages costs one Node process (§6.2 step 3) rather than one per page.
/// </para>
/// </remarks>
public sealed class PublishExecutor
{
    /// <summary>Media type Confluence must record for an SVG to render inline rather than as a download.</summary>
    private const string SvgMediaType = "image/svg+xml";

    private const string DefaultMediaType = "application/octet-stream";

    /// <summary>
    /// §6.2 step 7's comment, verbatim from the plan in its first sentence, with the machine provenance a
    /// reader needs to know why a comment appeared under a page nobody commented on.
    /// </summary>
    private const string ReviewRequestComment =
        "<p>Content updated since approval — please re-review.</p>"
        + "<p><em>Posted by DocuMe. This page changed after it was approved, so its <code>approved</code> "
        + "label was removed and its status is now needs-review.</em></p>";

    private readonly ConfluenceClient _client;
    private readonly string _wikiRoot;
    private readonly DiagramRenderer? _renderDiagram;

    /// <summary>Attachment name → its bytes and hash, so identical content is materialized once per run.</summary>
    private readonly Dictionary<string, AttachmentContent> _content = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="PublishExecutor"/> class for one run.</summary>
    /// <param name="client">The Confluence client, already carrying credentials and the retry pipeline.</param>
    /// <param name="wikiRoot">
    /// The wiki root the plan's asset paths are relative to (<see cref="WikiTree.Root"/>).
    /// </param>
    /// <param name="renderDiagram">
    /// How to render a mermaid diagram. Optional: a wiki with no diagram to upload never calls it, and
    /// a run that needs one without it fails the page loudly rather than publishing a broken image.
    /// </param>
    public PublishExecutor(ConfluenceClient client, string wikiRoot, DiagramRenderer? renderDiagram = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(wikiRoot);

        _client = client;
        _wikiRoot = wikiRoot;
        _renderDiagram = renderDiagram;
    }

    /// <summary>
    /// Executes <paramref name="report"/> against Confluence.
    /// </summary>
    /// <param name="config">The consumer repo's <c>docume.json</c> — space, root page, label names.</param>
    /// <param name="report">The plan, from <see cref="PublishPipeline.Plan"/>.</param>
    /// <param name="state">State as loaded. Never mutated: the outcome carries the new value.</param>
    /// <param name="options">Per-run inputs; defaults when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PublishOutcome> ExecuteAsync(
        DocumeConfig config,
        PublishReport report,
        DocumeState state,
        PublishExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(state);
        options ??= new PublishExecutionOptions();

        var original = state;
        var results = new List<PagePublishResult>();
        var failures = new List<PagePublishFailure>();
        var warnings = new List<string>();
        var reorders = new List<ChildReorder>();

        PublishOutcome Outcome(string? stoppedBecause) => new(
            state, !ReferenceEquals(state, original), results, failures, warnings, stoppedBecause)
        {
            Reorders = reorders,
        };

        // The guard REPORTS a refusal rather than throwing (PublishGuard), so a write path that never
        // looks is precisely the failure mode it exists to prevent (CLAUDE.md §0.1, rule §1.4).
        if (!report.CanWrite)
        {
            return Outcome(report.WriteRefusal);
        }

        if (!report.CanPublish)
        {
            return Outcome(
                $"{report.Failures.Count} page(s) the converter refuses; no page publishes until every "
                + "page converts (§7).");
        }

        var pageIds = KnownPageIds(state);
        var excludedByScope = report.ExcludedByScope
            .Select(page => page.Path)
            .ToHashSet(StringComparer.Ordinal);
        var drafts = report.Drafts.ToHashSet(StringComparer.Ordinal);

        var writes = report.Pages.Count(page => page.Action != PagePublishAction.Skip);
        var spaceId = string.Empty;

        if (writes > 0)
        {
            // Resolved once, before the first write: a create needs the numeric id, and a space the
            // token cannot see is a whole-run problem rather than a per-page one.
            try
            {
                spaceId = await ResolveSpaceIdAsync(config.Confluence, cancellationToken).ConfigureAwait(false)
                    ?? string.Empty;
            }
            catch (ConfluenceException ex)
            {
                return Outcome(ex.Message);
            }
            catch (HttpRequestException ex)
            {
                return Outcome(Unreachable(ex));
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                return Outcome(TimedOut(ex));
            }
            catch (OperationCanceledException)
            {
                return Outcome(Cancelled(path: null, published: 0));
            }

            if (spaceId.Length == 0)
            {
                return Outcome(
                    $"Confluence has no space with key '{config.Confluence.SpaceKey}' that this account "
                    + "can see. Check confluence.spaceKey in docume.json and the account's space "
                    + "permissions.");
            }
        }

        foreach (var planned in report.Pages)
        {
            // Returned, not thrown: an interrupted run's state is the caller's to persist, and a page id
            // earned moments ago is exactly what must not be lost with it (see PublishOutcome.State).
            if (cancellationToken.IsCancellationRequested)
            {
                return Outcome(Cancelled(planned.Path, results.Count));
            }

            if (planned.Action == PagePublishAction.Skip)
            {
                // A page the scope excluded was deliberately left alone, and the report already names it
                // as held back, so nagging about its parent here would only offer advice (--force) that
                // this run would ignore anyway.
                if (!planned.ExcludedByScope)
                {
                    WarnOnParentDrift(planned, state, pageIds, config.Confluence.RootPageId, warnings);
                }

                continue;
            }

            if (!TryResolveParentId(planned, pageIds, config.Confluence.RootPageId, out var parentId))
            {
                failures.Add(
                    new PagePublishFailure(
                        planned.Path, MissingParentMessage(planned.ParentPath!, drafts, excludedByScope)));

                continue;
            }

            PageOutcome outcome;
            try
            {
                outcome = await PublishPageAsync(
                        config, planned, state, spaceId, parentId, options, warnings, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ConfluenceAuthenticationException ex)
            {
                // Never retried, never worked around: an expired token replayed across an ~80-page
                // publish is how an account gets locked out (rule §1.2).
                failures.Add(new PagePublishFailure(planned.Path, ex.Message));
                return Outcome(
                    $"Confluence rejected the credentials, so the run stopped at '{planned.Path}' with "
                    + $"{results.Count} page(s) published. Nothing after it was attempted.");
            }
            catch (MermaidRenderException ex) when (ex.Fault == MermaidRenderFault.Setup)
            {
                // Nothing could have rendered, so every remaining diagram would fail identically.
                failures.Add(new PagePublishFailure(planned.Path, ex.Message));
                return Outcome(
                    $"the mermaid renderer cannot run, so the run stopped at '{planned.Path}' with "
                    + $"{results.Count} page(s) published.");
            }
            catch (ConfluenceException ex)
            {
                failures.Add(new PagePublishFailure(planned.Path, ex.Message));
                continue;
            }
            catch (HttpRequestException ex)
            {
                // Already retried with backoff by the transport, so a connection still being refused
                // will refuse the remaining pages too. Stopping says that once instead of 78 times.
                failures.Add(new PagePublishFailure(planned.Path, Unreachable(ex)));
                return Outcome(
                    $"Confluence became unreachable at '{planned.Path}', with {results.Count} page(s) "
                    + "published. Nothing after it was attempted.");
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(new PagePublishFailure(planned.Path, TimedOut(ex)));
                return Outcome(
                    $"Confluence stopped answering at '{planned.Path}', with {results.Count} page(s) "
                    + "published. Nothing after it was attempted.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Not a page failure: the page was not refused, the operator stopped the run. Whatever it
                // had already written is in `state`, and returning is what carries that to the caller.
                return Outcome(Cancelled(planned.Path, results.Count));
            }
            catch (MermaidRenderException ex)
            {
                failures.Add(new PagePublishFailure(planned.Path, ex.Message));
                continue;
            }
            catch (IOException ex)
            {
                failures.Add(new PagePublishFailure(planned.Path, $"an attachment could not be read: {ex.Message}"));
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                failures.Add(new PagePublishFailure(planned.Path, $"an attachment could not be read: {ex.Message}"));
                continue;
            }

            state = outcome.State;

            if (outcome.Failure is { } failure)
            {
                failures.Add(new PagePublishFailure(planned.Path, failure));
                continue;
            }

            var result = outcome.Result!;
            results.Add(result);
            pageIds[planned.Path] = result.PageId;
        }

        // The post-pass, after every upsert and after nothing else: it needs every child's final page
        // id, and the id of a page created moments ago does not exist until its create returns (§6.2,
        // "after all upserts"). Unreachable from the early returns above on purpose — a run that
        // stopped on an expired token or a dead connection has no business issuing more requests.
        string? stopped = null;
        if (options.Reorder && writes > 0)
        {
            stopped = await ReconcileChildOrderAsync(
                    report, pageIds, config.Confluence.RootPageId, reorders, warnings, cancellationToken)
                .ConfigureAwait(false);
        }

        // Stamped even when the post-pass was cancelled: every page published, and the pass touches no
        // state by design — what was interrupted is the order of a menu, not the wiki this sha describes.
        if (failures.Count == 0 && options.RepoSha is { Length: > 0 } sha)
        {
            state = StateUpdates.RecordLastPublishedSha(state, sha);
        }

        return Outcome(stopped);
    }

    /// <summary>Publishes one page: page write, attachment uploads, approval, state (§6.2 steps 5-8).</summary>
    private async Task<PageOutcome> PublishPageAsync(
        DocumeConfig config,
        PlannedPage planned,
        DocumeState state,
        string spaceId,
        string? parentId,
        PublishExecutionOptions options,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var plan = planned.Plan;
        state.Pages.TryGetValue(planned.Path, out var current);

        // Reads first, so the create-or-update shape is settled before anything is written. An update
        // needs the version Confluence holds now, not the one state remembers: a human's browser edit
        // moves it on, and republishing over hand edits is the design (rule §9.1) — publishing over a
        // stale version number is just a 409.
        var pageId = current?.PageId;
        var recreate = false;
        var remoteVersion = 0;

        if (plan.Action != PagePublishAction.Create)
        {
            var remote = await _client.FindPageByIdAsync(pageId!, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (remote is null)
            {
                recreate = true;
            }
            else
            {
                remoteVersion = remote.Version;
            }
        }

        if (recreate && planned.UploadBody is null)
        {
            // An attachment upload or a move has no body to write, so there is nothing to re-create the
            // page from. Said plainly with the fix, rather than uploading into a 404.
            var vanished = $"page {pageId} no longer exists in Confluence and this run writes no body for "
                + "it, so there was nothing to publish. Re-run with --force to create it again.";

            return new PageOutcome(state, null, vanished);
        }

        var creating = plan.Action == PagePublishAction.Create || recreate;

        if (!creating && plan.WritesBody)
        {
            WarnOnHandEdits(planned, current, remoteVersion, warnings);
        }

        // §6.2 step 6, and this early on purpose: a page that is about to be held back must not render a
        // diagram or upload a byte first. Only a body rewrite can strand a comment's anchor — a create has
        // no comments to strand, and a move or an attachment-only publish leaves the text alone.
        if (!creating && plan.WritesBody && ChecksComments(options))
        {
            var refusal = await GuardOpenCommentsAsync(
                    planned.Path, pageId!, options, warnings, cancellationToken)
                .ConfigureAwait(false);

            if (refusal is not null)
            {
                return new PageOutcome(state, null, refusal);
            }
        }

        // A recreated page carries none of its old attachments, so the changed subset is not enough.
        var uploads = creating
            ? planned.Attachments.Select(attachment => attachment.Name).ToArray()
            : plan.ChangedAttachments;

        var byName = planned.Attachments.ToDictionary(attachment => attachment.Name, StringComparer.Ordinal);
        var materialized = new List<(string Name, AttachmentContent Content)>(uploads.Count);
        foreach (var name in uploads)
        {
            var content = await ContentForAsync(byName[name], cancellationToken).ConfigureAwait(false);
            materialized.Add((name, content));
        }

        // §7's ac:width, here and not in the plan: it is a measurement of a rendered SVG, and nothing is
        // rendered until the loop above (DiagramImageWidth).
        var widths = DiagramWidths(planned, current, materialized);
        var body = planned.UploadBody is null
            ? null
            : DiagramImageWidth.Apply(planned.UploadBody, widths);

        // The managed marker (ManagedMarker): stamped on every create, and on a body update of a page
        // state does not record as marked, which is how pages published before the marker existed pick
        // one up. A move or an attachment-only publish stamps nothing: neither proves authorship any
        // better than the page already does, and the body write is the natural moment.
        var marked = current is { Marked: true };

        int version;
        if (creating)
        {
            var created = await _client
                .CreatePageAsync(
                    new ConfluencePageDraft(spaceId, planned.Title, RequireBody(planned, body), parentId),
                    cancellationToken)
                .ConfigureAwait(false);

            pageId = created.Id;
            version = created.Version;

            if (recreate)
            {
                warnings.Add(
                    $"{planned.Path} was missing from Confluence and was created again as page {pageId}. "
                    + "Any labels the old page carried are gone with it — `docume sync` reconciles them.");
            }

            // Unconditional, recreate included: a fresh page carries no property whatever state
            // remembers, and `marked` takes the stamp's actual outcome so a failed stamp on a recreate
            // clears a record that is no longer true.
            marked = await StampManagedMarkerAsync(planned.Path, pageId!, warnings, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (plan.WritesBody)
        {
            var updated = await _client
                .UpdatePageAsync(
                    new ConfluencePageRevision(
                        pageId!,
                        planned.Title,
                        RequireBody(planned, body),
                        remoteVersion,
                        parentId,
                        ProvenanceMessage(options.RepoSha, plan.ContentHash)),
                    cancellationToken)
                .ConfigureAwait(false);

            version = updated.Version;

            if (!marked)
            {
                marked = await StampManagedMarkerAsync(planned.Path, pageId!, warnings, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else if (plan.Action == PagePublishAction.Move)
        {
            if (parentId is not { Length: > 0 } target)
            {
                // A move appends the page under a target page, and the target for a page at the top of
                // the tree is confluence.rootPageId. With none configured there is no page to append
                // under, so the move is impossible rather than optional — and a request with an empty
                // id segment would be a 404 dressed up as a bug.
                const string unmovable = "the tree moves this page to the top of the wiki, but no "
                    + "confluence.rootPageId is set in docume.json, so there is no page to file it "
                    + "under. Set confluence.rootPageId, or move the page in Confluence by hand.";

                return new PageOutcome(state, null, unmovable);
            }

            await _client
                .MovePageAsync(
                    new ConfluencePageMove(pageId!, ConfluencePageMovePosition.Append, target),
                    cancellationToken)
                .ConfigureAwait(false);

            // Whether a move bumps the page version is undocumented and unverified against a real
            // Confluence (ConfluenceClient.MovePageAsync), so it is re-read rather than assumed: §5.3
            // wants state to record what Confluence holds, not what the run hoped it would hold.
            var moved = await _client.FindPageByIdAsync(pageId!, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            version = moved?.Version ?? remoteVersion;
        }
        else
        {
            // §6.2's whole reason for a third action: changed attachment bytes must reach the page
            // without spending a page version, which would churn history and (before §9.2) approval.
            version = remoteVersion;
            WarnOnParentDrift(planned, current?.ParentPageId, parentId, warnings);
        }

        var uploaded = new List<string>(materialized.Count);
        foreach (var (name, content) in materialized)
        {
            await _client
                .UploadAttachmentAsync(
                    new ConfluenceAttachmentUpload(pageId!, name, content.Bytes, content.ContentType),
                    cancellationToken)
                .ConfigureAwait(false);

            uploaded.Add(name);
        }

        var revoked = false;
        var notified = false;
        if (plan.InvalidatesApproval)
        {
            await RemoveLabelIfPresentAsync(pageId!, config.Labels.Approved, cancellationToken)
                .ConfigureAwait(false);
            state = StateUpdates.InvalidateApproval(state, planned.Path);
            revoked = true;

            // After the label, never before it: the comment says the approval is gone, and saying so
            // while it is still on the page would be false for however long the next request takes.
            if (options.NotifyReviewers)
            {
                notified = await NotifyReviewersAsync(planned.Path, pageId!, warnings, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var published = new PublishedPage(
            pageId!,
            planned.Title,
            parentId,
            plan.ContentHash,
            version,
            AttachmentHashes(planned, materialized),
            widths,
            SealSources(planned, options, wroteBody: creating || plan.WritesBody, current, warnings));

        state = StateUpdates.RecordPublish(state, planned.Path, published);
        state = StateUpdates.RecordMarked(state, planned.Path, marked);

        return new PageOutcome(
            state,
            new PagePublishResult(
                planned.Path, planned.Title, plan.Action, pageId!, version, uploaded, revoked, recreate)
            {
                ReviewersNotified = notified,
            },
            null);
    }

    /// <summary>
    /// The seal for a page this run just wrote: the fingerprint of the files its <c>sources</c> globs
    /// match now (spec §3.2), or <c>null</c> for nothing to record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only a body write seals.</strong> The seal's claim is "these were the source bytes when
    /// the live body was published", so a run that wrote no body has nothing new to claim: a move and an
    /// attachment upload leave the body exactly as the sources named by the standing seal produced it.
    /// Re-sealing them would silently re-point an old body at today's sources and swallow the very drift
    /// the page should report. Null therefore means "leave the previous seal standing", which is what
    /// <see cref="StateUpdates.RecordPublish"/> does with it.
    /// </para>
    /// <para>
    /// <strong>Sources it cannot read seal nothing, and warn.</strong> A page whose glob names a
    /// directory that is not in this checkout, or a file the process may not open, must not publish a
    /// fingerprint over whatever happened to be readable — that value would equal itself on the next run
    /// and hold the page out of a drift report nobody could have verified. Warned rather than failed
    /// because the page itself is published and correct by the time this runs, the same contract
    /// <see cref="StampManagedMarkerAsync"/> keeps.
    /// </para>
    /// <para>
    /// <strong>Globs that matched nothing seal nothing either</strong> (spec §3.1 as revised
    /// 2026-08-19). <see cref="SourcesFingerprint.EmptySet"/> is a real hash but a useless verdict: it is
    /// what a typo'd glob, a page that declares no sources, and a sparse checkout scoped away from
    /// <c>src/</c> all produce, and a later drift run under the same structural condition recomputes it
    /// and reports the page as verified against bytes nobody read. Refusing it costs the wiki nothing —
    /// a page whose globs match nothing is a page drift would never flag anyway — and buys the guarantee
    /// that a seal in state is always evidence about at least one file.
    /// </para>
    /// <para>
    /// <strong>The warning says which of the two situations the operator is left in.</strong> A page that
    /// never sealed falls back to the commit range; a page carrying a seal an earlier publish wrote keeps
    /// answering from those bytes, because <see cref="StateUpdates.RecordPublish"/> carries a standing
    /// seal through a null. Those are different facts and "drift keeps answering from the commit range"
    /// is only true of the first, so the message is worded on <paramref name="current"/> rather than
    /// asserting one of them at both.
    /// </para>
    /// </remarks>
    private static SealedVerdict? SealSources(
        PlannedPage planned,
        PublishExecutionOptions options,
        bool wroteBody,
        PageState? current,
        List<string> warnings)
    {
        if (!wroteBody || options.Sealing is not { } sealing)
        {
            return null;
        }

        // A page the map does not name declares no sources, so its globs match nothing and the block
        // below declines to seal it — silently, because a page that documents no file is not a problem
        // and drift never reads it back (DriftPlanner.Plan skips a page without sources first of all).
        var patterns = sealing.SourcesByPath.GetValueOrDefault(planned.Path) ?? [];

        string fingerprint;
        try
        {
            fingerprint = SourcesFingerprint.Compute(sealing.RepoRoot, patterns, sealing.CandidateFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add(
                $"{planned.Path} published, but the files its `sources` globs name could not all be read, "
                + $"so this publish sealed none of them ({ex.Message}). {Fallback(current)}");

            return null;
        }

        if (SourcesFingerprint.IsEmptySet(fingerprint))
        {
            if (Declares(patterns))
            {
                warnings.Add(
                    $"{planned.Path} published, but its `sources` globs matched none of the files git "
                    + "tracks, so this publish sealed nothing. Check the globs against the tree — a glob "
                    + $"that can never fire is an advisory check nobody investigates. {Fallback(current)}");
            }

            return null;
        }

        return new SealedVerdict
        {
            SourcesHash = fingerprint,
            SealedAt = sealing.SealedAt.UtcDateTime.ToString(
                LabelReader.TimestampFormat, CultureInfo.InvariantCulture),
            RepoSha = options.RepoSha,
        };
    }

    /// <summary>Whether the page named a glob at all, blanks not counting (<see cref="SourcesFingerprint"/> skips them).</summary>
    private static bool Declares(IReadOnlyList<string> patterns) =>
        patterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern));

    /// <summary>
    /// What answers drift for a page this run declined to seal — the second sentence of both warnings
    /// above, and the reason they are not one fixed string: a page whose earlier publish sealed
    /// successfully is not back on the commit range, it is running on an older seal, and only
    /// <paramref name="current"/> can tell the two apart.
    /// </summary>
    private static string Fallback(PageState? current) =>
        current?.Verdict?.SourcesHash is { Length: > 0 }
            ? "The seal an earlier publish wrote still stands, so drift keeps answering for the page "
                + "against those bytes — which are now older than the body just published."
            : "Drift keeps answering for the page from the commit range until a publish that can seal it.";

    /// <summary>Whether this run reads a page's comments before rewriting it (§6.2 step 6).</summary>
    /// <remarks>
    /// Blocking implies checking, so the two flags cannot combine into a run that promises to hold pages
    /// back and then never looks. The CLI rejects the contradictory command line outright; this makes the
    /// safe reading the only reachable one for every other caller.
    /// </remarks>
    private static bool ChecksComments(PublishExecutionOptions options)
        => options.CheckOpenComments || options.BlockOnOpenComments;

    /// <summary>
    /// The open-comment guard (§6.2 step 6): why this page must be left alone, or <c>null</c> to carry on
    /// — adding a warning on the way when there were comments but the run was told to proceed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Executor-time, not plan-time, and that has a consequence worth stating.</strong> The check
    /// needs the network, and <see cref="PublishPipeline.Plan"/> is pure by construction — which is what
    /// makes <c>--dry-run</c> the real run minus its side effects. So a dry run cannot report open
    /// comments at all, and the CLI says so rather than implying otherwise.
    /// </para>
    /// <para>
    /// <strong>A comments read that fails does not fail the page — unless it was load-bearing.</strong>
    /// Warning mode is advisory, so an endpoint that answers 404 or 500 costs a warning saying the check
    /// did not happen and the page publishes; under <c>--block-on-open-comments</c> the caller asked for a
    /// guarantee, and proceeding on an unread page would be exactly the silent failure the flag was passed
    /// to prevent. An expired token is neither case: it propagates, and the run stops (rule §1.2).
    /// </para>
    /// </remarks>
    private async Task<string?> GuardOpenCommentsAsync(
        string path,
        string pageId,
        PublishExecutionOptions options,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ConfluenceInlineComment> comments;
        try
        {
            comments = await _client.GetInlineCommentsAsync(pageId, cancellationToken).ConfigureAwait(false);
        }
        catch (ConfluenceException ex) when (ex is not ConfluenceAuthenticationException)
        {
            if (options.BlockOnOpenComments)
            {
                return "its inline comments could not be read, and --block-on-open-comments cannot be "
                    + $"honored without them, so the page was left alone: {ex.Message}";
            }

            warnings.Add(
                $"{path}: its inline comments could not be read, so nothing checked whether rewriting it "
                + $"strands one ({ex.Message}). The page itself published.");

            return null;
        }

        var unresolved = OpenCommentGuard.Unresolved(comments);
        if (unresolved.Count == 0)
        {
            return null;
        }

        if (options.BlockOnOpenComments)
        {
            return OpenCommentGuard.Refusal(unresolved);
        }

        warnings.Add(OpenCommentGuard.Warning(path, unresolved));

        return null;
    }

    /// <summary>
    /// Reconciles every parent's child order with the source tree (§6.2's post-pass), one read and as
    /// few moves per parent as <see cref="ChildOrderPlanner"/> can manage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Trouble here is a warning, not a failure.</strong> The pages are published by the time
    /// this runs and their content is correct; what is wrong is the order of a menu. Reporting it and
    /// exiting zero is the honest reading, and the next run reconciles what this one could not — where
    /// failing the command would tell CI that a correct publish was broken. It is reported by name
    /// either way, never swallowed.
    /// </para>
    /// <para>
    /// Nothing here touches state: a position is not in §5.3, which is what lets the pass be skipped,
    /// retried, or turned off without the next run planning differently.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Why the pass stopped early, or <c>null</c>. Only cancellation fills it: the pass's own troubles are
    /// warnings by the contract above, but a run the operator interrupted is not a run that succeeded.
    /// </returns>
    private async Task<string?> ReconcileChildOrderAsync(
        PublishReport report,
        Dictionary<string, string> pageIds,
        string? rootPageId,
        List<ChildReorder> reorders,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var groups = GroupChildren(report, pageIds, rootPageId, warnings);

        foreach (var group in groups)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return CancelledReorder(group.ParentName);
            }

            try
            {
                await ReconcileAsync(group, reorders, warnings, cancellationToken).ConfigureAwait(false);
            }
            catch (ConfluenceAuthenticationException ex)
            {
                // Same rule as the page loop (rule §1.2): never retried, never worked around, and the
                // remaining parents would refuse identically.
                warnings.Add(
                    $"child order was left alone from {group.ParentName} onwards: {ex.Message} The pages "
                    + "themselves are published.");
                return null;
            }
            catch (HttpRequestException ex)
            {
                warnings.Add($"child order was left alone from {group.ParentName} onwards: {Unreachable(ex)}");
                return null;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"child order was left alone from {group.ParentName} onwards: {TimedOut(ex)}");
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CancelledReorder(group.ParentName);
            }
            catch (ConfluenceException ex)
            {
                // One parent's own problem — a page moved out from under the run, a permission that
                // covers the pages but not the tree — so the rest of the wiki still gets ordered.
                warnings.Add($"the child order under {group.ParentName} was left alone: {ex.Message}");
            }
        }

        return null;
    }

    private async Task ReconcileAsync(
        ChildGroup group,
        List<ChildReorder> reorders,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var desired = group.Children.Select(child => child.PageId).ToList();
        var observed = await _client.GetChildPagesAsync(group.ParentPageId, cancellationToken)
            .ConfigureAwait(false);

        var moves = ChildOrderPlanner.Plan(desired, [.. observed.Select(child => child.Id)]);
        if (moves.Count == 0)
        {
            return;
        }

        foreach (var move in moves)
        {
            // In the planned order, which is load-bearing: each move anchors on a sibling an earlier
            // one placed (ChildOrderPlanner.Plan).
            await _client
                .MovePageAsync(new ConfluencePageMove(move.PageId, move.Position, move.TargetId), cancellationToken)
                .ConfigureAwait(false);
        }

        var paths = group.Children.ToDictionary(child => child.PageId, child => child.Path, StringComparer.Ordinal);
        reorders.Add(new ChildReorder(
            group.ParentPath,
            group.ParentPageId,
            [.. moves.Select(move => paths[move.PageId])]));

        // Verified rather than assumed, because the diff was computed against an order Atlassian
        // documents no guarantee for (ConfluenceClient.GetChildPagesAsync). If the assumption is wrong
        // this is the sentence that says so, on the first real space, instead of a tree quietly in the
        // wrong order — and it costs one read per parent that actually needed moving.
        var settled = await _client.GetChildPagesAsync(group.ParentPageId, cancellationToken)
            .ConfigureAwait(false);

        if (ChildOrderPlanner.Plan(desired, [.. settled.Select(child => child.Id)]).Count > 0)
        {
            warnings.Add(
                $"the children of {group.ParentName} were reordered with {moves.Count} move(s), but "
                + "Confluence still lists them in an order the source tree does not ask for. The pages "
                + "and their content are published; only the position in the page tree is wrong. Re-run "
                + "to try again, or set the order by hand and publish with --no-reorder.");
        }
    }

    /// <summary>
    /// Every parent whose children the post-pass could reorder, in tree order, each carrying its
    /// children in the order the source tree wants them.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="PublishReport.Pages"/>, which is tree order — so a numeric prefix like
    /// <c>10-domains/</c> expresses the intent §6.2 asks the post-pass to honor, with no separate
    /// sorting rule to keep in step with the walk. Skipped and scope-excluded pages are included
    /// deliberately: a page this run did not write is still one of its parent's children, and leaving it
    /// out would compute the order of a list that does not exist.
    /// </remarks>
    private static List<ChildGroup> GroupChildren(
        PublishReport report,
        Dictionary<string, string> pageIds,
        string? rootPageId,
        List<string> warnings)
    {
        var groups = new List<ChildGroup>();
        var byParentId = new Dictionary<string, ChildGroup>(StringComparer.Ordinal);
        var unanchored = new List<string>();

        foreach (var planned in report.Pages)
        {
            if (!pageIds.TryGetValue(planned.Path, out var pageId))
            {
                // Never published, or published and then lost to a failure: it has no id to move.
                continue;
            }

            if (!TryResolveParentId(planned, pageIds, rootPageId, out var parentId))
            {
                continue;
            }

            if (parentId is not { Length: > 0 } parent)
            {
                unanchored.Add(planned.Path);
                continue;
            }

            if (!byParentId.TryGetValue(parent, out var group))
            {
                group = new ChildGroup(planned.ParentPath, parent, []);
                byParentId[parent] = group;
                groups.Add(group);
            }

            // Two paths claiming one page id is a corrupt state file rather than a tree
            // (PageHierarchy.PathsByPageId says the same). The first one wins, deterministically: a page
            // cannot be ordered against itself, and throwing here would turn a bad state file into a
            // crash after every page had already published.
            if (group.Children.All(child => !string.Equals(child.PageId, pageId, StringComparison.Ordinal)))
            {
                group.Children.Add(new ChildEntry(planned.Path, pageId));
            }
        }

        if (unanchored.Count > 1)
        {
            // Atlassian's own caution, and the one way this pass could do real damage: `before`/`after`
            // against a top-level target moves the page to the top level of the SPACE, where it does not
            // appear in the page tree at all. With no confluence.rootPageId there is no parent page whose
            // children these are, so there is no target that is certainly not top-level — and the pass
            // refuses rather than guessing, exactly as the reparent does.
            warnings.Add(
                $"the order of the {unanchored.Count} pages at the top of the wiki was left alone: no "
                + "confluence.rootPageId is set in docume.json, so they have no parent page whose children "
                + "they are, and positioning them relative to each other risks moving them out of the page "
                + "tree entirely. Set confluence.rootPageId to have the order reconciled.");
        }

        // One child cannot be out of order, and a read per parent to prove it is a read wasted on
        // every page of a deep, narrow wiki.
        return [.. groups.Where(group => group.Children.Count > 1)];
    }

    /// <summary>
    /// The full attachment set state records: the hash of what was just uploaded, and the plan's hash
    /// for everything that was already current (§5.3 stores what the page has, not what moved).
    /// </summary>
    private static Dictionary<string, string> AttachmentHashes(
        PlannedPage planned,
        List<(string Name, AttachmentContent Content)> materialized)
    {
        var uploaded = materialized.ToDictionary(
            item => item.Name, item => item.Content.Hash, StringComparer.Ordinal);

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attachment in planned.Attachments)
        {
            if (uploaded.TryGetValue(attachment.Name, out var hash))
            {
                hashes[attachment.Name] = hash;
                continue;
            }

            // A diagram with no plan-time hash is always in the upload set (PublishPlanner sees a name
            // state does not know), so reaching here means the plan and the upload set disagree — a
            // bug, and one that would otherwise write a page whose attachment state is a lie.
            hashes[attachment.Name] = attachment.ContentHash
                ?? throw new InvalidOperationException(
                    $"Attachment '{attachment.Name}' on {planned.Path} has no content hash and was not "
                    + "uploaded, so there is nothing to record for it.");
        }

        return hashes;
    }

    private static string RequireBody(PlannedPage planned, string? body) =>
        body
        ?? throw new InvalidOperationException(
            $"{planned.Path} plans a body write but the plan carries no body to upload.");

    /// <summary>
    /// The <c>ac:width</c> to publish for every diagram this page references (§7): measured from the SVG
    /// this run rendered, or carried forward from what state remembers publishing.
    /// </summary>
    /// <remarks>
    /// The second source is what keeps the attribute stable. A publish renders only the diagrams it
    /// uploads, so a text-only edit to a page holds no measurement for a diagram whose source did not
    /// change — and rendering one just to measure it would make every text edit depend on a working Node
    /// (<see cref="PageState.DiagramWidths"/>). Both sources go through
    /// <see cref="DiagramImageWidth.Pixels"/> rather than only the fresh one, so nothing reaches the
    /// published markup that is not a pixel count, including out of a hand-edited state file.
    /// </remarks>
    private static Dictionary<string, string> DiagramWidths(
        PlannedPage planned,
        PageState? current,
        List<(string Name, AttachmentContent Content)> materialized)
    {
        var rendered = materialized.ToDictionary(
            item => item.Name, item => item.Content, StringComparer.Ordinal);

        var widths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attachment in planned.Attachments)
        {
            if (attachment.Kind != AttachmentKind.Diagram)
            {
                continue;
            }

            var measured = rendered.TryGetValue(attachment.Name, out var content)
                ? content.SvgWidth
                : current?.DiagramWidths.GetValueOrDefault(attachment.Name);

            if (DiagramImageWidth.Pixels(measured) is { } pixels)
            {
                widths[attachment.Name] = pixels;
            }
        }

        return widths;
    }

    private static Dictionary<string, string> KnownPageIds(DocumeState state)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, page) in state.Pages)
        {
            if (page.PageId is { Length: > 0 } id)
            {
                ids[path] = id;
            }
        }

        return ids;
    }

    /// <summary>
    /// Maps a plan's parent path onto a page id, reading the ids this run has accumulated so that a
    /// child created after its parent lands under it (§6.2, parents before children).
    /// </summary>
    /// <returns>
    /// False when the parent is a page in the tree with no id — it failed earlier in this run. Filing
    /// the child anyway would put it somewhere the tree does not say, so the caller fails it instead.
    /// </returns>
    private static bool TryResolveParentId(
        PlannedPage planned,
        Dictionary<string, string> pageIds,
        string? rootPageId,
        out string? parentId)
    {
        if (planned.ParentPath is null)
        {
            parentId = rootPageId;
            return true;
        }

        var resolved = pageIds.TryGetValue(planned.ParentPath, out var id);
        parentId = id;

        return resolved;
    }

    /// <summary>
    /// Why a page whose parent id would not resolve cannot publish. Three reasons a parent can be
    /// missing, and only one of them is a failure to fix: a draft parent (§5.2) was held back on
    /// purpose, a scoped-out parent was excluded on purpose, and anything else is a parent whose own
    /// publish went wrong. Blaming a deliberate draft for "failing" would send the reader hunting for
    /// an error that never happened.
    /// </summary>
    private static string MissingParentMessage(
        string parentPath,
        HashSet<string> drafts,
        HashSet<string> excludedByScope)
    {
        if (drafts.Contains(parentPath))
        {
            return $"its parent page '{parentPath}' is a draft (publish: false) that has never been "
                + "published, so there is nothing to file this page under. Publish the parent, or move "
                + "this page out from under it.";
        }

        if (excludedByScope.Contains(parentPath))
        {
            return $"its parent page '{parentPath}' has never been published and this run's scope "
                + "excludes it, so there is nothing to file this page under. Widen the scope to include "
                + "the parent, or publish the whole tree once.";
        }

        return $"its parent page '{parentPath}' was not published in this run, so filing this page "
            + "would put it somewhere the tree does not say. Fix the parent's failure and re-run.";
    }

    /// <summary>
    /// The version message every body update carries: which tool wrote the version, from which commit,
    /// of which content — the page-side half of <see cref="WarnOnHandEdits"/>, since a version without
    /// this stamp in the history was not written by DocuMe.
    /// </summary>
    /// <remarks>
    /// The sha is the run's, not the page's: <see cref="PublishExecutionOptions.RepoSha"/> is optional
    /// (a publish outside a git checkout has none), and the stamp says what it knows. The content hash
    /// is §5.3's banner-excluded <see cref="ContentHash"/>, so the history entry names the same value
    /// state records — recoverable from Confluence alone if state.json is ever lost. The model notes an
    /// unverified community report of v2 dropping <c>version.message</c>; the stamp is provenance, not
    /// a mechanism anything depends on, so sending it costs nothing either way.
    /// </remarks>
    private static string ProvenanceMessage(string? repoSha, string contentHash) =>
        repoSha is { Length: > 0 } sha
            ? $"docume publish — repo {sha}, content {contentHash}"
            : $"docume publish — content {contentHash}";

    /// <summary>
    /// Rule §9.1 said out loud: a live version ahead of the one the last publish wrote means somebody
    /// edited the page in Confluence, and the body write that follows discards that edit.
    /// </summary>
    /// <remarks>
    /// A warning and never a refusal, on purpose. Overwriting hand edits is the design — the repo is the
    /// source of truth, and preserving a browser edit would need the page body read that rule §9.1
    /// forbids. What was missing is the word: the versions are already in hand (the optimistic-lock read
    /// above), so naming them costs no request, and the page history holds the diff for whoever wants
    /// to bring the edit back through a pull request. A page state without a recorded version — an
    /// adopted wiki before its first publish records one — proves nothing either way, so it stays quiet.
    /// </remarks>
    private static void WarnOnHandEdits(
        PlannedPage planned,
        PageState? current,
        int remoteVersion,
        List<string> warnings)
    {
        if (current is not { PublishedVersion: > 0 } recorded || remoteVersion <= recorded.PublishedVersion)
        {
            return;
        }

        warnings.Add(
            $"{planned.Path} was edited in Confluence after the last publish: the page is at version "
            + $"{remoteVersion} and the last publish wrote version {recorded.PublishedVersion}. The repo "
            + "is the source of truth, so this run overwrites those edits — the page history holds them "
            + "if something there is worth bringing back through a pull request.");
    }

    /// <summary>
    /// Reports a page whose recorded parent id is not the id this run resolved its parent to, for the
    /// actions that write no body.
    /// </summary>
    /// <remarks>
    /// The ordinary reparent — adding <c>a/README.md</c> above pages whose markdown did not change — is
    /// a <see cref="PagePublishAction.Move"/> now, decided in the plan (<see cref="PageHierarchy.ParentMoved"/>).
    /// What is left for this warning is the disagreement a plan cannot see, because a plan compares paths
    /// and this compares ids: a parent recreated under a new id earlier in this same run leaves its
    /// children pointing at the id state still records. Naming it is enough — state records the new id
    /// once the parent is written, so the next run reads the stale id as a move and performs it.
    /// </remarks>
    private static void WarnOnParentDrift(
        PlannedPage planned,
        DocumeState state,
        Dictionary<string, string> pageIds,
        string? rootPageId,
        List<string> warnings)
    {
        state.Pages.TryGetValue(planned.Path, out var current);

        if (TryResolveParentId(planned, pageIds, rootPageId, out var parentId))
        {
            WarnOnParentDrift(planned, current?.ParentPageId, parentId, warnings);
        }
    }

    private static void WarnOnParentDrift(
        PlannedPage planned,
        string? recordedParentId,
        string? plannedParentId,
        List<string> warnings)
    {
        if (string.Equals(recordedParentId, plannedParentId, StringComparison.Ordinal))
        {
            return;
        }

        warnings.Add(
            $"{planned.Path} is filed under page {Describe(recordedParentId)} in Confluence but this run "
            + $"resolved its parent as {Describe(plannedParentId)} — state's parent id is stale, most "
            + "often because the parent page was recreated. This run writes no body for it, so it was "
            + "left where it is; the next run moves it.");
    }

    private static string Describe(string? pageId) => pageId is { Length: > 0 } id ? id : "the space root";

    /// <summary>
    /// A transport failure said in words rather than as a stack trace. The resilience handler has
    /// already retried it (<see cref="ConfluenceHttp"/>), so by the time it reaches here the address or
    /// the network is wrong, not the moment.
    /// </summary>
    private static string Unreachable(Exception ex) =>
        $"Confluence could not be reached: {ex.Message} The request was already retried with backoff, so "
        + "check confluence.baseUrl and the network rather than re-running immediately.";

    private static string TimedOut(Exception ex) =>
        $"Confluence did not answer before the client timeout ran out: {ex.Message} A bulk publish can "
        + "raise it through ConfluenceClientOptions.Timeout.";

    /// <summary>
    /// What a Ctrl-C says. It names the count that survived because that is the question an operator who
    /// just interrupted a bulk publish has: re-running is safe, and it resumes rather than starting over.
    /// </summary>
    private static string Cancelled(string? path, int published)
    {
        var where = path is { Length: > 0 }
            ? $"the run was cancelled at '{path}'"
            : "the run was cancelled before any page was published";

        return $"{where}, with {published} page(s) published. Nothing after it was attempted, and what did "
            + "publish is recorded in state — re-run to carry on from there.";
    }

    private static string CancelledReorder(string parentName) =>
        $"the run was cancelled during the child-order pass, at {parentName}. Every page published and its "
        + "content is correct; the order under that parent and any after it was left as Confluence had it, "
        + "and the next run reconciles it.";

    private static string MediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".svg" => SvgMediaType,
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",

        // Confluence records whatever it is told; an unknown type downloads instead of rendering,
        // which is a visible degradation rather than a silent wrong one.
        _ => DefaultMediaType,
    };

    private async Task<string?> ResolveSpaceIdAsync(
        ConfluenceConfig confluence,
        CancellationToken cancellationToken)
    {
        // A configured id is trusted: it costs a request to confirm and the config is committed and
        // reviewed (§5.1).
        if (confluence.SpaceId is { Length: > 0 } configured)
        {
            return configured;
        }

        if (confluence.SpaceKey is not { Length: > 0 } key)
        {
            return null;
        }

        var space = await _client.FindSpaceByKeyAsync(key, cancellationToken).ConfigureAwait(false);

        return space?.Id;
    }

    private async Task<AttachmentContent> ContentForAsync(
        PlannedAttachment attachment,
        CancellationToken cancellationToken)
    {
        if (_content.TryGetValue(attachment.Name, out var cached))
        {
            return cached;
        }

        var content = attachment.Kind == AttachmentKind.Diagram
            ? await RenderAsync(attachment, cancellationToken).ConfigureAwait(false)
            : ReadAsset(attachment);

        _content[attachment.Name] = content;

        return content;
    }

    private AttachmentContent ReadAsset(PlannedAttachment attachment)
    {
        var assetPath = attachment.AssetPath
            ?? throw new InvalidOperationException(
                $"Attachment '{attachment.Name}' is an asset with no path to read.");

        var bytes = File.ReadAllBytes(
            Path.Combine(_wikiRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));

        return new AttachmentContent(bytes, ContentHash.OfBytes(bytes), MediaType(attachment.Name));
    }

    private async Task<AttachmentContent> RenderAsync(
        PlannedAttachment attachment,
        CancellationToken cancellationToken)
    {
        var source = attachment.DiagramSource
            ?? throw new InvalidOperationException(
                $"Attachment '{attachment.Name}' is a diagram with no source to render.");

        if (_renderDiagram is null)
        {
            throw new MermaidRenderException(
                $"'{attachment.Name}' has to be rendered before it can be published, but this run was "
                + "given no mermaid renderer. Publishing the page without it would leave a broken image "
                + "on it.",
                MermaidRenderFault.Setup);
        }

        var diagram = await _renderDiagram(source, cancellationToken).ConfigureAwait(false);

        // The body already references the name the resolver derived, so a renderer that named the file
        // differently would publish a page pointing at an attachment nobody uploaded.
        if (!string.Equals(diagram.AttachmentFilename, attachment.Name, StringComparison.Ordinal))
        {
            throw new MermaidRenderException(
                $"The renderer named this diagram '{diagram.AttachmentFilename}' but the page body "
                + $"references '{attachment.Name}'. Publishing it would leave a broken image on the page.",
                MermaidRenderFault.Setup);
        }

        var bytes = Encoding.UTF8.GetBytes(diagram.Svg);

        return new AttachmentContent(bytes, ContentHash.OfBytes(bytes), SvgMediaType, diagram.SvgWidth);
    }

    /// <summary>
    /// Removes a label, treating "it is not there" as done. §8 wants the label's <em>absence</em>, and
    /// a page recreated moments ago — or one a reviewer un-labelled by hand — already has that.
    /// </summary>
    private async Task RemoveLabelIfPresentAsync(
        string pageId,
        string label,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.RemoveLabelAsync(pageId, label, cancellationToken).ConfigureAwait(false);
        }
        catch (ConfluenceApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Nothing to do and nothing to report: the state this step exists to produce already holds.
        }
    }

    /// <summary>
    /// Posts §6.2 step 7's footer comment on a page whose approval this run just revoked
    /// (<c>--notify-reviewers</c>). Answers whether it was posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A comment that fails warns; it never fails the page.</strong> By the time this runs the body
    /// is published, the <c>approved</c> label is off and state says <c>needs-review</c>, so the re-review
    /// §8 asks for is already required and the dashboard already shows it. Reporting a failure here would
    /// mark a page as unpublished when it is published, and the next run would rewrite a page that is
    /// already correct to retry a notification.
    /// </para>
    /// <para>
    /// The body is a fixed string, so nothing here needs escaping: naming the page would mean putting a
    /// title into storage-format XML, and the comment is already on the page it is about.
    /// </para>
    /// </remarks>
    private async Task<bool> NotifyReviewersAsync(
        string path,
        string pageId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.CreateFooterCommentAsync(pageId, ReviewRequestComment, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (ConfluenceApiException ex)
        {
            warnings.Add(
                $"{path}: the approval was revoked but --notify-reviewers could not post the comment "
                + $"({ex.Message}). The `approved` label is off and state says needs-review, so the page "
                + "still reads as awaiting review everywhere else.");

            return false;
        }
    }

    /// <summary>
    /// Stamps the managed-marker property on a page this run just wrote
    /// (<see cref="ManagedMarker"/>), answering whether the stamp is on the page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A stamp that fails warns; it never fails the page.</strong> By the time this runs the
    /// body is published and correct, and what is missing is provenance: the property <c>--prune</c>
    /// reads before it deletes. Failing the page would report an unpublished page that is published,
    /// and the marker has a retry of its own, because <c>false</c> here keeps
    /// <see cref="PageState.Marked"/> false and the next body update stamps again. The same contract as
    /// <see cref="NotifyReviewersAsync"/>, and for the same reason.
    /// </para>
    /// <para>
    /// The filter is the one <see cref="GuardOpenCommentsAsync"/> carries: an expired token is the
    /// credential failing, not the stamp, so it propagates and the run stops (rule §1.2).
    /// </para>
    /// </remarks>
    private async Task<bool> StampManagedMarkerAsync(
        string path,
        string pageId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client
                .CreatePagePropertyAsync(pageId, ManagedMarker.Key, ManagedMarker.ValueFor(path), cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (ConfluenceException ex) when (ex is not ConfluenceAuthenticationException)
        {
            // A 400 here is usually the property already existing: a create replayed by the retry
            // pipeline after a response was lost, a hand-edited state.json that forgot a stamp that
            // landed, or a re-adopted wiki whose pages were stamped in a previous life. One read
            // settles it, and a marker that is already on the page is a stamp that succeeded — left
            // undifferentiated, the page would warn on every body update forever, with advice
            // ("--prune refuses") that the live property makes false.
            if (await MarkerAlreadyPresentAsync(pageId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            warnings.Add(
                $"{path}: the page published but its managed marker could not be stamped ({ex.Message}). "
                + "The next body update tries again; until then --prune refuses to delete the page, which "
                + "is the safe side of the miss.");

            return false;
        }
    }

    /// <summary>
    /// Whether the page already carries a managed marker — the read that turns an already-exists 400
    /// into the success it is. Best effort by construction: a read that fails proves nothing, and
    /// "nothing proven" keeps the original failure's answer.
    /// </summary>
    private async Task<bool> MarkerAlreadyPresentAsync(string pageId, CancellationToken cancellationToken)
    {
        try
        {
            var marker = await _client
                .FindPagePropertyAsync(pageId, ManagedMarker.Key, cancellationToken)
                .ConfigureAwait(false);

            return marker is not null && ManagedMarker.IsManaged(marker.RawValue);
        }
        catch (ConfluenceException ex) when (ex is not ConfluenceAuthenticationException)
        {
            return false;
        }
    }

    /// <summary>An attachment's bytes with the hash state will record and the media type to send.</summary>
    /// <param name="SvgWidth">
    /// For a rendered diagram, <see cref="MermaidDiagram.SvgWidth"/> — what §7's <c>ac:width</c> is
    /// measured from (<see cref="DiagramImageWidth"/>). Null for an asset: an author's image carries its
    /// width in the markdown, which is the converter's business.
    /// </param>
    private sealed record AttachmentContent(
        ReadOnlyMemory<byte> Bytes,
        string Hash,
        string ContentType,
        string? SvgWidth = null);

    /// <summary>One of a parent's children: the id a move needs, and the path a report names.</summary>
    private sealed record ChildEntry(string Path, string PageId);

    /// <summary>
    /// One parent and the children the source tree files under it, in the order it wants them.
    /// </summary>
    private sealed record ChildGroup(string? ParentPath, string ParentPageId, List<ChildEntry> Children)
    {
        /// <summary>The parent as a message names it: its path, or where it sits when it has none.</summary>
        public string ParentName => ParentPath ?? "the top of the wiki";
    }

    /// <summary>
    /// One page's result: the new state plus either what happened or why it did not. A record rather
    /// than an exception because a failed page is an ordinary outcome of a bulk publish, and the state
    /// accumulated so far must survive it.
    /// </summary>
    private sealed record PageOutcome(DocumeState State, PagePublishResult? Result, string? Failure);
}
