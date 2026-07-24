namespace DocuMe.Core.Markdown;

/// <summary>
/// The three lookups <see cref="ConfluenceStorageConverter.Convert"/> needs for one page,
/// bound to that page's location in the tree. Produced by <see cref="WikiTree.ResolversFor"/>.
/// </summary>
/// <remarks>
/// The converter is a pure text transform that never touches the filesystem (§7), so all
/// whole-tree knowledge — which paths exist, what each page is titled, what filename an
/// attachment lands under — arrives through these three delegates. Bundling them keeps the
/// publish pipeline's per-page call site to one lookup instead of three.
/// </remarks>
/// <param name="Link">Relative <c>.md</c> link path → target page title (§6.2 step 2).</param>
/// <param name="Attachment">Relative image path → flat Confluence attachment filename.</param>
/// <param name="Diagram">
/// <c>```mermaid</c> fence body → the attachment filename its rendered SVG lands under.
/// Page-independent (it hashes the diagram source), so every page gets the same delegate.
/// </param>
public sealed record PageResolvers(
    PageLinkResolver Link,
    AttachmentResolver Attachment,
    MermaidDiagramResolver Diagram);
