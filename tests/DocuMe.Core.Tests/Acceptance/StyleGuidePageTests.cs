using System.Reflection;
using DocuMe.Core.Markdown;
using DocuMe.Core.Tests.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// <c>docs/wiki/_meta/STYLE.md</c> against what it describes: the tree it counts, the constraints
/// <see cref="DogfoodWikiTests"/> actually enforces, and the degradation codes the converter actually
/// emits.
/// </summary>
/// <remarks>
/// <para>
/// This file is not a page. All three skills read it at the start of every run for "the consumer's
/// tone, audience, section taxonomy and marker conventions" (each <c>SKILL.md</c>'s inputs table), so
/// it is <em>generative instruction</em>: a wrong sentence here is written into pages rather than
/// merely misread. At iter94 it was wrong four ways at once — it had no **Verification** section at
/// all, though <c>docs-loop</c>'s Markers section says that section owns the marker conventions; its
/// "constructs to avoid" list named six of the seven codes, omitting the ordered task list; its
/// "constraints that are checked" list named four of six enforced contracts and widened "shipped" to
/// the whole repo; and its page count had been true two pages earlier.
/// </para>
/// <para>
/// Each check derives its expectation rather than restating it: the markers come from the golden case
/// that pins them, the constraints from the suite's own <c>[Fact]</c> methods, the codes from
/// <see cref="ConversionDiagnosticCodes"/>, the counts from the loaded tree. A new degradation code, a
/// new dogfood contract or a new page therefore reddens this class until the guide says so, which is
/// the only way a description stays true to a thing that keeps moving.
/// </para>
/// <para>
/// <see cref="Markdown.ConversionReferencePageTests"/> owns the other half of the "constructs to
/// avoid" list: whether each bullet draws the converter's real <em>boundary</em> (explicitly-left
/// columns, Atlassian-documented-but-unmapped fence languages). This class owns completeness and the
/// code-to-bullet mapping; that one owns the wording of the two bullets whose trigger is narrower than
/// its construct class. Neither subsumes the other.
/// </para>
/// <para>
/// Accepted cost, recorded so it is not read as a bug: the page count under **Scope** is exact, while
/// <see cref="DogfoodWikiTests"/> deliberately keeps its own page-count assertion inexact ("a test that
/// fails on every new page is a test people delete"). The difference is what each one is: that one is a
/// build gate on the tree, this one holds a <em>claim about the tree</em> that had already gone stale
/// once. The edit it forces is one digit in the file whose job is to say the wiki is deliberately
/// small.
/// </para>
/// </remarks>
public sealed class StyleGuidePageTests
{
    private const string StylePath = "docs/wiki/_meta/STYLE.md";

    /// <summary>The golden case that pins marker pass-through (PLAN.md §7's markers row, rule §4.3).</summary>
    private const string MarkerGolden = "markers.md";

    private const string WarningSign = "⚠️";

    /// <summary>
    /// One line of markdown that fires each declared code, so the mapping from code to bullet is
    /// checked against the converter's live classification rather than against its constant names. The
    /// key set is asserted equal to <see cref="ConversionDiagnosticCodes"/>'s, so a new code has to
    /// arrive here and in the guide together.
    /// </summary>
    private static readonly (string Code, string Markdown)[] Triggers =
    [
        (ConversionDiagnosticCodes.UnknownFenceLanguage, "```octave\nx = 1\n```\n"),
        (ConversionDiagnosticCodes.MixedTaskList, "- [x] done\n- plain item\n"),
        (ConversionDiagnosticCodes.SamePageAnchorLink, "See [the overview](#overview) below.\n"),
        (ConversionDiagnosticCodes.TableAlignmentDropped, "| a |\n|:-:|\n| 1 |\n"),
        (ConversionDiagnosticCodes.OrderedListStartDropped, "3. third\n4. fourth\n"),
        (ConversionDiagnosticCodes.AlertTypeCollapsed, "> [!IMPORTANT]\n> Body.\n"),
        (ConversionDiagnosticCodes.TaskListNumberingDropped, "1. [x] done\n2. [ ] pending\n"),
    ];

    /// <summary>
    /// What the guide has to say for each contract <see cref="DogfoodWikiTests"/> enforces, keyed by
    /// the test method that enforces it. The keys are asserted equal to that class's <c>[Fact]</c>
    /// methods, so adding a contract without describing it fails here — the guide's own framing is
    /// "contracts rather than preferences", and a contract nobody wrote down is a preference.
    /// </summary>
    private static readonly Dictionary<string, string> ConstraintTokens = new(StringComparer.Ordinal)
    {
        // The load validates titles and the converter fails a page whose relative link leaves the
        // tree, so both land on whichever test loads the tree first.
        ["The_wiki_loads_as_a_tree_that_can_be_published"] = "Titles are unique",
        ["Every_page_converts_with_no_failure_and_no_degradation"] = "zero failures and zero degradations",
        ["Every_page_declares_the_code_it_derives_from"] = "at least one `sources` glob",
        ["Every_sources_glob_matches_a_file_that_exists"] = "matches a file that exists",
        ["Every_shipped_path_reaches_some_page_through_its_sources"] = "Every shipped path reaches some page",
        ["Every_directory_publishes_through_its_own_index_page"] = "publishes through its own `README.md`",
    };

