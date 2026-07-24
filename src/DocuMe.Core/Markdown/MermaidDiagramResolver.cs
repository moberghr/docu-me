namespace DocuMe.Core.Markdown;

/// <summary>
/// Resolves the body of a <c>```mermaid</c> fence to the <em>Confluence attachment
/// filename the rendered diagram will be uploaded under</em>, or <c>null</c> when that
/// diagram cannot be rendered.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does <em>not</em> mirror <see cref="AttachmentResolver"/>, despite the
/// similar shape. An image attachment already exists as a file on disk, so a path→filename
/// lookup is enough. A mermaid attachment <em>does not exist yet</em>: it must be rendered
/// from the fence's own source (shelling out to Node and <c>render-mermaid.mjs</c>, PLAN.md
/// §4/§6.2 step 3), so there is no path to look up and the diagram source itself is the key.
/// </para>
/// <para>
/// Rendering, caching, dedup and the upload all belong to the publish pipeline (M2, §6.2);
/// the converter only consumes the lookup, so it stays a pure text transform that never
/// touches the filesystem and never starts a process. That property is what makes it
/// deterministic for the §8 content hash and testable with hand-authored goldens.
/// </para>
/// <para>
/// <strong>The returned filename MUST be a pure function of the diagram source</strong>
/// (e.g. <c>mermaid-&lt;hash-of-source&gt;.svg</c>), not sequential or random. It lands in
/// the published body and therefore in the content hash, so a filename that changes per
/// render would churn the hash on every publish and invalidate approvals that nothing
/// actually changed (§8, §9.2).
/// </para>
/// </remarks>
/// <param name="mermaidSource">
/// The fence body verbatim, exactly as the author wrote it (no trailing newline).
/// </param>
public delegate string? MermaidDiagramResolver(string mermaidSource);
