using System.Reflection;
using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// <c>docs/wiki/20-reference/conversion.md</c>'s degradation table, and
/// <c>docs/wiki/_meta/STYLE.md</c>'s "constructs to avoid" list, against the codes the converter
/// actually emits and the boundaries it actually draws.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConversionDiagnosticsTests"/> proves the behaviour; nothing tied the two reader-facing
/// artifacts to it, and at iter90 <em>four of the seven rows</em> were wrong the same way: each named a
/// construct class wider than what reports, so an author with the near-miss construct expects a warning
/// that correctly never comes, and reads the silence as a bug. The table said column alignment "is not
/// representable" (an explicitly left <c>:---</c> column publishes exactly as GitHub renders it and
/// stays silent), that <c>alert-type-collapsed</c> is "two alert types mapping to one panel" (only
/// <c>[!IMPORTANT]</c> reports, because <c>[!NOTE]</c> owns <c>info</c> and still reads the way its
/// author meant), and that <c>unknown-fence-language</c> is "a language Confluence does not document"
/// (the brush map takes two-source confirmation, so Atlassian-documented <c>octave</c> reports too). Its
/// <c>same-page-anchor-link</c> row asserted an <em>absence</em> the build has not settled: PLAN.md §13
/// S2's live half would unlock <c>ac:anchor</c> macros, so "has no storage-format equivalent" is a claim
/// this repo cannot make.
/// </para>
/// <para>
/// STYLE.md carried the same wrong cause for fence languages, and it is generative instruction: a skill
/// trusting "languages Confluence does not document" would write <c>```octave</c> believing it safe and
/// break the strict bar <see cref="Acceptance.DogfoodWikiTests"/> holds this wiki to.
/// </para>
/// <para>
/// Each boundary check <em>executes the near-miss construct</em> rather than trusting the prose on either
/// side. That is the point: a hand-listed "the row must say 'silent'" would keep passing if the converter
/// started reporting explicit-left columns, and the row would be stale with a green build. Here the
/// classification and the wording fail together.
/// </para>
/// </remarks>
public sealed class ConversionReferencePageTests
{
    private const string PagePath = "docs/wiki/20-reference/conversion.md";
    private const string StylePath = "docs/wiki/_meta/STYLE.md";

    /// <summary>The header cell that opens the degradation table, and the row scan's anchor.</summary>
    private const string TableHeader = "| Code |";

    /// <summary>
    /// One boundary per code whose real trigger is narrower, or whose cause is other, than the
    /// construct class a reader would name. <c>Markdown</c> is the near-miss sample, <c>Reports</c> is
    /// which side of the boundary it falls on, and <c>DocMustName</c> is what the description has to
    /// carry for a reader to place their own construct.
    /// <para>
    /// Listed, not derived: which boundary is surprising enough to need naming is a judgement about
    /// readers, the same judgement (and for the alert row the same fact) that
    /// <c>QuoteBlockRenderer.CollapsedMarkers</c> writes out by hand rather than deriving from the panel
    /// map. The three codes absent here are the ones whose row already describes exactly what fires:
    /// a mixed task list, an ordered list starting past 1, an ordered task list.
    /// </para>
    /// </summary>
    private static readonly (string Code, string Markdown, bool Reports, string[] DocMustName)[] Boundaries =
    [

        // Atlassian documents Octave; Prism has no component for it, so it fails the second
        // confirmation and keeps reporting. "Confluence does not document it" is the wrong cause.
        (ConversionDiagnosticCodes.UnknownFenceLanguage, "```octave\nx = 1\n```\n", true, ["Prism", "octave"]),

        // What is lost is the destination, not an equivalent that does not exist. S2 is open.
        (ConversionDiagnosticCodes.SamePageAnchorLink, "See [the overview](#overview) below.", true, ["link text"]),

        // Markdig fills Alignment only where the author wrote a colon, so explicit-left is
        // distinguishable and publishes the way GitHub renders it.
        (ConversionDiagnosticCodes.TableAlignmentDropped, "| a |\n|:--|\n| 1 |", false, ["explicitly left", "silent"]),

        // Of the two markers on `info`, only the one that does not own the panel loses anything.
        (ConversionDiagnosticCodes.AlertTypeCollapsed, "> [!NOTE]\n> Body.", false, ["[!IMPORTANT]", "[!NOTE]", "silent"]),
    ];

