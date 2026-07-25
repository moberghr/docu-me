using System.Text.Json.Serialization;

namespace DocuMe.Core.Confluence;

/// <summary>
/// Wire shapes for the Confluence Cloud REST v2 responses DocuMe reads, named after the schemas in
/// Atlassian's own OpenAPI document (<c>MultiEntityResult&lt;Page&gt;</c>, <c>PageBulk</c>,
/// <c>SpaceBulk</c>, …). Internal on purpose: the public surface is
/// <see cref="ConfluencePage"/>/<see cref="ConfluenceSpace"/>, so a wire-format change stays a
/// mapping change.
/// </summary>
/// <remarks>
/// <para>
/// Every member is nullable even where the schema marks it required, because the mapper — not the
/// deserializer — is what turns a missing field into a
/// <see cref="ConfluenceProtocolException"/> naming the field. A non-nullable member would instead
/// hand the caller a default and lose the field name.
/// </para>
/// <para>
/// Ids are strings, matching the v2 schemas (verified against Atlassian's OpenAPI document, where
/// <c>PageBulk.id</c>, <c>PageBulk.spaceId</c> and <c>SpaceBulk.id</c> are all typed
/// <c>string</c>). The client's serializer uses web defaults, which also read a JSON number into a
/// numeric member, so a version arriving as <c>"3"</c> still parses.
/// </para>
/// </remarks>
/// <remarks>
/// Shared with the two v1 endpoints DocuMe uses, whose <c>ContentArray</c> and <c>LabelArray</c>
/// schemas wrap their results in the same <c>results</c> member. The extra members v1 adds
/// (<c>start</c>, <c>limit</c>, <c>size</c>) are simply not read.
/// </remarks>
/// <typeparam name="T">The wire shape of one entity, e.g. <see cref="PageBulk"/>.</typeparam>
/// <param name="Results">The entities themselves.</param>
/// <param name="Links">
/// v2's <c>_links</c> block, whose <c>next</c> member is how a read that has more to say says so
/// (<see cref="ConfluenceClient.GetChildPagesAsync"/>). Optional because every other read DocuMe
/// performs asks for one entity and can never be paginated.
/// </param>
internal sealed record MultiEntityResult<T>(
    IReadOnlyList<T>? Results,
    [property: JsonPropertyName("_links")] PaginationLinks? Links = null);

/// <summary>
/// The subset of a v2 <c>_links</c> block that decides whether a read is finished: the relative URL
/// of the next page of results, absent on the last one.
/// </summary>
internal sealed record PaginationLinks(string? Next);

internal sealed record SpaceBulk(string? Id, string? Key, string? Name);

internal sealed record PageBulk(
    string? Id,
    string? Title,
    string? SpaceId,
    string? ParentId,
    VersionBulk? Version,
    BodyBulk? Body);

/// <summary>
/// The v2 <c>Version</c> schema, narrowed to the three members DocuMe reads. <c>createdAt</c> and
/// <c>authorId</c> belong to a comment's version rather than a page's: they are when the comment was
/// written and the account id that wrote it, which is all Confluence offers about a comment's author
/// (see <see cref="ConfluenceComment"/>).
/// </summary>
/// <remarks>
/// Optional rather than positional so the page and attachment reads, which ask for neither, keep
/// mapping unchanged. Verified against Atlassian's own <c>openapi-v2.v3.json</c>, where <c>Version</c>
/// carries <c>createdAt</c>, <c>message</c>, <c>number</c>, <c>minorEdit</c> and <c>authorId</c>.
/// </remarks>
internal sealed record VersionBulk(int? Number, string? CreatedAt = null, string? AuthorId = null);

/// <summary>
/// The v2 <c>ChildPage</c> schema, narrowed to what the child-order post-pass reads. It is a
/// deliberately thin shape — the endpoint answers no version and no parent id, which is why the
/// post-pass reorders by id and never tries to write from what it read.
/// </summary>
internal sealed record ChildPageBulk(string? Id, string? Title, int? ChildPosition);

