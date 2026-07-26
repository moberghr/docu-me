using System.Globalization;
using System.Text;

namespace DocuMe.Core.Feedback;

/// <summary>
/// One piece of feedback about one page, as it is written to
/// <c>&lt;wiki.root&gt;/_meta/feedback/inbox/&lt;pageSlug&gt;-&lt;commentId&gt;.json</c> (PLAN.md §5.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is the pluggable seam of the whole feedback loop</strong> (§9). Confluence comments
/// are the only intake in v1, but nothing in this shape mentions Confluence: a future channel — Jira, a
/// support inbox — produces the same item and <c>/docs-feedback</c> triages it the same way. Hence
/// <see cref="Id"/> carries a channel prefix rather than a bare comment id.
/// </para>
/// <para>
/// <strong><see cref="Body"/> and <see cref="QuotedText"/> are untrusted input</strong> (CLAUDE.md §0.2,
/// rule §1.3). They are copied verbatim out of the API response and nothing in the CLI reads them: the
/// tool writes them down, and the skill that reads them treats them as claims to verify against the
/// code, never as instructions. Storing them raw is the point — a "cleaned up" body is a body something
/// has already interpreted.
/// </para>
/// <para>
/// <strong>Committed, so it is written for a human reading a PR diff</strong> (§5.4: "Inbox is
/// committed → auditable, works across machines"). Members that have no value are omitted rather than
/// written as <c>null</c>, which is how <see cref="Json.DocumeJson"/> writes every DocuMe data file:
/// a footer item carries no <c>quotedText</c> line at all, and <c>resolution</c> appears when
/// <c>/docs-feedback</c> fills it in.
/// </para>
/// </remarks>
public sealed record FeedbackItem
{
    /// <summary>Channel-prefixed id, e.g. <c>conf-comment-987654</c> (see <see cref="FeedbackItemId"/>).</summary>
    public string? Id { get; init; }

    /// <summary>The wiki-relative markdown path the feedback is about, as state.json keys it (§5.3).</summary>
    public string? Page { get; init; }

    /// <summary><c>inline</c> | <c>footer</c> — see <see cref="FeedbackKind"/>.</summary>
    public string? Kind { get; init; }

    /// <summary>
    /// Who wrote it: a display name where Confluence answered one, otherwise the account id, otherwise
    /// <see cref="FeedbackAuthor.Unknown"/>. Never DocuMe's own account.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>When it was written, ISO-8601 UTC (<see cref="FeedbackTimestamp.Format"/>).</summary>
    public string? CreatedAt { get; init; }

    /// <summary>
    /// The page text an inline comment is anchored to, verbatim. Absent on a footer item, which is
    /// anchored to nothing.
    /// </summary>
    public string? QuotedText { get; init; }

    /// <summary>The feedback itself, verbatim, in Confluence storage format. Untrusted input.</summary>
    public string? Body { get; init; }

    /// <summary><c>new</c> | <c>fixed</c> | <c>rejected</c> | <c>question</c> — see <see cref="FeedbackStatus"/>.</summary>
    public string? Status { get; init; }

    /// <summary>What was done about it. Written by <c>/docs-feedback</c>, never by the CLI (§9).</summary>
    public string? Resolution { get; init; }

    /// <summary>
    /// When <c>docume sync --reply</c> answered the comment in Confluence, in
    /// <see cref="FeedbackTimestamp.Format"/>; absent until it has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one member the CLI writes after ingestion, and the only thing that stops a second
    /// reply.</strong> §9 step 5 runs on a cron over items whose triage happened in an earlier PR, so
    /// "which of these have I already answered" has to survive between runs, and it cannot be derived:
    /// <see cref="Status"/> says what the triage decided, not whether the reviewer was told. Without this
    /// field every cron would post "Fixed in the latest version" again under the same comment.
    /// </para>
    /// <para>
    /// <strong>It lives on the item rather than in <c>state.json</c></strong> because it is a fact about
    /// this one piece of feedback, it reads in the PR diff next to the triage it answers, and an item
    /// moved to <c>_meta/feedback/archive/</c> (§5.4) carries it along. A parallel list of answered
    /// comment ids in state would have to be kept in step with a file that moves.
    /// </para>
    /// <para>
    /// <strong>Not in §5.4's shape</strong>, which predates the reply step being built; recorded as a
    /// pending PLAN edit rather than silently diverging.
    /// </para>
    /// </remarks>
    public string? RepliedAt { get; init; }
}

