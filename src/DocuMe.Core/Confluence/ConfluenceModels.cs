namespace DocuMe.Core.Confluence;

/// <summary>A Confluence space, as much of it as DocuMe needs.</summary>
/// <param name="Id">
/// The numeric space id as a string. The publish pipeline needs it rather than the key: the v2 API
/// filters pages by <c>space-id</c>, not by key.
/// </param>
/// <param name="Key">The space key, e.g. <c>DOCUMESBX</c> — what a human configures.</param>
/// <param name="Name">The display name; empty when the response omitted it.</param>
public sealed record ConfluenceSpace(string Id, string Key, string Name);

/// <summary>
/// A Confluence page, as much of it as DocuMe needs.
/// </summary>
/// <param name="Id">The page id, which lands in <c>_meta/state.json</c> (PLAN.md §5.3).</param>
/// <param name="Title">
/// The page title. Unique per space — the constraint the link map validates before any publish
/// (PLAN.md §6.2 step 1).
/// </param>
/// <param name="SpaceId">The space the page lives in.</param>
/// <param name="ParentId">The parent page, or <c>null</c> for a space-root page.</param>
/// <param name="Version">
/// The current version number. An update must send this incremented by one, so a page read is
/// how the publish pipeline gets its optimistic-lock value.
/// </param>
/// <param name="Storage">
/// The body in storage format, present only when the read asked for it. <c>null</c> means "not
/// requested", never "empty page" — the distinction matters because §8 hashes body content.
/// </param>
public sealed record ConfluencePage(
    string Id,
    string Title,
    string SpaceId,
    string? ParentId,
    int Version,
    string? Storage);

/// <summary>A page that does not exist in Confluence yet (PLAN.md §6.2 step 5, the create half of the upsert).</summary>
/// <param name="SpaceId">The numeric space id from <see cref="ConfluenceClient.FindSpaceByKeyAsync"/>.</param>
/// <param name="Title">
/// The page title, unique within the space. The uniqueness constraint is Confluence's, which is why
/// the link map validates it before any publish (PLAN.md §6.2 step 1).
/// </param>
/// <param name="Storage">The rendered body in storage format (§7).</param>
/// <param name="ParentId">
/// The parent page. <c>null</c> means "wherever Confluence puts a parentless page", which its own
/// documentation defines as the space homepage — not the space root. A DocuMe publish always passes
/// one, because the wiki tree has a root page (<c>confluence.rootPageId</c>, PLAN.md §5.1).
/// </param>
public sealed record ConfluencePageDraft(
    string SpaceId,
    string Title,
    string Storage,
    string? ParentId = null);

/// <summary>
/// A new revision of a page that already exists (PLAN.md §6.2 step 5, the update half of the upsert).
/// </summary>
/// <param name="PageId">The page to overwrite, from <c>_meta/state.json</c> (PLAN.md §5.3).</param>
/// <param name="Title">
/// The title to publish. A title change is a normal update, not a move, so this may differ from what
/// Confluence currently holds.
/// </param>
/// <param name="Storage">The rendered body in storage format (§7).</param>
/// <param name="CurrentVersion">
/// The version Confluence holds right now, as read by <see cref="ConfluenceClient.FindPageByIdAsync"/>
/// or <see cref="ConfluenceClient.FindPageByTitleAsync"/>. The client sends this incremented by one;
/// callers never do the arithmetic, so there is one place for it to be wrong.
/// </param>
/// <param name="ParentId">
/// The parent to move the page under, or <c>null</c> to leave it where it is. Moving within the space
/// is what a reorganized wiki tree needs; moving between spaces is not supported by the endpoint.
/// </param>
/// <param name="VersionMessage">
/// An optional note stored with the version. Sent when present, but do not build an audit trail on it
/// unverified: an Atlassian community report has the v2 update endpoint dropping
/// <c>version.message</c>, and no sandbox run has confirmed either way yet.
/// </param>
public sealed record ConfluencePageRevision(
    string PageId,
    string Title,
    string Storage,
    int CurrentVersion,
    string? ParentId = null,
    string? VersionMessage = null);

