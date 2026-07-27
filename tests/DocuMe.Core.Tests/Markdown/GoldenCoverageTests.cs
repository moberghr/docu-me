using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// The second half of the converter's acceptance bar (rule §4.4): the golden corpus must cover
/// <em>every</em> construct in PLAN.md §7's table, not merely convert whatever it happens to contain.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GoldenFileTests"/> asserts that each golden converts to its reviewed output, and
/// <see cref="Acceptance.ConversionAcceptanceTests"/> asserts the corpus converts with no failure and
/// exactly the recorded degradations. Neither can notice a construct with <em>no case at all</em>: an
/// absent golden is an absent test, and the suite stays green while §7 says something the corpus never
/// checks. That is not hypothetical — the two rows this class names last, the ⚠️ markers and the
/// generated footer line, had no case until this test was written, and the corpus had been green for
/// twenty-odd iterations.
/// </para>
/// <para>
/// The same reasoning covers one fact about the corpus's <em>shape</em> rather than its constructs
/// (<see cref="No_golden_case_hides_below_the_corpus_root"/>): the runner enumerates recursively and
/// every reviewing assertion enumerates the root, so a nested case would be converted by one and
/// pinned by neither.
/// </para>
/// <para>
/// This matters more since the bar was revised (2026-07-25): real-corpus validation moved to M7, so the
/// goldens are the whole of M1's acceptance. Adding a row to §7's table means adding its case here in the
/// same slice, which is the rule §4.4 sentence this class enforces.
/// </para>
/// </remarks>
public sealed class GoldenCoverageTests
{
    /// <summary>
    /// PLAN.md §7's construct table, row by row, mapped to the golden cases that pin it. §7's tenth row
    /// bundles seven constructs into one cell (inline code, bold, italic, strikethrough, blockquotes, hr,
    /// nested lists); they are split here, because "the row is covered" would otherwise be true with six
    /// of the seven missing.
    /// </summary>
    private static readonly (string Construct, string[] Cases)[] Table =
    [
        ("headings h1-h4", ["headings-and-paragraph"]),
        ("tables (GFM)", ["tables", "tables-ragged"]),
        ("fenced code blocks", ["code-blocks"]),
        ("code fence attributes", ["code-fence-attributes"]),
        ("GitHub alerts", ["alerts", "alerts-nested", "alert-with-blocks"]),
        ("mermaid fences", ["mermaid"]),
        ("relative .md links", ["links-page", "links-reference"]),
        ("external links", ["links-external"]),
        ("[TOC] alone on a line", ["toc"]),
        ("inline code, bold, italic", ["inline-formatting"]),
        ("strikethrough", ["strikethrough"]),
        ("blockquotes", ["blockquote", "quote-in-list"]),
        ("hr", ["thematic-break"]),
        ("nested lists", ["lists", "list-loose"]),
        ("task lists", ["task-lists", "task-lists-nested", "task-lists-mixed"]),
        ("images (local files)", ["images"]),
        ("markers passed through as text", ["markers"]),
        ("generated footer line", ["footer"]),
        ("HTML comments", ["html-comments"]),
    ];

    /// <summary>
    /// Cases that pin behavior §7's table does not give a row of its own. <c>entities</c> is the escaper:
    /// §7 assumes character references resolve and that <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> survive
    /// a round trip, in every construct rather than in one.
    /// </summary>
    private static readonly string[] Extras = ["entities"];

    [Fact]
    public void Every_construct_in_PLAN_7_has_a_golden_case()
    {
        var missing = Table
            .SelectMany(row => row.Cases.Select(name => (row.Construct, Name: name)))
            .Where(entry => !File.Exists(Path.Combine(GoldenCorpus.Directory, entry.Name + ".md")))
            .Select(entry => $"{entry.Construct} -> {entry.Name}.md")
            .ToList();

        missing.ShouldBeEmpty("A §7 construct with no golden case is an untested construct (rule §4.4).");
    }

    [Fact]
    public void Every_named_case_has_a_reviewed_expectation_beside_it()
    {
        // A .md with no .storage.xml beside it is not a half-written case: GoldenFileTests would throw
        // on it, so the failure is loud either way. It is named here because the message is the useful
        // part — which construct lost its expectation.
        var missing = Table
            .SelectMany(row => row.Cases)
            .Concat(Extras)
            .Where(name => !File.Exists(Path.Combine(GoldenCorpus.Directory, name + ".storage.xml")))
            .ToList();

        missing.ShouldBeEmpty("Every golden case needs its hand-reviewed .storage.xml (rule §4.3).");
    }

