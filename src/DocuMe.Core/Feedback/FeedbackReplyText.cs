using DocuMe.Core.Markdown;

namespace DocuMe.Core.Feedback;

/// <summary>
/// Composes the storage-format body of a reply to a triaged comment (PLAN.md §9 step 5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The reviewer is told which of the three things happened, not just "done".</strong> §9 step 5
/// quotes one sentence — "Fixed in the latest version — thanks" — and that one is kept verbatim for
/// <see cref="FeedbackStatus.Fixed"/>. The other two triage outcomes need their own: a
/// <see cref="FeedbackStatus.Rejected"/> comment answered with "fixed" is a lie, and a
/// <see cref="FeedbackStatus.Question"/> answered with silence is how an open question gets forgotten.
/// </para>
/// <para>
/// <strong>Everything variable is escaped through <see cref="ConfluenceStorageRenderer"/></strong>, the
/// same discipline <see cref="PageBanner"/> uses. The resolution text is written by
/// <c>/docs-feedback</c> and reviewed by a human in the PR before it can reach here, so it is not
/// untrusted in the §1.3 sense — but it is free text authored elsewhere, and a <c>&lt;</c> in it would
/// produce a body Confluence rejects as malformed storage format. Escaping is about the markup, not the
/// trust.
/// </para>
/// <para>
/// <strong>Nothing from the comment itself is echoed back.</strong> The body and the quoted text are
/// untrusted input (rule §1.3); a reply that quoted them would take text DocuMe was told never to
/// interpret and render it into a page. The reviewer knows what they wrote.
/// </para>
/// </remarks>
public static class FeedbackReplyText
{
    /// <summary>§9 step 5's own wording, kept as it is written in the plan.</summary>
    private const string FixedSentence = "Fixed in the latest version — thanks.";

    private const string RejectedSentence =
        "Thanks for the note. After checking it against the code, the page is staying as it is.";

    private const string QuestionSentence =
        "Thanks for the question. It is recorded as an open point in the repo's docs backlog "
        + "(_meta/GAPS.md) rather than answered here.";

    /// <summary>
    /// The line that tells a human a machine wrote this. Short and last: a reviewer should be able to
    /// tell an automated answer from a colleague's without reading to the end of a paragraph.
    /// </summary>
    private const string Signature =
        "Posted automatically by DocuMe when this feedback was processed. The page is generated from "
        + "the repository, so replies here do not change it.";

    /// <summary>Whether a triage status is one the reply pass answers at all.</summary>
    /// <remarks>
    /// <see cref="FeedbackStatus.New"/> is not: it means <c>/docs-feedback</c> has not looked yet, and an
    /// unknown value is treated the same way. Answering a status this build does not understand would put
    /// a wrong sentence under a reviewer's comment.
    /// </remarks>
    public static bool IsTriaged(string? status) => status is
        FeedbackStatus.Fixed or FeedbackStatus.Rejected or FeedbackStatus.Question;

    /// <summary>
    /// The storage-format reply for <paramref name="status"/>, with <paramref name="resolution"/> as its
    /// own paragraph when the triage recorded one.
    /// </summary>
    /// <param name="status">A status <see cref="IsTriaged"/> accepts.</param>
    /// <param name="resolution">The item's <c>resolution</c>, or <c>null</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not a triaged status.</exception>
    public static string Compose(string? status, string? resolution)
    {
        var opening = status switch
        {
            FeedbackStatus.Fixed => FixedSentence,
            FeedbackStatus.Rejected => RejectedSentence,
            FeedbackStatus.Question => QuestionSentence,
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Only a triaged item is answered; check IsTriaged first."),
        };

        using var writer = new StringWriter();

        // The full renderer, for its escaping discipline alone — nothing is rendered from a document
        // here, so its object renderers are never reached (same use as PageBanner).
        var renderer = new ConfluenceStorageRenderer(writer);

        renderer.Write("<p>").WriteEscaped(opening).Write("</p>");

        if (resolution is { Length: > 0 } && !resolution.AsSpan().IsWhiteSpace())
        {
            renderer.Write("<p>").WriteEscaped(resolution.Trim()).Write("</p>");
        }

        renderer.Write("<p><em>").WriteEscaped(Signature).Write("</em></p>");

        writer.Flush();

        return writer.ToString();
    }
}