/// <summary>
/// Where a move puts a page relative to its target, spelled as v1's three <c>position</c> values.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Append"/> is the reparent: "move the page to be a child of the target", in Atlassian's
/// words. <see cref="Before"/> and <see cref="After"/> are the sibling reorder — they file the page
/// under the <em>target's</em> parent, at a position relative to the target, which is what §6.2's
/// child-page ordering post-pass needs.
/// </para>
/// <para>
/// <strong>The caution is Atlassian's own and it is load-bearing:</strong> never use
/// <see cref="Before"/> or <see cref="After"/> when the target is a top-level page, because that
/// moves the page to the top level of the space, where it does not appear in the page tree at all.
/// The client cannot enforce this — it would need a read to know what is top-level — so the caller
/// owns it. DocuMe's tree always hangs under <c>confluence.rootPageId</c> (§5.1), so a reorder
/// reorders the children of a known parent and never touches top-level siblings.
/// </para>
/// </remarks>
public enum ConfluencePageMovePosition
{
    /// <summary>Under the target's parent, immediately before the target.</summary>
    Before,

    /// <summary>Under the target's parent, immediately after the target.</summary>
    After,

    /// <summary>A child of the target — the reparent.</summary>
    Append,
}

/// <summary>
/// One child of a page, as much of it as the child-order post-pass needs (PLAN.md §6.2).
/// </summary>
/// <param name="Id">The child page's id — what a move names, and the key the post-pass diffs on.</param>
/// <param name="Title">The child's title, carried so a report can name a page a human recognizes.</param>
/// <param name="ChildPosition">
/// Confluence's own position value, or <c>null</c>.
/// </param>
/// <remarks>
/// <strong><see cref="ChildPosition"/> is deliberately not the ordering key.</strong> It is absent on
/// pages migrated from Confluence Server and on the children of a page that was deleted, and
/// Confluence's own page tree falls back to alphabetical order for those — so a run that sorted by it
/// would compute its diff against an order nobody sees. The order the endpoint lists children in is
/// what the post-pass treats as observed truth, and it verifies the result rather than trusting it
/// (<see cref="ConfluenceClient.GetChildPagesAsync"/>). The value is read anyway because it is the
/// one number a human debugging a wrong order in a real space will ask for.
/// </remarks>
public sealed record ConfluenceChildPage(string Id, string Title, int? ChildPosition);

/// <summary>
/// One inline comment on a page — anchored to a span of the stored body, which is what makes it
/// fragile across a republish (PLAN.md §6.2 step 6).
/// </summary>
/// <param name="Id">The comment id, which is also what <c>focusedCommentId</c> in a link refers to.</param>
/// <param name="ResolutionStatus">
/// Confluence's own resolution state, verbatim and unparsed — <c>open</c>, <c>resolved</c> and
/// <c>dangling</c> are the values reported in practice — or <c>null</c> when the response carried none.
/// </param>
/// <param name="WebUiLink">
/// The comment's browser URL as Confluence composes it (site-relative), or <c>null</c>. Carried so a
/// warning can point at the comment rather than quoting it.
/// </param>
public sealed record ConfluenceInlineComment(string Id, string? ResolutionStatus, string? WebUiLink)
{
    /// <inheritdoc cref="CommentResolution.IsResolved"/>
    public bool IsResolved => CommentResolution.IsResolved(ResolutionStatus);
}

/// <summary>
/// The one rule for reading Confluence's inline-comment resolution state, shared by the publish-time
/// guard (<see cref="ConfluenceInlineComment"/>) and feedback ingestion
/// (<see cref="ConfluenceComment"/>).
/// </summary>
/// <remarks>
/// Shared rather than written twice: the guard warns about comments a republish would strand and
/// ingestion decides which comments are still live feedback, and the two disagreeing about what
/// "resolved" means would show up as a comment nobody triages.
/// </remarks>
internal static class CommentResolution
{
    /// <summary>The one status that means a human has closed the comment.</summary>
    private const string ResolvedStatus = "resolved";

