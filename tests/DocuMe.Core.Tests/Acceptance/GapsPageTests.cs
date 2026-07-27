using System.Text.RegularExpressions;
using DocuMe.Core.Config;
using DocuMe.Core.Drift;
using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// <c>docs/wiki/_meta/GAPS.md</c> held to the pages and tests it credits (PLAN.md §9 step 3, §11).
/// </summary>
/// <remarks>
/// <para>
/// The gaps list is the one wiki file whose entries are almost entirely claims *about other files*:
/// "documented on that page, under that heading", "asserted by that test class", "belongs on this page
/// once observed". <see cref="DogfoodWikiTests"/> cannot reach any of it, because
/// <c>wiki.exclude</c> keeps <c>_meta/</c> out of the tree it loads, so every one of those
/// cross-references was unguarded: a page could be renamed, a heading retitled or a test class deleted
/// and this file would go on crediting it.
/// </para>
/// <para>
/// The failure that motivated the class is the direction that costs most. An entry naming a
/// destination that does not exist reads as an instruction, and the reader is a generation run:
/// <c>/docs-loop</c> told to put an observation "on the feedback page" has no such page to open, and
/// the wiki's numbered taxonomy gives it no obvious place to invent one. A wrong citation on a
/// reference page misleads a human who can go and look; a wrong citation here sends an agent to write.
/// </para>
/// </remarks>
public sealed partial class GapsPageTests
{
    private const string PagePath = "_meta/GAPS.md";

    /// <summary>
    /// The section heading <see cref="DogfoodWikiTests"/> quotes back at the reader when a shipped file
    /// no page covers turns up. A retitled section would leave that message pointing at nothing.
    /// </summary>
    private const string ShippedSection = "Shipped but no page describes it";

    [Fact]
    public void Every_wiki_page_the_gaps_list_cites_exists_and_publishes()
    {
        var cited = CitedPages();
        var published = PublishedPaths();

        // Vacuous-pass guard: a rewrite that dropped every citation would otherwise pass by citing
        // nothing. Five today, across the two open items and the three answered ones.
        cited.Count.ShouldBeGreaterThanOrEqualTo(4);

        var missing = cited.Where(path => !published.Contains(path)).ToList();

        var message =
            $"{PagePath} cites a wiki page that is not in the published tree. An entry here names where "
            + "something is documented or where it belongs, and a generation run reads that as an "
            + "instruction, so a stale path sends it to write a page nobody asked for. Published: "
            + $"{string.Join(", ", published.OrderBy(path => path, StringComparer.Ordinal))}. Missing:";

        missing.ShouldBeEmpty(message);
    }

    [Fact]
    public void Every_heading_the_gaps_list_credits_exists_on_the_page_it_credits()
    {
        var credits = HeadingCredits();

        // Vacuous-pass guard: the pairing below finds nothing if the phrasing changes, and finding
        // nothing must not read as agreement.
        credits.Count.ShouldBeGreaterThanOrEqualTo(3);

        var wrong = new List<string>();

        foreach (var (page, heading) in credits)
        {
            var headings = HeadingsOf(page);

            if (!headings.Contains(heading, StringComparer.Ordinal))
            {
                wrong.Add($"{page} has no heading \"{heading}\" (has: {string.Join(" / ", headings)})");
            }
        }

        const string message =
            PagePath + " credits a heading that its page does not have. The heading is how a reader "
            + "finds the answer an entry says was written down; a retitled section turns the entry into "
            + "a claim the reader cannot check. Mismatches:";

        wrong.ShouldBeEmpty(message);
    }

    [Fact]
    public void Every_test_class_the_gaps_list_credits_exists()
    {
        var credited = CreditedTestClasses();

        credited.Count.ShouldBeGreaterThanOrEqualTo(2);

        var known = typeof(GapsPageTests).Assembly
            .GetTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var phantom = credited.Where(name => !known.Contains(name)).ToList();

        const string message =
            PagePath + " credits a test class that no longer exists. \"Nothing today, and it is now "
            + "checked rather than asserted\" is only true while the checker does, and a deleted guard "
            + "leaves the section asserting exactly what it says it stopped asserting. Phantom:";

        phantom.ShouldBeEmpty(message);
    }

