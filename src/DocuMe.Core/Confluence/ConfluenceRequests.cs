namespace DocuMe.Core.Confluence;

/// <summary>
/// Wire shapes for the Confluence Cloud REST v2 writes DocuMe sends, named after the request bodies
/// and schemas in Atlassian's own OpenAPI document (<c>PageCreateRequest</c>,
/// <c>PageUpdateRequest</c>, <c>PageNestedBodyWrite</c>, <c>PageBodyWrite</c>). Internal for the same
/// reason the response shapes are: the public surface is
/// <see cref="ConfluencePageDraft"/>/<see cref="ConfluencePageRevision"/>, so a wire-format change
/// stays a mapping change.
/// </summary>
/// <remarks>
/// <para>
/// Serialized with <c>JsonIgnoreCondition.WhenWritingNull</c>, which is what makes the nullable
/// members mean "leave this alone" rather than "clear it". Two of them matter: an absent
/// <c>parentId</c> on create lets Confluence pick the space homepage (documented default), whereas
/// <c>"parentId": null</c> is a value the endpoint has no documented handling for; and an absent
/// <c>version.message</c> keeps the version history line as Confluence writes it.
/// </para>
/// <para>
/// <c>spaceId</c> is deliberately absent from <see cref="PageUpdateRequest"/> even though the schema
/// accepts it: the schema's own note says it "currently <b>does not support moving the page to a
/// different space</b>", so sending it can only ever restate what Confluence already knows.
/// </para>
/// </remarks>
internal sealed record PageCreateRequest(
    string SpaceId,
    string Status,
    string Title,
    string? ParentId,
    PageNestedBodyWrite Body);

internal sealed record PageUpdateRequest(
    string Id,
    string Status,
    string Title,
    string? ParentId,
    PageNestedBodyWrite Body,
    VersionWrite Version);

internal sealed record PageNestedBodyWrite(PageBodyWrite Storage);

internal sealed record PageBodyWrite(string Representation, string Value);

internal sealed record VersionWrite(int Number, string? Message);

/// <summary>
/// One label to add, named after the v1 <c>LabelCreate</c> schema, which requires both members. Sent
/// as an array: the endpoint accepts either a bare object or an array, and an array is the shape that
/// does not change when a caller adds a second label.
/// </summary>
internal sealed record LabelCreate(string Prefix, string Name);