    [Fact]
    public void The_verification_section_names_every_marker_the_golden_corpus_pins()
    {
        var section = Section("Verification");

        foreach (var marker in GoldenMarkers())
        {
            var missing =
                $"{StylePath} does not name the marker '{marker}', which the converter passes through "
                + $"as text (golden case {MarkerGolden}). All three skills read this section for the "
                + "repo's marker conventions, so a marker missing here is one a run either does not use "
                + "or uses to its own convention.";

            section.ShouldContain($"{WarningSign} {marker}", Case.Sensitive, missing);
        }

        const string noAlternative =
            "The Verification section does not say where a claim that earns no marker goes, so the only "
            + "reading left is that it stays on the page unmarked.";

        section.ShouldContain("GAPS.md", Case.Sensitive, noAlternative);
    }

    [Fact]
    public void Every_contract_the_dogfood_suite_enforces_is_listed_as_checked()
    {
        var enforced = DogfoodContracts();

        const string drifted =
            "The dogfood suite's contracts and the style guide's list of them have diverged. Add the new "
            + "test's entry to ConstraintTokens and its bullet to the guide, or remove both.";

        ConstraintTokens.Keys.ShouldBe(enforced, ignoreOrder: true, drifted);

        var section = Section("Constraints that are checked");

        foreach (var (test, token) in ConstraintTokens)
        {
            var missing =
                $"{StylePath} does not describe the contract {nameof(DogfoodWikiTests)}.{test} enforces. "
                + "A build gate nobody wrote down reads as a preference, and the run that breaks it finds "
                + "out from a red build rather than from the guide it read first.";

            section.ShouldContain(token, Case.Sensitive, missing);
        }

        // Both classes gate this list, and a reader who has to find the failing assertion needs both
        // names.
        section.ShouldContain(nameof(DogfoodWikiTests), Case.Sensitive, "the enforcing suite is unnamed");
        section.ShouldContain(nameof(StyleGuidePageTests), Case.Sensitive, "this suite is unnamed");
    }

    /// <summary>
    /// The shipped set is the one boundary in that list a run would otherwise get wrong in the
    /// expensive direction: believing <c>tests/</c> or <c>.github/</c> needs a page means writing one,
    /// and a page about how DocuMe is made is a page a reviewer has to reject rather than correct.
    /// </summary>
    [Fact]
    public void The_shipped_set_the_guide_names_is_the_set_the_suite_walks()
    {
        var section = Section("Constraints that are checked");

        DogfoodWikiTests.ShippedRoots.Length.ShouldBeGreaterThan(1);

        foreach (var root in DogfoodWikiTests.ShippedRoots)
        {
            var missing =
                $"{StylePath} does not name '{root}' as shipped, so a run cannot tell whether a change "
                + "there needs a page. The guide's own sentence is what decides that.";

            section.ShouldContain($"`{root}`", Case.Sensitive, missing);
        }

        const string unbounded =
            "The guide states no upper bound on what 'shipped' means, so it reads as the whole repo — "
            + "including the tests and CI that are how DocuMe is made rather than what it hands over.";

        section.ShouldContain("how DocuMe", Case.Sensitive, unbounded);
    }

    [Fact]
    public void Every_degradation_code_has_a_bullet_and_still_fires_from_its_own_sample()
    {
        var declared = DeclaredCodes();

        const string drifted =
            "The converter's degradation codes and this class's samples have diverged, so the guide's "
            + "completeness check no longer covers every code. Add the new code's sample here and its "
            + "bullet to the guide.";

        Triggers.Select(trigger => trigger.Code).ShouldBe(declared, ignoreOrder: true, drifted);

        var section = Section("Constructs to avoid");

        foreach (var (code, markdown) in Triggers)
        {
            var diagnostics = new List<ConversionDiagnostic>();
            ConfluenceStorageConverter.Convert(markdown, diagnostics: diagnostics);

            var moved =
                $"This sample no longer reports {code}, so the converter's classification moved and every "
                + $"description of it — in {StylePath} and in docs/wiki/20-reference/conversion.md — is "
                + "stale by definition.";

            diagnostics
                .Exists(diagnostic => string.Equals(diagnostic.Code, code, StringComparison.Ordinal))
                .ShouldBeTrue(moved);

            var missing =
                $"{StylePath}'s 'constructs to avoid' list has no bullet ending in ({code}). A construct "
                + "that degrades and is not listed is one a generation run will use, and the strict bar "
                + $"then fails the whole wiki on it ({nameof(DogfoodWikiTests)}).";

            section.ShouldContain($"(`{code}`)", Case.Sensitive, missing);
        }

        // One bullet per code and no others: a bullet with no code is a rule whose warning a reader
        // cannot trace back to it.
        var bullets = section
            .Split('\n')
            .Count(line => line.StartsWith("- ", StringComparison.Ordinal));

        bullets.ShouldBe(
            declared.Length,
            $"{StylePath} lists {bullets} constructs for {declared.Length} codes.");
    }

