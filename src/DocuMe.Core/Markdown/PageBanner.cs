using System.Globalization;

namespace DocuMe.Core.Markdown;

/// <summary>
/// The PLAN.md §8 <em>banner</em>: the storage-format info panel injected at the top of every
/// published page. Machine-owned, carrying generation provenance (baseline SHA, date) and the
/// sentence that points a reader at where review status actually lives.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It is outside the content hash, and that is the whole point.</strong> §8 requires that
/// banner-only and machine edits never invalidate approval (rule §9.2), so the publish pipeline
/// computes <see cref="State.ContentHash.OfBody"/> over the converter's output and only then
/// injects the banner (§6.2 step 5, in that order). This type is therefore deliberately
/// <em>not</em> part of <see cref="ConfluenceStorageConverter.Convert"/>: the converter's output
/// is the hash preimage, so a banner emitted from inside it would revoke approval on every
/// approved page in the wiki and look like a content change while doing it.
/// </para>
/// <para>
/// <strong>Static per publish.</strong> §8 spends the banner on provenance only; live review
/// status lives in page labels and the dashboard (§6.5). A status flip must therefore never
/// rewrite a page body, which is the same no-page-version-churn rule as §9.3. Nothing here reads
/// state, labels or approval.
/// </para>
/// <para>
/// <strong>The date is a parameter, never a clock read here.</strong> A banner carrying today's
/// date changes on every publish that writes a body, so the value has to be pinnable by a test
/// and identical across the pages of one run. It reaches the page, never the hash.
/// </para>
/// <para>
/// Both consumer-supplied strings (<see cref="BaselineSha"/> from <c>state.json</c>,
/// <see cref="DashboardTitle"/> from <c>docume.json</c> §5.1) are escaped through
/// <see cref="ConfluenceStorageRenderer"/> rather than concatenated raw: a second escaping
/// implementation is a second thing to get wrong, and a stray <c>&amp;</c> in a page title is
/// enough to make Confluence reject the whole body.
/// </para>
/// </remarks>
public sealed record PageBanner
{
    /// <summary>
    /// Read as one sentence with the provenance clause. States §9.1 (the repo is the source of
    /// truth; hand edits are lost on republish) on the page itself, where the person about to
    /// make that edit is looking.
    /// </summary>
    private const string SourceOfTruthSentence =
        "Edit the source in the repository; changes made on this page are overwritten by the next publish.";

    /// <summary>
    /// The repo commit the wiki was generated against (§5.3 <c>baselineSha</c>). Omitted from the
    /// banner when <c>null</c> or empty, which is what a first publish before any generation run
    /// looks like.
    /// </summary>
    public string? BaselineSha { get; init; }

    /// <summary>
    /// The generation date the banner records, rendered as an ISO <c>yyyy-MM-dd</c> date so it
    /// reads the same for every reader. Omitted when <c>null</c>.
    /// </summary>
    public DateOnly? GeneratedOn { get; init; }

    /// <summary>
    /// Title of the dashboard page the review-status sentence links to (§5.1
    /// <c>dashboard.title</c>). When <c>null</c> or empty the sentence names labels only rather
    /// than linking a page that may not exist: a Confluence page link to a missing title renders
    /// as a broken link on all 79 pages.
    /// </summary>
    public string? DashboardTitle { get; init; }

    /// <summary>
    /// Renders the banner as a storage-format fragment, with no dependency on the page it will
    /// sit on. Deterministic: same inputs, same bytes.
    /// </summary>
    public string Render()
    {
        using var writer = new StringWriter();

        // The full renderer, for its escaping discipline (WriteEscaped / WriteAttributeEscaped).
        // Its object renderers are never reached — nothing is rendered from a document here.
        var renderer = new ConfluenceStorageRenderer(writer);

        // `icon` is Confluence's own default for the info panel; written explicitly for the same
        // reason ConfluenceStorageRenderer writes it on GitHub alert panels — not relying on a
        // server-side default a site could in principle change.
        renderer.Write("<ac:structured-macro ac:name=\"info\">")
            .Write("<ac:parameter ac:name=\"icon\">true</ac:parameter>")
            .Write("<ac:rich-text-body>")
            .Write('\n')
            .Write("<p>");

        WriteProvenance(renderer);

        renderer.Write("</p>").Write('\n').Write("<p>");

        WriteReviewStatus(renderer);

        renderer.Write("</p>")
            .Write('\n')
            .Write("</ac:rich-text-body></ac:structured-macro>");

        writer.Flush();
        return writer.ToString();
    }

    /// <summary>
    /// Returns what a publish uploads: the banner above <paramref name="storageFormat"/>
    /// (§6.2 step 5, "inject the banner above the body").
    /// </summary>
    /// <param name="storageFormat">
    /// The converter's output for one page, and the hash preimage. Must already have been hashed:
    /// the returned string is a different preimage, so hashing it instead would silently break
    /// approval invalidation rather than fail.
    /// </param>
    public string InjectInto(string storageFormat)
    {
        ArgumentNullException.ThrowIfNull(storageFormat);

        return string.Concat(Render(), "\n", storageFormat);
    }

    private void WriteProvenance(ConfluenceStorageRenderer renderer)
    {
        renderer.Write("Generated by DocuMe");

        var baselineSha = BaselineSha;
        if (baselineSha is { Length: > 0 })
        {
            renderer.Write(" from commit ").WriteEscaped(baselineSha);
        }

        if (GeneratedOn is { } generatedOn)
        {
            renderer.Write(" on ")
                .Write(generatedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        renderer.Write(". ").Write(SourceOfTruthSentence);
    }

    private void WriteReviewStatus(ConfluenceStorageRenderer renderer)
    {
        renderer.Write("Review status is shown by page labels");

        var dashboardTitle = DashboardTitle;
        if (dashboardTitle is not { Length: > 0 })
        {
            renderer.Write('.');
            return;
        }

        renderer.Write(" and the <ac:link><ri:page ri:content-title=\"")
            .WriteAttributeEscaped(dashboardTitle)
            .Write("\"/><ac:link-body>")
            .WriteEscaped(dashboardTitle)
            .Write("</ac:link-body></ac:link> page.");
    }
}
