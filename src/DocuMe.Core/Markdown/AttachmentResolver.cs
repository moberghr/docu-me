namespace DocuMe.Core.Markdown;

/// <summary>
/// Resolves a relative image path (e.g. <c>images/sub/architecture.png</c>, exactly as
/// written in the source) to the <em>final Confluence attachment filename</em> it will be
/// uploaded under, or <c>null</c> when it resolves to no known file (a broken image
/// reference).
/// </summary>
/// <remarks>
/// The returned value is a bare filename, not a path: Confluence attachments are
/// <em>flat</em> per page, so <c>images/sub/architecture.png</c> must arrive here and leave
/// as something like <c>architecture.png</c>. Flattening, collision resolution, content
/// hashing, dedup and the upload itself all belong to the publish pipeline (M2, PLAN.md
/// §6.2); the converter only consumes the lookup, so it stays a pure text transform that
/// never touches the filesystem.
/// <para>
/// This mirrors <see cref="PageLinkResolver"/> exactly, and for the same reason — the
/// whole-tree knowledge lives one layer up.
/// </para>
/// </remarks>
public delegate string? AttachmentResolver(string relativeImagePath);