/// <summary>
/// The two values <see cref="FeedbackItem.Kind"/> takes (PLAN.md §5.4). Constants rather than an enum
/// for the reason <see cref="State.ApprovalStatus"/> is: the file spells them as-is and stays
/// hand-readable.
/// </summary>
public static class FeedbackKind
{
    /// <summary>A comment anchored to a span of the page body.</summary>
    public const string Inline = "inline";

    /// <summary>A comment in the page's thread at the bottom.</summary>
    public const string Footer = "footer";
}

/// <summary>The four values <see cref="FeedbackItem.Status"/> takes (PLAN.md §5.4).</summary>
/// <remarks>
/// Ingestion only ever writes <see cref="New"/>. The other three are <c>/docs-feedback</c>'s triage
/// outcomes (§9): a factual error that was fixed, a suggestion that was declined with a reason, or an
/// open question that went to <c>_meta/GAPS.md</c>.
/// </remarks>
public static class FeedbackStatus
{
    /// <summary>Ingested, not yet triaged. The only status the CLI writes.</summary>
    public const string New = "new";

    /// <summary>The page was corrected.</summary>
    public const string Fixed = "fixed";

    /// <summary>Declined, with a reason in <see cref="FeedbackItem.Resolution"/>.</summary>
    public const string Rejected = "rejected";

    /// <summary>An open question, recorded in <c>_meta/GAPS.md</c>.</summary>
    public const string Question = "question";
}

/// <summary>What <see cref="FeedbackItem.Author"/> records when nothing about the author is known.</summary>
/// <remarks>
/// The same literal, and the same reasoning, as <see cref="Sync.LabelSyncPlanner.UnknownApprover"/>: the
/// account DocuMe authenticates as is never a substitute for a person's name.
/// </remarks>
public static class FeedbackAuthor
{
    /// <summary>No account id and no display name.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Composes <see cref="FeedbackItem.Id"/> from a channel and the channel's own id (PLAN.md §5.4's
/// <c>conf-comment-987654</c>).
/// </summary>
/// <remarks>
/// The prefix is what keeps §9's seam honest: two channels can both number their items from 1, and an
/// inbox holding both has to stay unambiguous.
/// </remarks>
public static class FeedbackItemId
{
    /// <summary>The channel prefix for a Confluence comment.</summary>
    public const string ConfluenceCommentPrefix = "conf-comment-";

    /// <summary>The id of the inbox item for Confluence comment <paramref name="commentId"/>.</summary>
    public static string ForConfluenceComment(string commentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commentId);

        return ConfluenceCommentPrefix + commentId;
    }
}

/// <summary>
/// The inbox item's file name: <c>&lt;pageSlug&gt;-&lt;commentId&gt;.json</c> (PLAN.md §5.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Both halves are sanitized, and the comment id half is a security boundary.</strong> The id
/// arrives from the Confluence API, and a file name composed from remote input is exactly where a
/// <c>../</c> lands somewhere nobody meant to write (CLAUDE.md §0.2). Every character outside
/// <c>[A-Za-z0-9-_]</c> becomes a hyphen, so a separator or a dot segment cannot survive into the path;
/// an id with nothing usable left is refused by <see cref="IsUsableId"/> before it reaches here.
/// </para>
/// <para>
/// <strong>Two pages cannot collide even though the slug is lossy.</strong> Flattening
/// <c>10-domains/loans/README.md</c> and a hypothetical <c>10-domains-loans-README.md</c> to the same
/// slug is possible; producing the same <em>file name</em> is not, because a comment belongs to exactly
/// one page, so the two would have to share a comment id. The slug is there to make the file name
/// readable, not to be the key.
/// </para>
/// </remarks>
public static class FeedbackItemFile
{
    /// <summary>The extension every inbox item carries.</summary>
    public const string Extension = ".json";

    private const char Separator = '-';

