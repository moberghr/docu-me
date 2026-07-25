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