/// <summary>
/// The v2 inline-comment schema, narrowed to what the open-comment guard reads (PLAN.md §6.2 step 6).
/// </summary>
/// <remarks>
/// <c>resolutionStatus</c> is the field the whole guard turns on. Atlassian's current OpenAPI document
/// does list it (<c>InlineCommentResolutionStatus</c>: <c>open</c>, <c>reopened</c>, <c>resolved</c>,
/// <c>dangling</c> — checked against <c>openapi-v2.v3.json</c> on 2026-07-25, correcting an earlier
/// remark here that said it did not), but it stays nullable and is still never compared against a closed
/// set: a value this client has never seen must read as unresolved rather than as a parse failure. See
/// <see cref="ConfluenceInlineComment.IsResolved"/>.
/// </remarks>
internal sealed record InlineCommentBulk(
    string? Id,
    string? ResolutionStatus,
    [property: JsonPropertyName("_links")] EntityLinks? Links);

/// <summary>
/// The v2 <c>PageCommentModel</c> and <c>PageInlineCommentModel</c> schemas as one shape: what feedback
/// ingestion reads (PLAN.md §6.3's Comments bullet, §5.4).
/// </summary>
/// <remarks>
/// <para>
/// One record for both endpoints because the footer schema is the inline schema minus two members —
/// <c>resolutionStatus</c> and <c>properties</c>, which simply arrive null on a footer read. Kept
/// separate from <see cref="InlineCommentBulk"/>, which is the open-comment guard's deliberately
/// body-free shape (see <see cref="ConfluenceClient.GetInlineCommentsAsync"/>): the guard asks for no
/// body because it quotes none, and widening it to share this record would start requesting comment
/// text on every publish.
/// </para>
/// <para>
/// <c>body</c> is only populated when the request asks for a representation, which is why both
/// ingestion reads send <c>body-format=storage</c>.
/// </para>
/// </remarks>
internal sealed record CommentBulk(
    string? Id,
    string? Title,
    string? PageId,
    VersionBulk? Version,
    BodyBulk? Body,
    string? ResolutionStatus,
    InlineCommentPropertiesBulk? Properties,
    [property: JsonPropertyName("_links")] EntityLinks? Links);

/// <summary>
/// The v2 <c>InlineCommentProperties</c> schema. <c>inlineOriginalSelection</c> is the page text the
/// comment is anchored to — <c>quotedText</c> in §5.4's inbox item.
/// </summary>
internal sealed record InlineCommentPropertiesBulk(string? InlineMarkerRef, string? InlineOriginalSelection);

/// <summary>
/// The v1 user schema, narrowed to the two members DocuMe reads. Everything else it answers —
/// <c>email</c> above all — is deliberately not mapped: an inbox item is a committed file (§5.4), and a
/// reviewer's address does not belong in a repo.
/// </summary>
internal sealed record UserBulk(string? AccountId, string? DisplayName);

/// <summary>
/// The subset of an entity's own <c>_links</c> block DocuMe reads: the browser URL, which is the one
/// thing that turns a comment id in a terminal into something a human can open.
/// </summary>
/// <remarks>
/// Distinct from <see cref="PaginationLinks"/> on purpose. They are the same JSON member on different
/// objects — one belongs to a result set and answers "is the read finished", the other belongs to an
/// entity and answers "where is it".
/// </remarks>
internal sealed record EntityLinks(string? Webui);

/// <summary>
/// The v1 <c>Content</c> schema, narrowed to what an attachment upload answers with. v1 marks only
/// <c>type</c> and <c>status</c> required, so everything DocuMe reads is checked by the mapper.
/// </summary>
internal sealed record ContentBulk(string? Id, string? Title, VersionBulk? Version);

/// <summary>
/// The whole documented body of a successful v1 move: the id of the page that moved. Read rather than
/// ignored for the same reason every other write maps its response — a 200 answering something other
/// than the documented shape is worth a loud failure, not a shrug.
/// </summary>
internal sealed record ContentMoveResponse(string? PageId);

/// <summary>The v1 <c>Label</c> schema, whose four members are all required.</summary>
internal sealed record LabelBulk(string? Prefix, string? Name, string? Id, string? Label);

internal sealed record BodyBulk(BodyType? Storage);

internal sealed record BodyType(string? Value, string? Representation);
