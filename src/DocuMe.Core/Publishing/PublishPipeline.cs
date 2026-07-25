using DocuMe.Core.Config;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;

namespace DocuMe.Core.Publishing;

/// <summary>Per-run switches a <see cref="PublishReport"/> depends on (PLAN.md §6.2).</summary>
public sealed record PublishOptions
{
    /// <summary>
    /// <c>--force</c>: republish every page even when nothing moved, re-uploading its attachments.
    /// Never invalidates approval on its own — see <see cref="PublishPlanner.PlanPage"/>.
    /// </summary>
    public bool Force { get; init; }

    /// <summary><c>--allow-protected-space</c>: unlock a space listed in <c>confluence.protectedSpaces</c>.</summary>
    public bool AllowProtectedSpace { get; init; }

    /// <summary>
    /// <c>--changed-since &lt;sha&gt;</c> or <c>--page &lt;path&gt;</c>: the files this run may write, or
    /// <c>null</c> for the whole tree.
    /// </summary>
    /// <remarks>
    /// It narrows the write set alone — the tree is still walked whole, so orphan detection and the link
    /// map still see every page. <see cref="PublishScope"/> says why that distinction is the whole design.
    /// </remarks>
    public PublishScope? Scope { get; init; }

    /// <summary>
    /// The date the §8 banner records, or <c>null</c> to omit it.
    /// </summary>
    /// <remarks>
    /// The caller's decision, on purpose: one value for the whole run, so a 79-page publish does not
    /// straddle midnight and produce two banners. Read it in UTC
    /// (<c>DateOnly.FromDateTime(DateTime.UtcNow)</c>) so a laptop and a CI runner in different zones
    /// publish the same banner. <see cref="PageBanner"/> refuses to read a clock for the same reason.
    /// </remarks>
    public DateOnly? GeneratedOn { get; init; }
}

/// <summary>
/// Composes PLAN.md §6.2 into one plan: walk the tree, convert every page, hash it, decide what the
/// run does with it, and inject the §8 banner into the body a real run would upload.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Planning writes nothing and calls nothing.</strong> No Confluence request, no credentials,
/// no state file write, not even a rendered diagram — which is what makes <c>--dry-run</c> exactly
/// the real run minus its side effects, rather than a second code path that can drift from it. Every
/// decision comes from <see cref="PublishPlanner"/>, which is pure by construction.
/// </para>
/// <para>
/// <strong>The link map is built before any page converts.</strong> A relative <c>.md</c> link
/// resolves to a page <em>title</em>, so a page cannot be converted until every page's title is
/// known; <see cref="WikiTree.Load"/> does that whole-tree pass, and this type consumes its
/// resolvers (§6.2 steps 1-2).
/// </para>
/// <para>
/// <strong>Hash before banner.</strong> The hash preimage is the converter's output; the banner is
/// injected afterwards, into a separate string. Reversing that order would put a per-publish date
/// inside the hash and revoke approval on every approved page in the wiki (§8, rule §9.2).
/// </para>
/// </remarks>
public static class PublishPipeline
{
    /// <summary>
    /// Stand-in hash for a diagram whose SVG has not been rendered and that state has never seen.
    /// </summary>
    /// <remarks>
    /// Never compared against a real hash: the name is absent from <c>state.json</c>, so
    /// <see cref="PublishPlanner"/> already counts it as an upload whatever this value is. Never
    /// stored either — it does not escape <see cref="Plan"/>, and it is deliberately not spelled like
    /// a <c>sha256:</c> value so a leak into state would be obvious rather than plausible.
    /// </remarks>
    private const string UnrenderedDiagram = "unrendered-diagram";

    /// <summary>
    /// Plans a publish run over <paramref name="tree"/>.
    /// </summary>
    /// <param name="config">The consumer repo's <c>docume.json</c> (§5.1).</param>
    /// <param name="tree">The loaded wiki tree — the link map of §6.2 step 2.</param>
    /// <param name="state">
    /// The loaded <c>_meta/state.json</c> (§5.3). Pass <c>new DocumeState()</c> for a first publish:
    /// every page then plans as a create, which is what a missing state file means.
    /// </param>
    /// <param name="options">Per-run switches; defaults when omitted.</param>
    public static PublishReport Plan(
        DocumeConfig config,
        WikiTree tree,
        DocumeState state,
        PublishOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(state);
        options ??= new PublishOptions();

        // One banner for the run: §8's panel is identical on every page, and a per-page instance
        // would invite a per-page date.
        var banner = new PageBanner
        {
            BaselineSha = state.BaselineSha,
            GeneratedOn = options.GeneratedOn,
            DashboardTitle = config.Dashboard.Title,
        };

        // Whole-tree, before the per-page loop, for the same reason as the link map: a page's parent
        // is a fact about the tree, not about the page (§6.2, PageHierarchy).
        var parents = PageHierarchy.Resolve(tree.Pages.Select(page => page.Path), config.Wiki.HomePage);

        var pages = new List<PlannedPage>();
        var failures = new List<PageConversionFailure>();

        foreach (var page in tree.Pages)
        {
            var planned = PlanOne(tree, state, page, parents[page.Path], banner, options, failures);
            if (planned is not null)
            {
                pages.Add(planned);
            }
        }

        // Orphans stay whole-tree even under a scope: an orphan is a state entry whose file is gone, and a
        // scope hides no file (PublishScope).
        return new PublishReport(
            config.Confluence.SpaceKey,
            options.GeneratedOn,
            pages,
            failures,
            PublishPlanner.OrphanPages(state, tree.Pages.Select(page => page.Path)),
            PublishGuard.WriteRefusal(config.Confluence, options.AllowProtectedSpace),
            options.Scope);
    }