    [Fact]
    public void The_composite_action_entry_is_true_a_change_under_actions_reaches_the_page_it_names()
    {
        // The entry's claim is not that the glob is spelled in the frontmatter but that a change to
        // actions/ "now surfaces as drift". Answer it with the planner a real drift run uses rather
        // than by reading the frontmatter, which is the check that would have passed before the fix.
        var tree = Load();
        var report = DriftPlanner.Plan("baseline", "head", ["actions/action.yml"], tree.Pages);

        var reached = report.Pages.Select(page => page.Path).ToList();

        const string message =
            "The gaps list says the composite action now surfaces as drift because `actions/*.yml` is in "
            + "30-automation/workflows.md's `sources`. A change under actions/ that reaches no page puts "
            + "the entry back in the state it describes as fixed: documented, and invisible to drift.";

        reached.ShouldContain("30-automation/workflows.md", message);
    }

    [Fact]
    public void The_mermaid_entry_names_the_same_refusals_as_the_page_it_credits()
    {
        var conversion = File.ReadAllText(WikiPath("20-reference/conversion.md"));
        var gaps = Gaps();

        // Every rejected-header row, not the first: a page that grew a third row while the gaps list
        // went on saying "two" is the drift this asserts, and Match() would have missed it.
        var rejected = RejectedHeaderRow()
            .Matches(conversion)
            .Select(match => match.Groups["header"].Value)
            .ToList();

        rejected.ShouldBe(["pie", "graph TD;"], "20-reference/conversion.md's rejected-header table moved.");

        // The gaps list spells one of them in a code span and the other in prose, which is why this
        // asserts the two facts separately rather than string-matching a sentence.
        const string lostPie = PagePath + " stopped naming the `pie` refusal.";
        const string lostSemicolon =
            PagePath + " stopped naming the trailing-semicolon refusal, which is the one a reader hits "
            + "by writing `graph TD;` the way mermaid.js accepts.";

        gaps.ShouldContain("`pie`", Case.Sensitive, lostPie);
        gaps.ShouldContain("trailing semicolon", Case.Sensitive, lostSemicolon);

        // The third refusal iter156 measured stays OUT of the table on purpose: that table is pinned
        // to what the golden corpus proves (MermaidAcceptanceTests renders every row and fails a row
        // the corpus never rejects), and no golden case carries frontmatter. It is prose on both
        // pages instead, so this binds the pair the same way rather than leaving it unheld.
        const string lostFrontmatter =
            " stopped naming the frontmatter refusal. It fails every diagram type, including the six "
            + "the renderer does implement, so it is the one that looks least like a dialect gap.";

        gaps.ShouldContain("frontmatter", Case.Sensitive, PagePath + lostFrontmatter);
        conversion.ShouldContain(
            "frontmatter",
            Case.Sensitive,
            "20-reference/conversion.md" + lostFrontmatter);
    }

    [Fact]
    public void The_header_sentence_matches_how_docs_loop_actually_picks_its_next_unit()
    {
        var skill = File.ReadAllText(
            Path.Combine(RepoRoot, "plugin", "skills", "docs-loop", "SKILL.md"));
        var gaps = Gaps();

        // The two halves of the corrected sentence, each pinned to the skill that owns the behaviour.
        // It used to say /docs-loop reads this file "to find the next page worth writing", which is
        // PROGRESS.md's job; this file is read so a settled-impossible question is not re-asked.
        const string selectionMoved =
            "docs-loop no longer selects its unit from PROGRESS.md in file order, so the gaps list's "
            + "description of what reads what is now wrong.";
        const string wrongInventory =
            PagePath + " must name PROGRESS.md as where the next unit comes from. Crediting this file "
            + "for that sends a run to the wrong inventory.";
        const string missingReason =
            PagePath + " must say why /docs-loop reads it, which is so an earlier run's unsettled "
            + "question is not asked again.";

        skill.ShouldContain("takes the first `todo` in file order", Case.Sensitive, selectionMoved);
        gaps.ShouldContain("`_meta/PROGRESS.md`", Case.Sensitive, wrongInventory);
        gaps.ShouldContain("re-ask", Case.Sensitive, missingReason);
    }

    [Fact]
    public void The_gaps_list_is_excluded_from_publishing_by_the_default_it_claims()
    {
        // The page opens by saying it is excluded from publishing and names the mechanism. DocuMe's own
        // repo has no docume.json at all, so the claim rests on the default holding and on `docume init`
        // writing it: this repo becomes a consumer of that default at gate-m2.
        const string defaultMoved =
            "The gaps list claims wiki.exclude defaults to _meta/**. It is the only thing keeping an "
            + "internal backlog, with its unanswered questions, out of a published space.";
        const string published = PagePath + " loaded as a publishable page.";

        new DocumeConfig().Wiki.Exclude.ShouldContain("_meta/**", defaultMoved);

        Load().Pages
            .Select(page => page.Path)
            .ShouldNotContain(PagePath, published);
    }

