using DocuMe.Core.Acceptance;
using DocuMe.Core.Drift;
using DocuMe.Core.Markdown;
using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// DocuMe's own wiki (<c>docs/wiki</c>) held to the bar it documents (PLAN.md §3, §12: "dogfooded —
/// DocuMe's own docs published with DocuMe"). The corpus here is the shipped tree, not a fixture:
/// these assertions fail when someone edits a page, which is the point.
/// </summary>
/// <remarks>
/// <para>
/// Why the strict bar rather than the golden corpus's accepted-loss policy: the goldens exist to pin
/// what the converter does with a construct, degradations included, so they deliberately contain
/// constructs that degrade. This wiki is *content*, and a tool whose own documentation converts badly
/// is not making a good argument. <c>docs/wiki/_meta/STYLE.md</c> lists the constructs to avoid and
/// names this class as what enforces them.
/// </para>
/// <para>
/// The <c>sources</c> check is the one that would otherwise rot silently. A glob matching no file
/// makes its page permanently invisible to <c>docume drift</c> — no error, no warning, just a page
/// that never appears in a drift report and therefore never gets refreshed. It is the failure mode of
/// documentation-adjacent config generally: wrong in a way that looks like "nothing changed".
/// </para>
/// </remarks>
public sealed class DogfoodWikiTests
{
    /// <summary>Directory names that hold no source and would dominate the repo walk.</summary>
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.Ordinal) { ".git", ".mtk", "bin", "obj", "node_modules" };

    /// <summary>
    /// What a consumer receives: the marketplace this repo is, the tool, the plugin, the files
    /// <c>init</c> scaffolds, the composite action and the config schema. Everything else in the repo
    /// (tests, the build loop, CI for this repository) is how DocuMe is made rather than what it hands
    /// over, and the wiki documents the product.
    /// </summary>
    /// <remarks>
    /// Internal because <see cref="StyleGuidePageTests"/> holds the style guide's description of
    /// "shipped" to this list: the guide is what a generation run reads, and a run that believes
    /// <c>tests/</c> needs a page writes one. Paired with <see cref="UnshippedRoots"/> against the
    /// tree's own top level by
    /// <see cref="Every_top_level_directory_is_declared_shipped_or_not"/>.
    /// </remarks>
    internal static readonly string[] ShippedRoots =
        [".claude-plugin/", "actions/", "plugin/", "schema/", "src/", "templates/"];

    /// <summary>
    /// The complement: top-level directories that are how DocuMe is made rather than what it hands over,
    /// each classified on purpose.
    /// </summary>
    /// <remarks>
    /// Not a second definition of "shipped" — its job is to make the two lists together account for the
    /// whole top level. Both sweeps that ask what DocuMe hands over bound their population by
    /// <see cref="ShippedRoots"/> and pass over everything outside it without a word: the
    /// <c>sources</c> coverage below, and <see cref="ConsumerKnowledgeCoverageTests"/>'s rule §9.5 scan.
    /// So before this pairing an unclassified directory was exempt from both at once, and exempt
    /// silently.
    /// </remarks>
    private static readonly string[] UnshippedRoots =
    [
        ".claude/",  // this repo's own agent configuration: rules, references, MTK settings.
        ".github/",  // CI for this repository. The workflows `init` scaffolds live under templates/.
        "docs/",     // the wiki itself: what documents the product, not an artifact it describes.
        "tasks/",    // MTK's lessons and todo list — notes about building DocuMe.
        "tests/",    // this suite, and the golden corpus it asserts against.
        "tools/",    // the build loop, and this repo's own scaffolded copy of the render script.
    ];

    /// <summary>
    /// Shipped files no page derives from, each for a stated reason. Adding a line here is a decision
    /// that an artifact needs no page; leaving it out is the default.
    /// </summary>
    private static readonly HashSet<string> UndocumentedByDesign = new(StringComparer.Ordinal)
    {
        // Project plumbing: version, analyzer and packaging settings, described by the release notes
        // and the house standards rather than by a wiki page about DocuMe's behaviour.
        "src/DocuMe.Cli/DocuMe.Cli.csproj",
        "src/DocuMe.Core/DocuMe.Core.csproj",

        // The plugin's own README is documentation, not a documented artifact. Pointing a wiki page's
        // sources at it would report "the docs changed, so the docs may need changing".
        "plugin/README.md",
    };

    private const string UncoveredMessage =
        "A shipped file no page's `sources` glob covers can never arrive as drift, so the page "
        + "describing it goes stale with nothing reporting it (docs/wiki/_meta/GAPS.md, \"Shipped but "
        + "no page describes it\"). Either add a glob to the page that describes it, or add it to "
        + "UndocumentedByDesign with the reason. Uncovered:";

    [Fact]
    public void The_wiki_loads_as_a_tree_that_can_be_published()
    {
        var tree = Load();

        // Deliberately not an exact count: the number is expected to grow, and a test that fails on
        // every new page is a test people delete.
        tree.Pages.Count.ShouldBeGreaterThanOrEqualTo(6);

        // _meta holds the style guide, the gaps list and (once published) state.json. wiki.exclude's
        // default keeps them out of Confluence, and this is the assertion that the default is what
        // the repo actually relies on.
        tree.Pages
            .Select(page => page.Path)
            .ShouldAllBe(path => !path.StartsWith("_meta/", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_page_converts_with_no_failure_and_no_degradation()
    {
        var report = ConversionAcceptance.RunTree(Load(), AcceptancePolicy.Strict);

        var failures = FailureSummary(report);
        report.FailedPageCount.ShouldBe(0, failures);

        // Relative .md links are checked here rather than in their own test: a link to a page outside
        // the tree fails its page in the converter, so it lands in FailedPageCount above.
        var degradations = DegradationSummary(report);
        report.DiagnosticCount.ShouldBe(0, degradations);
        report.MeetsAcceptanceBar.ShouldBeTrue(failures);
    }

    [Fact]
    public void Every_page_declares_the_code_it_derives_from()
    {
        var tree = Load();

        var undeclared = tree.Pages
            .Where(page => page.Parsed.Frontmatter.Sources.Count == 0)
            .Select(page => page.Path)
            .ToList();

        const string message =
            "Every page needs at least one `sources` glob or `docume drift` can never flag it "
            + "(PLAN.md §5.2, §6.4). Pages missing one:";

        undeclared.ShouldBeEmpty(message);
    }

    [Fact]
    public void Every_sources_glob_matches_a_file_that_exists()
    {
        var tree = Load();
        var files = RepoFiles();

        // Feeding the whole repo in as the changed set turns DriftPlanner into the question "which
        // globs can ever match anything?" — and answers it with the same Matcher a real drift run
        // uses, rather than a second glob implementation that would eventually disagree with it.
        var report = DriftPlanner.Plan("baseline", "head", files, tree.Pages);

        var matched = report.Pages
            .SelectMany(page => page.Matches.Select(match => match.Pattern))
            .ToHashSet(StringComparer.Ordinal);

        // Vacuous-pass guards: an empty file list, or a plan that matched nothing, would make the
        // assertion below pass while proving nothing.
        files.Count.ShouldBeGreaterThan(100);
        matched.ShouldNotBeEmpty();
        report.AffectedCount.ShouldBe(tree.Pages.Count);

        var dead = tree.Pages
            .SelectMany(page => page.Parsed.Frontmatter.Sources.Select(source => (page.Path, source)))
            .Where(entry => !matched.Contains(entry.source))
            .Select(entry => $"{entry.Path} → {entry.source}")
            .ToList();

        const string message =
            "A `sources` glob matching no file in the repo makes its page invisible to `docume drift` "
            + "with no error anywhere. Dead globs:";

        dead.ShouldBeEmpty(message);
    }

    [Fact]
    public void Every_shipped_path_reaches_some_page_through_its_sources()
    {
        var tree = Load();

        var shipped = RepoFiles()
            .Where(file => ShippedRoots.Any(root => file.StartsWith(root, StringComparison.Ordinal)))
            .Where(file => !UndocumentedByDesign.Contains(file))
            .ToList();

        var report = DriftPlanner.Plan("baseline", "head", shipped, tree.Pages);

        var covered = report.Pages
            .SelectMany(page => page.Matches.SelectMany(match => match.Files))
            .ToHashSet(StringComparer.Ordinal);

        // Vacuous-pass guards: a shipped list that walked nothing, or a plan that matched nothing,
        // would make the assertion below pass by describing an empty repo.
        shipped.Count.ShouldBeGreaterThan(20);
        covered.ShouldNotBeEmpty();

        var invisible = shipped
            .Where(file => !covered.Contains(file))
            .ToList();

        invisible.ShouldBeEmpty(UncoveredMessage);
    }

    /// <summary>
    /// The tree's top level is accounted for by the two declared lists, both ways.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ShippedRoots"/> is a literal that bounds the population of every "what does DocuMe
    /// hand over" sweep in the suite, and nothing paired it with the tree it describes. A directory
    /// added at the top level therefore arrived outside both sweeps in silence — no error, no warning,
    /// the same failure mode the <c>sources</c> check above exists to catch one level down.
    /// </para>
    /// <para>
    /// What this cannot do, recorded rather than implied: a determined edit can still move a root from
    /// one list to the other and reword the guide's sentence. The bar it sets is that dropping a root
    /// now costs a written claim that the root is how DocuMe is made, where before it cost nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_top_level_directory_is_declared_shipped_or_not()
    {
        var actual = TopLevelDirectories();

        // Each direction below is the other's vacuity guard — an empty walk fails the second, an empty
        // declaration fails the first. This floor covers the case neither does: both going empty.
        actual.Count.ShouldBeGreaterThan(ShippedRoots.Length);

        const string unclassified =
            "A top-level directory is in neither ShippedRoots nor UnshippedRoots. Both sweeps that ask "
            + "what DocuMe hands over start from ShippedRoots and pass over everything else without a "
            + "word — the `sources` coverage above, and ConsumerKnowledgeCoverageTests' rule §9.5 scan "
            + "— so an unclassified directory is exempt from both without anyone deciding it should be. "
            + "Add it to ShippedRoots if a consumer receives it, or to UnshippedRoots with the reason. "
            + "Unclassified:";

        actual
            .Except(ShippedRoots.Concat(UnshippedRoots), StringComparer.Ordinal)
            .ShouldBeEmpty(unclassified);

        const string vanished =
            "A declared root is not on disk, so the list holding it describes a tree that has moved. A "
            + "shipped root that vanished takes its files out of both sweeps; an unshipped one launders "
            + "an exemption nothing needs any more. Declared but absent:";

        ShippedRoots
            .Concat(UnshippedRoots)
            .Except(actual, StringComparer.Ordinal)
            .ShouldBeEmpty(vanished);

        const string contradictory =
            "A root is declared shipped and unshipped at once, which makes the classification mean "
            + "nothing: the sweeps read ShippedRoots, so the unshipped entry is decoration a reviewer "
            + "would read as an exemption. In both lists:";

        ShippedRoots
            .Intersect(UnshippedRoots, StringComparer.Ordinal)
            .ShouldBeEmpty(contradictory);
    }

    [Fact]
    public void Every_directory_publishes_through_its_own_index_page()
    {
        var tree = Load();
        var paths = tree.Pages.Select(page => page.Path).ToHashSet(StringComparer.Ordinal);

        var missing = paths
            .Select(DirectoryOf)
            .Distinct(StringComparer.Ordinal)
            .Select(IndexOf)
            .Where(index => !paths.Contains(index))
            .ToList();

        // PageHierarchy skips a directory with no index rather than synthesizing one, so its pages
        // hang from whatever index is above it — the published tree stops mirroring the folders and
        // nothing complains (rule §9.1).
        const string message =
            "A directory whose pages have no README.md loses its level in the published tree. Missing:";

        missing.ShouldBeEmpty(message);

        // And exactly one page sits at the tree root, under confluence.rootPageId.
        PageHierarchy.Resolve(paths)
            .Where(entry => entry.Value is null)
            .Select(entry => entry.Key)
            .ShouldBe(["README.md"]);
    }

    private static WikiTree Load() => WikiTree.Load(Path.Combine(RepoRoot, "docs", "wiki"));

    /// <summary>
    /// The repo's immediate subdirectories, trailing-slash spelled to match the declared roots, minus
    /// the ones <see cref="SkippedDirectories"/> names (build output, scratch, vendored packages).
    /// </summary>
    private static List<string> TopLevelDirectories() =>
        new DirectoryInfo(RepoRoot)
            .EnumerateDirectories()
            .Select(directory => directory.Name)
            .Where(name => !SkippedDirectories.Contains(name))
            .Select(name => $"{name}/")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string DirectoryOf(string pagePath)
    {
        var lastSlash = pagePath.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : pagePath[..lastSlash];
    }

    private static string IndexOf(string directory) =>
        directory.Length == 0 ? "README.md" : $"{directory}/README.md";

    /// <summary>
    /// Every file in the repo as a forward-slash path relative to the root — the spelling
    /// <c>sources</c> globs are written against (§6.4).
    /// </summary>
    private static List<string> RepoFiles()
    {
        var files = new List<string>();
        Walk(new DirectoryInfo(RepoRoot), string.Empty, files);
        return files;
    }

    private static void Walk(DirectoryInfo directory, string prefix, List<string> files)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            files.Add(prefix + file.Name);
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            if (!SkippedDirectories.Contains(child.Name))
            {
                Walk(child, $"{prefix}{child.Name}/", files);
            }
        }
    }

    private static string DegradationSummary(AcceptanceReport report) =>
        report.DiagnosticCount == 0
            ? "no degradations"
            : string.Join(
                " // ",
                report.Pages.SelectMany(page => page.Diagnostics.Select(
                    diagnostic => $"{page.Path}: {diagnostic.Code} ({diagnostic.Construct})")));

    private static string FailureSummary(AcceptanceReport report) =>
        report.Failures.Count == 0
            ? "no failures"
            : string.Join(
                " // ",
                report.Failures.Select(group => $"{group.Count}x {group.Occurrences[0].Message}"));

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
