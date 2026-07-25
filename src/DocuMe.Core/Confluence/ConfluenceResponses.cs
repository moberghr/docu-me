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
internal sealed record MultiEntityResult<T>(IReadOnlyList<T>? Results);

internal sealed record SpaceBulk(string? Id, string? Key, string? Name);

internal sealed record PageBulk(
    string? Id,
    string? Title,
    string? SpaceId,
    string? ParentId,
    VersionBulk? Version,
    BodyBulk? Body);

internal sealed record VersionBulk(int? Number);

internal sealed record BodyBulk(BodyType? Storage);

internal sealed record BodyType(string? Value, string? Representation);