    /// <summary>Whether <paramref name="commentId"/> can be named in a file at all.</summary>
    /// <remarks>
    /// An id of nothing but punctuation would sanitize to an empty string and produce a file named after
    /// the page alone, quietly overwriting a sibling comment's item. The planner reports such a comment
    /// instead (<see cref="FeedbackSkipReason.UnusableId"/>) — one comment nobody can file is worth a
    /// line of output, not a lost inbox item.
    /// </remarks>
    public static bool IsUsableId(string? commentId) => Sanitize(commentId).Length > 0;

    /// <summary>The file name for <paramref name="commentId"/> on <paramref name="pagePath"/>.</summary>
    /// <param name="pagePath">The wiki-relative markdown path, as state.json keys it.</param>
    /// <param name="commentId">The channel's own comment id.</param>
    /// <exception cref="ArgumentException"><paramref name="commentId"/> has no usable characters.</exception>
    public static string NameFor(string pagePath, string commentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pagePath);

        var id = Sanitize(commentId);
        if (id.Length == 0)
        {
            throw new ArgumentException(
                "A comment id made only of characters a file name cannot carry has no inbox file name; "
                + "check IsUsableId first.",
                nameof(commentId));
        }

        var slug = Slug(pagePath);

        return string.Create(CultureInfo.InvariantCulture, $"{slug}{Separator}{id}{Extension}");
    }

    /// <summary>
    /// The readable half of the file name: the page path with its <c>.md</c> extension dropped and every
    /// separator flattened to a hyphen.
    /// </summary>
    public static string Slug(string pagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pagePath);

        var withoutExtension = pagePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? pagePath[..^3]
            : pagePath;

        var slug = Sanitize(withoutExtension);

        return slug.Length == 0 ? "page" : slug;
    }

    /// <summary>
    /// Keeps letters, digits, hyphens and underscores; turns everything else into a single hyphen.
    /// </summary>
    /// <remarks>
    /// Ordinal by construction: <see cref="char.IsAsciiLetterOrDigit"/> rather than
    /// <see cref="char.IsLetterOrDigit(char)"/>, so a page path with a non-ASCII letter in it produces the same
    /// name on every machine and every filesystem rather than one that depends on the current culture or
    /// on how the volume normalizes Unicode.
    /// </remarks>
    private static string Sanitize(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is Separator or '_')
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length > 0 && builder[^1] != Separator)
            {
                builder.Append(Separator);
            }
        }

        return builder.ToString().Trim(Separator);
    }
}

/// <summary>
/// The one timestamp format the feedback loop reads and writes: ISO-8601 UTC with milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Milliseconds are load-bearing, unlike in <see cref="Sync.LabelReader.TimestampFormat"/>.</strong>
/// <c>feedbackCursor</c> (§5.3) is compared against a comment's own <c>createdAt</c>, which Confluence
/// answers to the millisecond. A cursor truncated to the second would sit <em>before</em> the comment it
/// was written from, and the next run would ingest that comment again — a duplicate inbox item on every
/// sync. So the cursor keeps the precision the comparison needs, and the item's <c>createdAt</c> is
/// written the same way to keep one format in the loop.
/// </para>
/// <para>
/// Parsing is deliberately permissive about the input's shape (any offset, any precision) and strict
/// about the output's: whatever a channel sends is normalized to UTC here, so a cursor never depends on
/// the timezone of whoever wrote the comment.
/// </para>
/// </remarks>
public static class FeedbackTimestamp
{
    /// <summary>The written form, e.g. <c>2026-08-02T14:11:00.000Z</c>.</summary>
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Parses an incoming timestamp, or returns <c>false</c> for anything unreadable.</summary>
    public static bool TryParse(string? value, out DateTimeOffset parsed)
    {
        if (value is not { Length: > 0 })
        {
            parsed = default;

            return false;
        }

        // AssumeUniversal is the one flag that decides anything here: a timestamp that arrives without an
        // offset is UTC, not the runner's local time. A cursor whose meaning depended on which machine
        // parsed it would re-ingest or skip comments depending on where the sync ran.
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
            out parsed);
    }

    /// <summary>Writes <paramref name="value"/> in the one format above, in UTC.</summary>
    public static string Write(DateTimeOffset value)
        => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);
}