    [Fact]
    public void The_section_heading_the_dogfood_suite_quotes_still_exists()
    {
        const string retitled =
            "DogfoodWikiTests quotes this section by name when a shipped file no page covers turns up. "
            + "Retitling it leaves that failure message pointing a reader at a section that is gone.";

        Gaps().ShouldContain($"## {ShippedSection}", Case.Sensitive, retitled);
    }

    /// <summary>
    /// Wiki-relative page paths the gaps list cites, as <c>NN-directory/page.md</c> inside a code span.
    /// <c>_meta/</c> paths are left out on purpose: they are not published pages, so
    /// <see cref="Every_wiki_page_the_gaps_list_cites_exists_and_publishes"/> would fail on them for the
    /// wrong reason.
    /// </summary>
    private static List<string> CitedPages() =>
        CitedPage()
            .Matches(Gaps())
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every <c>under "Heading"</c> paired with the page cited nearest before it. The gaps list writes
    /// its credits in that order in every entry, and pairing on position is what ties a heading to a
    /// page without the entries having to carry a machine-readable form.
    /// </summary>
    private static List<(string Page, string Heading)> HeadingCredits()
    {
        var gaps = Gaps();

        var tokens = CitedPageOrHeading()
            .Matches(gaps)
            .Select(match => (
                Path: match.Groups["path"].Value,
                Heading: match.Groups["heading"].Value))
            .ToList();

        var credits = new List<(string, string)>();
        var page = string.Empty;

        foreach (var token in tokens)
        {
            if (token.Path.Length > 0)
            {
                page = token.Path;
                continue;
            }

            if (page.Length > 0)
            {
                // The gaps list wraps its prose, so a credited heading arrives carrying the newline and
                // the continuation line's indentation. Comparing that against a one-line heading fails
                // on every credit, which is the wrong answer given loudly.
                credits.Add((page, Whitespace().Replace(token.Heading, " ")));
            }
        }

        return credits;
    }

    private static List<string> CreditedTestClasses() =>
        CreditedTestClass()
            .Matches(Gaps())
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Heading text on a wiki page, at any level, trimmed of its hashes.</summary>
    private static List<string> HeadingsOf(string pagePath) =>
        File.ReadAllLines(WikiPath(pagePath))
            .Where(line => line.StartsWith('#'))
            .Select(line => line.TrimStart('#').Trim())
            .ToList();

    private static HashSet<string> PublishedPaths() =>
        Load().Pages.Select(page => page.Path).ToHashSet(StringComparer.Ordinal);

    private static string Gaps() => File.ReadAllText(WikiPath(PagePath));

    private static string WikiPath(string pagePath) =>
        Path.Combine(RepoRoot, "docs", "wiki", pagePath.Replace('/', Path.DirectorySeparatorChar));

    private static WikiTree Load() => WikiTree.Load(Path.Combine(RepoRoot, "docs", "wiki"));

    [GeneratedRegex(
        @"`(?<path>\d\d-[a-z]+/[a-z0-9-]+\.md)`",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CitedPage();

    [GeneratedRegex(
        @"`(?<path>\d\d-[a-z]+/[a-z0-9-]+\.md)`|under ""(?<heading>[^""]+)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CitedPageOrHeading();

    [GeneratedRegex(
        @"`(?<name>[A-Z][A-Za-z0-9]*Tests)`",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CreditedTestClass();

    /// <summary>A newline and its continuation indent, or any other whitespace run.</summary>
    [GeneratedRegex(@"\s+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    /// <summary>
    /// A row of conversion.md's rejected-header table: the header in a code span in the first cell.
    /// Anchored to the table by requiring the "Why it fails" wording's column shape, so a code span in
    /// the surrounding prose is not read as a row.
    /// </summary>
    [GeneratedRegex(
        @"^\|\s*`(?<header>[^`]+)`\s*\|\s*(?:The renderer|A trailing)[^|]*\|\s*$",
        RegexOptions.ExplicitCapture | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex RejectedHeaderRow();

    private static string RepoRoot { get; } = Locate();

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>: the wiki ships in the tree and is not
    /// copied beside the test assembly, so the shipped copy is the one under test.
    /// </summary>
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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so docs/wiki cannot be found.");
    }
}
