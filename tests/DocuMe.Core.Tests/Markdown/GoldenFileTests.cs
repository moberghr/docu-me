using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// The converter contract (PLAN.md §7): every <c>tests/golden/&lt;case&gt;.md</c>
/// must convert byte-for-byte to <c>&lt;case&gt;.storage.xml</c>. Adding a case is
/// dropping the two files — this harness discovers them automatically. Seeded
/// with one construct (headings + paragraph) to prove the harness itself; the
/// construct table grows case-by-case across M1.
/// </summary>
public sealed class GoldenFileTests
{
    private static readonly string GoldenDir = Path.Combine(AppContext.BaseDirectory, "golden");

    public static IEnumerable<object[]> Cases()
    {
        foreach (var md in Directory.EnumerateFiles(GoldenDir, "*.md"))
        {
            yield return [Path.GetFileNameWithoutExtension(md)];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Converts_markdown_to_expected_storage_format(string caseName)
    {
        var markdown = File.ReadAllText(Path.Combine(GoldenDir, caseName + ".md"));
        var expected = File.ReadAllText(Path.Combine(GoldenDir, caseName + ".storage.xml"));

        var parsed = FrontmatterParser.Parse(markdown);
        var actual = ConfluenceStorageConverter.Convert(
            parsed.Body,
            ResolveGoldenLink,
            ResolveGoldenAttachment,
            ResolveGoldenDiagram);

        Normalize(actual).ShouldBe(Normalize(expected));
    }

    /// <summary>
    /// Fixed path→title map for the golden cases that exercise relative <c>.md</c>
    /// links (<c>links-page</c>). The real whole-tree resolver lands with the M2
    /// publish pipeline; cases without relative links never invoke this. The
    /// second title carries an <c>&amp;</c> to pin attribute escaping.
    /// </summary>
    private static string? ResolveGoldenLink(string relativeMarkdownPath) => relativeMarkdownPath switch
    {
        "domains/loans/README.md" => "Loans Domain",
        "../architecture/overview.md" => "Architecture & Design",
        _ => null,
    };

    /// <summary>
    /// Fixed path→attachment-filename map for the <c>images</c> golden case. The real
    /// resolver — which flattens nested paths, resolves collisions and hashes content —
    /// lands with the M2 publish pipeline; this one stands in for it, deliberately
    /// including a nested path that flattens to a different filename and one carrying an
    /// <c>&amp;</c> to pin attribute escaping on <c>ri:filename</c>.
    /// </summary>
    private static string? ResolveGoldenAttachment(string relativeImagePath) => relativeImagePath switch
    {
        "images/architecture.png" => "architecture.png",
        "images/spacer.png" => "spacer.png",
        "diagrams/loan-flow.png" => "loan-flow.png",
        "images/rich.png" => "rich.png",
        "images/sub/deep.png" => "images_sub_deep.png",
        "images/a-and-b.png" => "a & b.png",
        "images/badge.png" => "badge.png",
        "images/cell.png" => "cell.png",
        "images/item.png" => "item.png",
        "images/ref.png" => "ref.png",
        _ => null,
    };

    /// <summary>
    /// Fixed source→attachment-filename map for the <c>mermaid</c> golden case. Keying on the
    /// diagram source verbatim is the point: it pins exactly what the converter extracts from
    /// a fence, including the tilde-fence and list-item forms where the indentation could
    /// plausibly differ. The real resolver renders via Node (PLAN.md §4) and names the file
    /// from a hash of this same source; readable names here keep the golden reviewable by
    /// hand (§4.3).
    /// </summary>
    private static string? ResolveGoldenDiagram(string mermaidSource) => mermaidSource switch
    {
        "graph TD;\nA[Loan request] --> B{Approved?};\nB -- yes --> C[Disburse];" =>
            "mermaid-graph-td.svg",
        "sequenceDiagram\n  Alice->>Bob: Hello & welcome" => "mermaid-sequence.svg",
        "pie title Pets\n  \"Dogs\" : 386" => "mermaid-pie.svg",
        "flowchart LR\n  a --> b" => "mermaid-flowchart.svg",
        _ => null,
    };

    [Fact]
    public void Golden_directory_has_at_least_one_case()
    {
        // Guards against the harness silently testing nothing if the copy step
        // or the golden directory ever breaks.
        Directory.Exists(GoldenDir).ShouldBeTrue($"golden directory missing: {GoldenDir}");
        Directory.EnumerateFiles(GoldenDir, "*.md").ShouldNotBeEmpty();
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);
}
