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
