using System.Net;
using System.Text;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;

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

/// <summary>Per-run inputs the write path needs that a plan cannot carry.</summary>
public sealed record PublishExecutionOptions
{
    /// <summary>
    /// The repo commit this run publishes, stamped into state as <c>lastPublishedSha</c> (§6.2 step 8),
    /// or <c>null</c> to leave the previous value alone. Written only when the run finishes clean:
    /// "the wiki is published at this commit" is false if any page failed.
    /// </summary>
    public string? RepoSha { get; init; }
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

        PublishOutcome Outcome(string? stoppedBecause) => new(
            state, !ReferenceEquals(state, original), results, failures, warnings, stoppedBecause);

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
            cancellationToken.ThrowIfCancellationRequested();

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
                var orphaned = excludedByScope.Contains(planned.ParentPath!)
                    ? $"its parent page '{planned.ParentPath}' has never been published and this run's "
                        + "scope excludes it, so there is nothing to file this page under. Widen the scope "
                        + "to include the parent, or publish the whole tree once."
                    : $"its parent page '{planned.ParentPath}' was not published in this run, so filing "
                        + "this page would put it somewhere the tree does not say. Fix the parent's "
                        + "failure and re-run.";

                failures.Add(new PagePublishFailure(planned.Path, orphaned));
                continue;
            }

            PageOutcome outcome;
            try
            {
                outcome = await PublishPageAsync(
                        config, planned, state, spaceId, parentId, warnings, cancellationToken)
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

        if (failures.Count == 0 && options.RepoSha is { Length: > 0 } sha)
        {
            state = StateUpdates.RecordLastPublishedSha(state, sha);
        }

        return Outcome(null);
    }

    /// <summary>Publishes one page: page write, attachment uploads, approval, state (§6.2 steps 5-8).</summary>
    private async Task<PageOutcome> PublishPageAsync(
        DocumeConfig config,
        PlannedPage planned,
        DocumeState state,
        string spaceId,
        string? parentId,
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

        int version;
        if (creating)
        {
            var created = await _client
                .CreatePageAsync(
                    new ConfluencePageDraft(spaceId, planned.Title, RequireBody(planned), parentId),
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
        }
        else if (plan.WritesBody)
        {
            var updated = await _client
                .UpdatePageAsync(
                    new ConfluencePageRevision(
                        pageId!, planned.Title, RequireBody(planned), remoteVersion, parentId),
                    cancellationToken)
                .ConfigureAwait(false);

            version = updated.Version;
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
        if (plan.InvalidatesApproval)
        {
            await RemoveLabelIfPresentAsync(pageId!, config.Labels.Approved, cancellationToken)
                .ConfigureAwait(false);
            state = StateUpdates.InvalidateApproval(state, planned.Path);
            revoked = true;
        }

        var published = new PublishedPage(
            pageId!,
            planned.Title,
            parentId,
            plan.ContentHash,
            version,
            AttachmentHashes(planned, materialized));

        state = StateUpdates.RecordPublish(state, planned.Path, published);

        return new PageOutcome(
            state,
            new PagePublishResult(
                planned.Path, planned.Title, plan.Action, pageId!, version, uploaded, revoked, recreate),
            null);
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

    private static string RequireBody(PlannedPage planned) =>
        planned.UploadBody
        ?? throw new InvalidOperationException(
            $"{planned.Path} plans a body write but the plan carries no body to upload.");

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

        return new AttachmentContent(bytes, ContentHash.OfBytes(bytes), SvgMediaType);
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

    /// <summary>An attachment's bytes with the hash state will record and the media type to send.</summary>
    private sealed record AttachmentContent(ReadOnlyMemory<byte> Bytes, string Hash, string ContentType);

    /// <summary>
    /// One page's result: the new state plus either what happened or why it did not. A record rather
    /// than an exception because a failed page is an ordinary outcome of a bulk publish, and the state
    /// accumulated so far must survive it.
    /// </summary>
    private sealed record PageOutcome(DocumeState State, PagePublishResult? Result, string? Failure);
}
