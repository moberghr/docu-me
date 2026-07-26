using DocuMe.Core.Markdown;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// The golden corpus (<c>tests/golden</c>): where it is, and the fixed lookups it converts
/// against. The real whole-tree resolvers live on <see cref="WikiTree"/>; these stand in for them
/// so the reviewed goldens pin converter behavior alone (PLAN.md §4.3, §7).
/// </summary>
/// <remarks>
/// <para>
/// Shared by <see cref="GoldenFileTests"/> and the acceptance runner's own suite, which uses the
/// same corpus as its fixture. One copy on purpose: two copies that drift would make the two
/// suites disagree about the same 27 files.
/// </para>
/// <para>
/// The three maps are dictionaries rather than <c>switch</c> expressions so their keys can be
/// enumerated: a reference the corpus makes but no map answers fails loud already (the converter
/// throws), while a map entry nothing references is caught by nothing and accumulates as dead
/// fiction. <see cref="GoldenCoverageTests.Every_resolver_entry_is_referenced_by_a_golden_case"/>
/// closes that second direction, and needs the keys to do it.
/// </para>
/// </remarks>
internal static class GoldenCorpus
{
    /// <summary>
    /// Path→title map for the golden cases that exercise relative <c>.md</c> links
    /// (<c>links-page</c>). Cases without relative links never invoke this. The second title
    /// carries an <c>&amp;</c> to pin attribute escaping.
    /// </summary>
    private static readonly Dictionary<string, string> Links = new(StringComparer.Ordinal)
    {
        ["domains/loans/README.md"] = "Loans Domain",
        ["../architecture/overview.md"] = "Architecture & Design",
    };

    /// <summary>
    /// Path→attachment-filename map for the <c>images</c> golden case. Deliberately includes a
    /// nested path that flattens to a different filename and one carrying an <c>&amp;</c> to pin
    /// attribute escaping on <c>ri:filename</c>.
    /// </summary>
    private static readonly Dictionary<string, string> Attachments = new(StringComparer.Ordinal)
    {
        ["images/architecture.png"] = "architecture.png",
        ["images/spacer.png"] = "spacer.png",
        ["diagrams/loan-flow.png"] = "loan-flow.png",
        ["images/rich.png"] = "rich.png",
        ["images/sub/deep.png"] = "images_sub_deep.png",
        ["images/a-and-b.png"] = "a & b.png",
        ["images/badge.png"] = "badge.png",
        ["images/cell.png"] = "cell.png",
        ["images/item.png"] = "item.png",
        ["images/ref.png"] = "ref.png",
    };

    /// <summary>
    /// Source→attachment-filename map for the <c>mermaid</c> golden case. Keying on the diagram
    /// source verbatim is the point: it pins exactly what the converter extracts from a fence,
    /// including the tilde-fence and list-item forms where the indentation could plausibly
    /// differ. The real resolver renders via Node (PLAN.md §4) and names the file from a hash of
    /// this same source; readable names here keep the golden reviewable by hand (§4.3).
    /// </summary>
    private static readonly Dictionary<string, string> Diagrams = new(StringComparer.Ordinal)
    {
        ["graph TD;\nA[Loan request] --> B{Approved?};\nB -- yes --> C[Disburse];"] =
            "mermaid-graph-td.svg",
        ["sequenceDiagram\n  Alice->>Bob: Hello & welcome"] = "mermaid-sequence.svg",
        ["pie title Pets\n  \"Dogs\" : 386"] = "mermaid-pie.svg",
        ["flowchart LR\n  a --> b"] = "mermaid-flowchart.svg",
    };

    /// <summary>
    /// The corpus directory, copied beside the test assembly by the test project so it is found
    /// in any run.
    /// </summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "golden");

    /// <summary>The three lookups bundled, for callers that take a <see cref="PageResolvers"/>.</summary>
    public static PageResolvers Resolvers { get; } = new(Link, Attachment, Diagram);

    /// <summary>Every relative <c>.md</c> path <see cref="Link"/> claims to answer.</summary>
    public static IReadOnlyCollection<string> LinkKeys => Links.Keys;

    /// <summary>Every relative image path <see cref="Attachment"/> claims to answer.</summary>
    public static IReadOnlyCollection<string> AttachmentKeys => Attachments.Keys;

    /// <summary>Every mermaid source <see cref="Diagram"/> claims to answer.</summary>
    public static IReadOnlyCollection<string> DiagramKeys => Diagrams.Keys;

    /// <summary>Resolves a relative <c>.md</c> link to the target page's title, or <c>null</c>.</summary>
    public static string? Link(string relativeMarkdownPath) =>
        Links.GetValueOrDefault(relativeMarkdownPath);

    /// <summary>Resolves a relative image path to its flat attachment filename, or <c>null</c>.</summary>
    public static string? Attachment(string relativeImagePath) =>
        Attachments.GetValueOrDefault(relativeImagePath);

    /// <summary>Resolves a mermaid fence body to its rendered attachment filename, or <c>null</c>.</summary>
    public static string? Diagram(string mermaidSource) =>
        Diagrams.GetValueOrDefault(mermaidSource);
}