    [Fact]
    public void The_page_documents_every_degradation_code_the_converter_emits_and_no_others()
    {
        var documented = TableRows().Select(CodeOf).ToList();

        documented.ShouldBe(DeclaredCodes(), ignoreOrder: true);
    }

    [Fact]
    public void Each_boundary_the_converter_draws_is_named_where_the_page_describes_the_code()
    {
        var rows = TableRows();

        foreach (var (code, markdown, reports, mustName) in Boundaries)
        {
            var diagnostics = new List<ConversionDiagnostic>();
            ConfluenceStorageConverter.Convert(markdown, diagnostics: diagnostics);

            var fired = diagnostics.Exists(d => string.Equals(d.Code, code, StringComparison.Ordinal));
            var moved = $"The converter's own classification of this {code} sample moved, so every "
                + $"description of that boundary, in {PagePath} and in {StylePath}, is stale by definition.";
            fired.ShouldBe(reports, moved);

            var row = rows.Find(r => string.Equals(CodeOf(r), code, StringComparison.Ordinal));
            row.ShouldNotBeNull($"{PagePath} has no degradation-table row for {code}.");

            foreach (var token in mustName)
            {
                var missing = $"{PagePath}'s {code} row does not name the boundary '{token}', so it reads as "
                    + "the whole construct class. An author with the near-miss construct then expects a "
                    + "warning that correctly never comes.";
                row.ShouldContain(token, Case.Sensitive, missing);
            }
        }
    }

    /// <summary>
    /// The style guide is instruction, not description, so a wrong cause there is written into pages
    /// rather than merely misread: the fence bullet decides which languages a skill will use.
    /// </summary>
    [Fact]
    public void The_style_guides_constructs_to_avoid_draw_the_same_boundaries()
    {
        var style = Style();

        const string wholeRule = $"{StylePath} still gives Atlassian's documentation as the whole rule for fence "
            + "languages. A skill following it writes ```octave, which degrades and fails the strict bar.";
        style.ShouldContain("Prism", Case.Sensitive, wholeRule);

        var unnamed = $"{StylePath} does not name the documented-but-unmapped case.";
        style.ShouldContain("octave", Case.Sensitive, unnamed);

        const string allAlignment = $"{StylePath} tells a skill to avoid all column alignment, which costs a `:---` "
            + "column that publishes exactly as GitHub renders it.";
        style.ShouldContain("explicitly left", Case.Sensitive, allAlignment);
    }

    [Fact]
    public void The_table_scan_found_the_rows_these_checks_read()
    {
        // Both checks above pass vacuously on an empty scan: no rows means no codes to compare and no
        // row to search for a token.
        var rows = TableRows();
        rows.ShouldNotBeEmpty($"The '{TableHeader}' table was not found in {PagePath}.");
        rows.ShouldAllBe(r => r.Contains('`'));

        Page().Length.ShouldBeGreaterThan(2000, $"{PagePath} is far shorter than the page these tests scan.");
        Style().Length.ShouldBeGreaterThan(1000, $"{StylePath} is far shorter than the guide these tests scan.");

        DeclaredCodes().Length.ShouldBe(7);
        Boundaries.Length.ShouldBe(4);
    }

    /// <summary>Every line of the degradation table, separator and header excluded.</summary>
    private static List<string> TableRows()
    {
        var rows = new List<string>();
        var inTable = false;

        foreach (var line in File.ReadAllLines(Path.Combine(RepoRoot, Native(PagePath))))
        {
            if (line.StartsWith(TableHeader, StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!inTable || line.StartsWith("|---", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.StartsWith('|'))
            {
                break;
            }

            rows.Add(line);
        }

        return rows;
    }

    /// <summary>The code a row names: the first backticked token in its first cell.</summary>
    private static string CodeOf(string row)
    {
        var open = row.IndexOf('`', StringComparison.Ordinal);
        var close = row.AsSpan(open + 1).IndexOf('`') + open + 1;

        return row[(open + 1)..close];
    }

    private static string[] DeclaredCodes() =>
        typeof(ConversionDiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    private static string Page() => File.ReadAllText(Path.Combine(RepoRoot, Native(PagePath)));

    private static string Style() => File.ReadAllText(Path.Combine(RepoRoot, Native(StylePath)));

    private static string Native(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static string RepoRoot { get; } = Locate();

    /// <summary>Walks up to the directory holding <c>DocuMe.slnx</c>: both files ship in the tree.</summary>
    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so {PagePath} cannot be found.");
    }
}