    /// <summary>
    /// Converts and plans one page, or appends to <paramref name="failures"/> and returns
    /// <c>null</c> when the converter refuses it.
    /// </summary>
    private static PlannedPage? PlanOne(
        WikiTree tree,
        DocumeState state,
        WikiPage page,
        string? parentPath,
        PageBanner banner,
        PublishOptions options,
        List<PageConversionFailure> failures)
    {
        var resolvers = tree.ResolversFor(page.Path);
        state.Pages.TryGetValue(page.Path, out var current);

        // Wrapping the two attachment resolvers is how the pipeline learns its upload set: the
        // converter already visits every image and every mermaid fence, so there is no second parse
        // of the markdown and no reference that the body cites but the plan misses.
        var attachments = new Dictionary<string, PlannedAttachment>(StringComparer.Ordinal);
        var diagnostics = new List<ConversionDiagnostic>();

        string? Attachment(string reference)
        {
            var name = resolvers.Attachment(reference);
            if (name is null || attachments.ContainsKey(name))
            {
                return name;
            }

            // The resolver answered, so the reference names an asset in this tree. Resolving it a
            // second time is how the pipeline learns WHICH file to read: the delegate returns the
            // upload name, and flattening is not invertible (images/a_b.png and images_a/b.png).
            var assetPath = WikiTree.ResolveAgainst(page.Path, reference)!;
            var bytes = File.ReadAllBytes(
                Path.Combine(tree.Root, assetPath.Replace('/', Path.DirectorySeparatorChar)));

            attachments[name] = new PlannedAttachment(
                name, AttachmentKind.Asset, assetPath, null, ContentHash.OfBytes(bytes));

            return name;
        }

        string? Diagram(string mermaidSource)
        {
            var name = resolvers.Diagram(mermaidSource);
            if (name is null || attachments.ContainsKey(name))
            {
                return name;
            }

            // A diagram's filename IS a hash of its source, so a name state already knows means the
            // same source and — for a deterministic renderer — the same SVG: carrying state's hash
            // forward reports "unchanged" without shelling out to Node. A name state does not know
            // is a new diagram, which uploads regardless of what its bytes turn out to be. The one
            // gap is a renderer upgrade that changes the SVG for an unchanged source; a real run
            // hashes the bytes it rendered and catches that, a plan under-reports it by one upload.
            attachments[name] = new PlannedAttachment(
                name,
                AttachmentKind.Diagram,
                null,
                mermaidSource,
                current?.Attachments.GetValueOrDefault(name));

            return name;
        }

        string body;
        try
        {
            body = ConfluenceStorageConverter.Convert(
                page.Parsed.Body, resolvers.Link, Attachment, Diagram, diagnostics);
        }
        catch (NotSupportedException ex)
        {
            // The converter's fail-loud contract (§7) is the only exception it throws by design. The
            // run collects every refused page instead of stopping at the first, so one command shows
            // an author everything that has to change.
            failures.Add(new PageConversionFailure(page.Path, ex.Message));
            return null;
        }

        var contentHash = ContentHash.OfBody(body);
        var plan = PublishPlanner.PlanPage(
            page.Path, current, contentHash, PlanningHashes(attachments), options.Force);

        // The scope is applied here, to the DECISION, and nowhere earlier: the page has already been
        // converted, hashed and planned, so everything a full run knows about it is known. What changes is
        // that a page outside the scope is skipped rather than written — no body, no uploads, and no
        // approval revoked, because a run that writes nothing to a page cannot have invalidated it (§8).
        var excluded = options.Scope is { } scope
            && plan.Action != PagePublishAction.Skip
            && !scope.Includes(page.Path, attachments.Values);

        if (excluded)
        {
            plan = plan with
            {
                Action = PagePublishAction.Skip,
                ChangedAttachments = [],
                InvalidatesApproval = false,
            };
        }

        return new PlannedPage(
            page.Path,
            page.Title,
            parentPath,
            plan,
            plan.WritesBody ? banner.InjectInto(body) : null,
            [.. attachments.Values.OrderBy(attachment => attachment.Name, StringComparer.Ordinal)],
            diagnostics)
        {
            ExcludedByScope = excluded,
        };
    }

    private static Dictionary<string, string> PlanningHashes(
        Dictionary<string, PlannedAttachment> attachments) =>
        attachments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ContentHash ?? UnrenderedDiagram,
            StringComparer.Ordinal);
}