    [Fact]
    public void Every_golden_case_is_claimed_by_a_construct_or_declared_an_extra()
    {
        var claimed = Table
            .SelectMany(row => row.Cases)
            .Concat(Extras)
            .ToHashSet(StringComparer.Ordinal);

        var unclaimed = Directory
            .EnumerateFiles(GoldenCorpus.Directory, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && !claimed.Contains(name))
            .ToList();

        // The reverse direction, and the reason the forward one keeps working. Without it the table above
        // decays quietly: cases get added, nobody maps them, and "every construct has a case" ends up
        // asserted against a list that stopped describing the corpus.
        unclaimed.ShouldBeEmpty(
            "Map each new golden to the §7 row it pins, or add it to Extras with a reason.");
    }

    [Fact]
    public void No_golden_case_hides_below_the_corpus_root()
    {
        // The third blind spot, and the only one that is a live vector rather than an omission. The
        // shipped runner walks the corpus RECURSIVELY (ConversionAcceptance.RunDirectory,
        // SearchOption.AllDirectories) while every assertion that reviews it — the three above, and
        // GoldenFileTests — enumerates the root alone. A .md below the root is therefore converted
        // and counted as a corpus page that no hand-reviewed .storage.xml pins and no §7 row claims,
        // so rule §4.3's "asserted forever" quietly stops covering it while the suite stays green.
        //
        // Not hypothetical: the fixture is copied by a `..\golden\**\*` glob, so an UNTRACKED
        // subdirectory reaches it. tests/golden/.claude/ is in bin/ today, one .md away from firing.
        // The glob is deliberately left broad — narrowing it to the root would trade this loud
        // failure for a silent one, where a genuinely nested case never reaches the fixture at all.
        var nested = Directory
            .EnumerateFiles(GoldenCorpus.Directory, "*.md", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(GoldenCorpus.Directory, file).Replace('\\', '/'))
            .Where(relative => relative.Contains('/', StringComparison.Ordinal))
            .ToList();

        nested.ShouldBeEmpty(
            "The golden corpus is flat by contract: a nested .md is converted by the acceptance "
            + "runner but reviewed by nothing. Move it to the corpus root with its .storage.xml, "
            + "or keep it out of tests/golden entirely.");
    }

    [Fact]
    public void Every_resolver_entry_is_referenced_by_a_golden_case()
    {
        var requested = new
        {
            Links = new HashSet<string>(StringComparer.Ordinal),
            Attachments = new HashSet<string>(StringComparer.Ordinal),
            Diagrams = new HashSet<string>(StringComparer.Ordinal),
        };

        foreach (var md in Directory.EnumerateFiles(GoldenCorpus.Directory, "*.md"))
        {
            var parsed = FrontmatterParser.Parse(File.ReadAllText(md));

            // The real resolvers, wrapped: recording the argument rather than a hand-written regex
            // over the markdown means this sees exactly what the converter asks for — including the
            // mermaid sources, where the fence body the parser hands over is the whole subtlety.
            ConfluenceStorageConverter.Convert(
                parsed.Body,
                path => Record(requested.Links, path, GoldenCorpus.Link),
                path => Record(requested.Attachments, path, GoldenCorpus.Attachment),
                source => Record(requested.Diagrams, source, GoldenCorpus.Diagram));
        }

        // Only this direction needs asserting. The other one is already gated: a case referencing a
        // path no map answers makes the converter throw, so GoldenFileTests goes red on it. An entry
        // whose reference was renamed or deleted is caught by nothing, and reads to the next author
        // as corpus surface that is actually fiction.
        var dead = Dead("Link", GoldenCorpus.LinkKeys, requested.Links)
            .Concat(Dead("Attachment", GoldenCorpus.AttachmentKeys, requested.Attachments))
            .Concat(Dead("Diagram", GoldenCorpus.DiagramKeys, requested.Diagrams))
            .ToList();

        dead.ShouldBeEmpty(
            "A GoldenCorpus map entry nothing in tests/golden references: drop it, or add the case "
            + "that was meant to exercise it.");
    }

    private static string? Record(HashSet<string> seen, string key, Func<string, string?> resolve)
    {
        seen.Add(key);
        return resolve(key);
    }

    private static IEnumerable<string> Dead(
        string map,
        IEnumerable<string> declared,
        HashSet<string> requested) =>
        declared
            .Where(key => !requested.Contains(key))
            .Select(key => $"{map}: {key.ReplaceLineEndings(" ⏎ ")}");
}