    /// <summary>
    /// Whether Confluence considers the comment closed.
    /// </summary>
    /// <remarks>
    /// Deliberately "is it resolved" rather than "is it open", so that a missing status and a value this
    /// client has never seen both read as <em>not</em> resolved. Both directions of a wrong guess are
    /// available here and they are not symmetric: over-reporting costs a warning a human dismisses or an
    /// inbox item they close, under-reporting silently drops a reviewer's question. A <c>dangling</c>
    /// comment counts as unresolved for the same reason — its anchor is already lost, which is exactly
    /// the case worth talking about — and so does <c>reopened</c>.
    /// </remarks>
    public static bool IsResolved(string? resolutionStatus) =>
        string.Equals(resolutionStatus, ResolvedStatus, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Which comment collection a comment came out of (PLAN.md §5.4's <c>kind</c>).
/// </summary>
public enum ConfluenceCommentKind
{
    /// <summary>The page's comment thread at the bottom — <c>footer-comments</c>.</summary>
    Footer,

    /// <summary>A comment anchored to a span of the page body — <c>inline-comments</c>.</summary>
    Inline,
}

/// <summary>
/// One comment on a page, with its text, for feedback ingestion (PLAN.md §6.3's Comments bullet).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The body is untrusted input</strong> (CLAUDE.md §0.2, rule §1.3). It is carried verbatim, in
/// Confluence storage format exactly as the API answered it, and nothing in the CLI parses it, matches
/// patterns in it, or interpolates it into a prompt: the tool's whole job is to write it down (§5.4) so
/// that <c>/docs-feedback</c> can treat it as a claim to verify against the code.
/// </para>
/// <para>
/// <strong>The author is an account id, not a name.</strong> Neither comment endpoint answers a display
/// name — <c>version.authorId</c> is all there is — so a readable author costs a separate user lookup
/// (<see cref="ConfluenceClient.FindUserAsync"/>). The id is what identifies DocuMe's own replies, which
/// §6.3 says ingestion must skip.
/// </para>
/// </remarks>
/// <param name="Id">The comment id, which is what <c>focusedCommentId</c> in a link refers to.</param>
/// <param name="Kind">Which collection it came from.</param>
/// <param name="AuthorAccountId">
/// The Atlassian account id that wrote it (<c>version.authorId</c>), or <c>null</c> when the response
/// carried none — in which case the comment can never be identified as DocuMe's own.
/// </param>
/// <param name="CreatedAt">
/// When it was written (<c>version.createdAt</c>), verbatim, or <c>null</c>. This is what the
/// <c>feedbackCursor</c> (§5.3) is compared against.
/// </param>
/// <param name="Body">
/// The comment text in storage format, or <c>null</c> when the response carried no body at all.
/// </param>
/// <param name="QuotedText">
/// For an inline comment, the page text it is anchored to (<c>properties.inlineOriginalSelection</c>) —
/// §5.4's <c>quotedText</c>. Always <c>null</c> for a footer comment, which is anchored to nothing.
/// </param>
/// <param name="ResolutionStatus">Confluence's own resolution state, verbatim and unparsed, or <c>null</c>.</param>
/// <param name="WebUiLink">The comment's browser URL as Confluence composes it, or <c>null</c>.</param>
public sealed record ConfluenceComment(
    string Id,
    ConfluenceCommentKind Kind,
    string? AuthorAccountId,
    string? CreatedAt,
    string? Body,
    string? QuotedText,
    string? ResolutionStatus,
    string? WebUiLink)
{
    /// <inheritdoc cref="CommentResolution.IsResolved"/>
    public bool IsResolved => CommentResolution.IsResolved(ResolutionStatus);
}

/// <summary>
/// A Confluence account, narrowed to the two things DocuMe asks about one: which account it is, and what
/// to call it in an inbox item (PLAN.md §5.4's <c>author</c>).
/// </summary>
/// <remarks>
/// The email address the endpoint also answers is deliberately not carried. An inbox item is committed
/// to the consumer repo, and a display name is what a reviewer needs to recognize their own comment.
/// </remarks>
/// <param name="AccountId">The Atlassian account id.</param>
/// <param name="DisplayName">The name Confluence shows for the account, or <c>null</c> if it answered none.</param>
public sealed record ConfluenceUser(string AccountId, string? DisplayName);

/// <summary>
/// A page to move within its space, without writing its body (PLAN.md §6.2: the reorganized-tree case
/// and the child-page ordering post-pass).
/// </summary>
/// <param name="PageId">The page to move, from <c>_meta/state.json</c> (PLAN.md §5.3).</param>
/// <param name="Position">Where to put it relative to <paramref name="TargetId"/>.</param>
/// <param name="TargetId">
/// The page the move is relative to: the new parent for <see cref="ConfluencePageMovePosition.Append"/>,
/// the new sibling for the other two.
/// </param>
public sealed record ConfluencePageMove(
    string PageId,
    ConfluencePageMovePosition Position,
    string TargetId);

/// <summary>
/// A file to publish alongside a page: a rendered mermaid diagram or a repo image (PLAN.md §6.2
/// step 5).
/// </summary>
/// <param name="PageId">The page to attach it to.</param>
/// <param name="FileName">
/// The attachment name, which is what <c>&lt;ri:attachment ri:filename="…"/&gt;</c> in the page body
/// refers to. DocuMe derives it from the file's path, so it carries underscores and can be long
/// (the flattening rule in <c>AttachmentName</c>).
/// </param>
/// <param name="Content">
/// The bytes. Deliberately not a <see cref="Stream"/>: the transport replays a rate-limited request,
/// and a stream would be drained by the first attempt and send nothing on the second. The publish
/// pipeline has the bytes in hand anyway, having just hashed them to decide the upload was needed.
/// </param>
/// <param name="ContentType">
/// The media type of the part, e.g. <c>image/svg+xml</c>. Load-bearing rather than cosmetic:
/// Confluence records it as the attachment's media type, and it is what decides whether an
/// <c>&lt;ac:image&gt;</c> renders inline or degrades to a download link.
/// </param>
/// <param name="Comment">
/// An optional note stored with the attachment version. Omitted from the request entirely when
/// absent, rather than sent empty.
/// </param>
public sealed record ConfluenceAttachmentUpload(
    string PageId,
    string FileName,
    ReadOnlyMemory<byte> Content,
    string ContentType,
    string? Comment = null);

/// <summary>An attachment as Confluence stored it.</summary>
/// <param name="Id">The attachment id, e.g. <c>att77830</c>.</param>
/// <param name="Title">The stored file name, which is what the page body references.</param>
/// <param name="Version">
/// The attachment's version number, or <c>null</c> when the response omitted it. Nullable where a
/// page's version is not, because the upsert never has to send one back: Confluence versions an
/// attachment server-side, so a missing number costs an observation, not a publish.
/// </param>
public sealed record ConfluenceAttachment(string Id, string Title, int? Version);

/// <summary>A label on a page — the whole of the human approval gesture (PLAN.md §8).</summary>
/// <param name="Name">The label as a reviewer types it, e.g. <c>approved</c>.</param>
/// <param name="Prefix">The namespace Confluence files it under; <c>global</c> for a normal label.</param>
public sealed record ConfluenceLabel(string Name, string Prefix);

/// <summary>
/// One page a label search answered with (PLAN.md §6.3): what <c>sync --labels</c> reconciles into
/// <c>_meta/state.json</c>.
/// </summary>
/// <param name="Id">The page id, which is what the reconcile keys on — never the title.</param>
/// <param name="Title">The title as Confluence holds it, for a human-readable report line.</param>
/// <param name="Version">
/// The page version current when the search ran, or <c>null</c> when the response did not carry one.
/// Nullable because it decides a fallback rather than a failure: §8 records approval at the version
/// current at observation time, so a caller that gets no version here reads the page by id instead of
/// guessing (<see cref="ConfluenceClient.FindPageByIdAsync"/>). It is emphatically not
/// <c>state.publishedVersion</c> — the two differ exactly when a human edited the page in a browser,
/// which is the case §8's wording exists for.
/// </param>
public sealed record ConfluenceLabelledPage(string Id, string Title, int? Version);