    [Fact]
    public void The_page_count_the_guide_gives_is_the_tree_it_describes()
    {
        var tree = WikiTree.Load(Path.Combine(RepoRoot, "docs", "wiki"));

        var indexes = tree.Pages
            .Count(page => page.Path.EndsWith("README.md", StringComparison.Ordinal));
        var topics = tree.Pages.Count - indexes;

        // Vacuous-pass guards: a tree that loaded nothing would make both numbers agree with a guide
        // that says nothing.
        topics.ShouldBeGreaterThan(1);
        indexes.ShouldBeGreaterThan(1);

        var section = Section("Scope");

        var stale =
            $"{StylePath} counts the wiki wrong: the tree holds {topics} topic pages and {indexes} "
            + "indexes. The sentence is the guide's whole argument for keeping the wiki small, and a "
            + "count that drifts is the argument going quiet.";

        section.ShouldContain($"{topics} topic pages", Case.Sensitive, stale);
        section.ShouldContain($"{indexes} indexes", Case.Sensitive, stale);
    }

    /// <summary>
    /// "One H1 per page" is the rule; what it costs is a heading that disappears. Executed rather than
    /// asserted from the renderer's source, because the sentence in the guide is a promise about output.
    /// </summary>
    [Fact]
    public void A_second_H1_vanishes_with_no_warning_and_the_guide_says_so()
    {
        var diagnostics = new List<ConversionDiagnostic>();
        var storage = ConfluenceStorageConverter.Convert(
            "# Page Title\n\nFirst body.\n\n# Second Heading\n\nSecond body.\n",
            diagnostics: diagnostics);

        storage.ShouldNotContain("Second Heading", Case.Sensitive);
        storage.ShouldContain("Second body.", Case.Sensitive);
        diagnostics.ShouldBeEmpty("A dropped H1 now reports, so the guide's 'no warning' is wrong.");

        var section = Section("Structure");

        const string understated =
            "The guide states the one-H1 rule without its consequence, so a page with two H1s loses a "
            + "heading's text in Confluence with nothing — not the converter, not the strict bar — "
            + "reporting it.";

        section.ShouldContain("no warning", Case.Sensitive, understated);
    }

    [Fact]
    public void The_section_scan_found_the_sections_these_checks_read()
    {
        // Every check above searches one section, so an unreadable file or a renamed heading has to
        // fail loudly here rather than passing four assertions against an empty string.
        string[] read = ["Scope", "Structure", "Verification", "Constraints that are checked", "Constructs to avoid"];

        foreach (var heading in read)
        {
            Section(heading).Length.ShouldBeGreaterThan(80, $"{StylePath}'s '{heading}' section is near-empty.");
        }

        Style().Length.ShouldBeGreaterThan(1000, $"{StylePath} is far shorter than the guide these tests scan.");
        DeclaredCodes().Length.ShouldBe(7);
        DogfoodContracts().Count.ShouldBe(6);
        GoldenMarkers().Count.ShouldBe(2);
    }

    /// <summary>The markers the golden case pins, as their bare words (<c>UNVERIFIED</c>).</summary>
    private static List<string> GoldenMarkers()
    {
        var golden = File.ReadAllText(Path.Combine(GoldenCorpus.Directory, MarkerGolden));

        return golden
            .Split(WarningSign, StringSplitOptions.None)
            .Skip(1)
            .Select(after => after.TrimStart().Split(':')[0].Trim())
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The dogfood suite's <c>[Fact]</c> methods: one per enforced contract.</summary>
    private static List<string> DogfoodContracts() =>
        typeof(DogfoodWikiTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<FactAttribute>().Any())
            .Select(method => method.Name)
            .ToList();

    private static string[] DeclaredCodes() =>
        typeof(ConversionDiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    /// <summary>
    /// The text under one <c>##</c> heading, up to the next one. Scoped rather than whole-file on
    /// purpose: a token searched for across the whole guide can be satisfied by a sentence in an
    /// unrelated section, which is how a check passes over the claim it was written to hold.
    /// </summary>
    private static string Section(string heading)
    {
        var lines = Style().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var body = new List<string>();
        var inside = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inside)
                {
                    break;
                }

                inside = string.Equals(line[3..].Trim(), heading, StringComparison.Ordinal);
                continue;
            }

            if (inside)
            {
                body.Add(line);
            }
        }

        inside.ShouldBeTrue($"{StylePath} has no '## {heading}' section.");

        return string.Join('\n', body);
    }

    private static string Style() =>
        File.ReadAllText(Path.Combine(RepoRoot, "docs", "wiki", "_meta", "STYLE.md"));

    private static string RepoRoot { get; } = Locate();

    /// <summary>Walks up to the directory holding <c>DocuMe.slnx</c>: the guide ships in the tree.</summary>
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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so {StylePath} cannot be found.");
    }
}
