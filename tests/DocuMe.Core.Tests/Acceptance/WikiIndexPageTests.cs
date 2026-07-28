using System.Text.RegularExpressions;
using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// The four index pages of DocuMe's own wiki (<c>docs/wiki/README.md</c> and one per section) held to
/// the sets they claim to enumerate.
/// </summary>
/// <remarks>
/// <para>
/// An index page is almost entirely counted-set claims: these are the sections, these are the pages,
/// these are the things you must edit. Every one of them mirrors something on disk, so every one can be
/// compared rather than read. That is what this class does, and it is the check the pages themselves
/// could not survive without: the automation index spent its whole life telling readers that a workflow
/// involves "no model, no judgement" while two of the six templates installed Claude Code and ran a
/// skill, and the page it links to said so on the same day.
/// </para>
/// <para>
/// Both directions matter. A claim naming fewer items than exist is the failure that actually happened
/// here twice, and it is invisible to anyone reading only the page. A claim naming more is a link to
/// something deleted. So the assertions are set-equality against a reflected or globbed set, never
/// "contains".
/// </para>
/// </remarks>
public sealed class WikiIndexPageTests
{
    private const string AutomationIndex = "docs/wiki/30-automation/README.md";

    private const string RootIndex = "docs/wiki/README.md";

    /// <summary>
    /// The section directories a reader can reach, and the population every per-section fact below
    /// iterates. Paired with the wiki itself by
    /// <see cref="Every_section_the_wiki_ships_is_one_this_class_checks"/>, so <c>_meta</c> is absent
    /// because <c>wiki.exclude</c> keeps it out of the tree that pairing reads — not because this
    /// sentence says it should be.
    /// </summary>
    private static readonly string[] SectionDirectories = ["10-concepts", "20-reference", "30-automation"];

    /// <summary>
    /// Each thing a scaffolded workflow cannot guess, as the pattern that finds it in the templates and
    /// the pattern that finds it named on the automation index. Both sides are asserted, so adding an
    /// <c>EDIT BEFORE USE</c> note to a template without listing it here fails, and listing one the
    /// templates stopped asking for fails too.
    /// </summary>
    private static readonly (string Topic, string InTemplates, string OnIndex)[] EditTopics =
    [
        ("default branch", "your default branch", "your default branch"),
        ("wiki root", "`paths:` your wiki root", "your wiki root"),
        ("deploy workflow name", "your deploy workflow", "the name of your deploy workflow"),
        ("packages token", "EDIT BEFORE USE only in two cases", "packages token"),
        ("model api key", "ANTHROPIC_API_KEY must exist as a repository secret", "`ANTHROPIC_API_KEY`"),
        ("plugin ref", "pin `ref:` to the DocuMe release", "pin the plugin to"),
    ];

    private static readonly string[] NumberWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    [Fact]
    public void The_automation_index_does_not_claim_the_workflows_are_model_free()
    {
        var page = Read(AutomationIndex);
        var modelDriven = ModelDrivenWorkflows();

        // Vacuous-pass guard: a scan that found no template would make every assertion below trivial.
        WorkflowTemplates().Count.ShouldBeGreaterThanOrEqualTo(6);
        modelDriven.ShouldNotBeEmpty("No template invokes a model, so this whole class is checking nothing.");

        const string message =
            "docs/wiki/30-automation/README.md described the workflow half as model-free while "
            + "templates/workflows/ shipped workflows that install Claude Code and run a skill. It is the "
            + "first thing a reader of this section sees, and it is the sentence that tells them whether "
            + "a nightly job can rewrite their wiki. Model-driven templates:";

        var named = string.Join(", ", modelDriven);
        page.ShouldNotContain("Deterministic, no model", Case.Insensitive, $"{message} {named}");
        page.ShouldNotContain("never writes any", Case.Insensitive, $"{message} {named}");
    }

    [Fact]
    public void The_automation_index_names_every_model_driven_workflow()
    {
        var page = Read(AutomationIndex);
        var modelDriven = ModelDrivenWorkflows();

        var unnamed = modelDriven
            .Where(name => !page.Contains(name, StringComparison.Ordinal))
            .ToList();

        const string message =
            "A workflow that invokes a model needs ANTHROPIC_API_KEY and produces model-written content, "
            + "and the automation index is where a reader learns which ones those are. Not named:";

        unnamed.ShouldBeEmpty(message);
    }

    [Fact]
    public void The_automation_index_counts_the_cli_only_workflows_correctly()
    {
        var templates = WorkflowTemplates();
        var cliOnly = templates.Count - ModelDrivenWorkflows().Count;

        // Spelled the way the page spells it, so the check fails on the number rather than on prose.
        var claim = $"{NumberWords[cliOnly]} of the {NumberWords[templates.Count]}";

        const string message =
            "The automation index counts the workflows that run the CLI and nothing else. Adding a "
            + "template, or making an existing one invoke a skill, changes that count. Expected the page "
            + "to say:";

        Read(AutomationIndex).ShouldContain(claim, Case.Insensitive, $"{message} '{claim}'");
    }

    [Fact]
    public void The_automation_index_lists_every_thing_a_consumer_must_edit()
    {
        var templates = string.Join('\n', WorkflowTemplates().Select(File.ReadAllText));

        // Scoped to the paragraph that enumerates the edits, not the whole page. `ANTHROPIC_API_KEY`
        // is named twice here — once for what the model-driven workflows need, once in this list — and
        // a whole-page search let the list drop it while the check stayed green.
        var page = Paragraph(AutomationIndex, "EDIT BEFORE USE");

        var asked = EditTopics
            .Where(topic => templates.Contains(topic.InTemplates, StringComparison.Ordinal))
            .ToList();

        // Vacuous-pass guard: a table that matched nothing in the templates would assert nothing.
        var stale = string.Join(", ", EditTopics.Except(asked).Select(topic => topic.Topic));
        var staleMessage =
            $"EditTopics names an EDIT BEFORE USE note no template carries any more. Stale rows: {stale}";

        asked.Count.ShouldBe(EditTopics.Length, staleMessage);

        var missing = asked
            .Where(topic => !page.Contains(topic.OnIndex, StringComparison.Ordinal))
            .Select(topic => $"{topic.Topic} (\"{topic.OnIndex}\")")
            .ToList();

        const string message =
            "`docume init` drops these templates into a consumer's repo and each EDIT BEFORE USE note is "
            + "something the scaffolder could not guess. The index page listed two of six once, and the "
            + "four it dropped included the API key the model-driven workflows need. Unlisted:";

        missing.ShouldBeEmpty(message);
    }

    /// <summary>
    /// Every section the wiki ships is one this class checks — the assertion a fourth section
    /// directory fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SectionDirectories"/> is not a description of the wiki, it is the enforcement
    /// boundary: <see cref="Every_index_links_every_page_beside_it"/> iterates it to decide which index
    /// pages to read at all, so a section outside it has no index read and its pages are linked from
    /// nowhere with nothing to say so.
    /// </para>
    /// <para>
    /// Measured before this fact existed, and the shape inverts the usual one. A fourth section linked
    /// from nowhere passed the entire suite. The same section linked properly — from the root index,
    /// with its own index linking its page — went <em>red</em>, because
    /// <see cref="The_root_index_links_every_section"/> compares what that page links against this
    /// list. The net punished the careful edit and ignored the negligent one.
    /// </para>
    /// <para>
    /// The set is derived through <see cref="WikiTree"/> rather than from the directories on disk, so
    /// what counts as a section is the tool's own answer to what publishes rather than a second opinion
    /// that can drift from it. That is also what keeps <c>_meta</c> out: <c>wiki.exclude</c> drops it,
    /// so no filter here has to.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_section_the_wiki_ships_is_one_this_class_checks()
    {
        var sections = WikiTree.Load(Path.Combine(RepoRoot, "docs", "wiki"))
            .Pages
            .Select(page => page.Path.Split('/'))
            .Where(segments => segments.Length > 1)
            .Select(segments => segments[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Vacuous-pass guard: a tree holding no page under any directory would compare two empty sets
        // and report every section covered.
        sections.ShouldNotBeEmpty(
            "No page loaded from a section directory, so this comparison is not reading the wiki.");

        const string message =
            "The wiki's sections and the list this class checks have come apart. Every per-section fact "
            + "here iterates SectionDirectories, so a section missing from it gets no index read and "
            + "its pages go unlinked with nothing to notice; a section listed here that the wiki no "
            + "longer has leaves the root index pointing at nothing. Add or drop the one line, and link "
            + "the section from docs/wiki/README.md in the same change.";

        sections.ShouldBe(SectionDirectories.Order(StringComparer.Ordinal).ToList(), message);
    }

    [Fact]
    public void Every_index_links_every_page_beside_it()
    {
        var tree = WikiTree.Load(Path.Combine(RepoRoot, "docs", "wiki"));
        var pages = tree.Pages.Select(page => page.Path).ToList();

        pages.Count.ShouldBeGreaterThanOrEqualTo(6);

        var unlinked = new List<string>();

        foreach (var directory in SectionDirectories)
        {
            var index = Read($"docs/wiki/{directory}/README.md");
            var siblings = pages
                .Where(path => path.StartsWith($"{directory}/", StringComparison.Ordinal))
                .Where(path => !path.EndsWith("/README.md", StringComparison.Ordinal))
                .Select(path => path[(directory.Length + 1)..]);

            unlinked.AddRange(siblings
                .Where(name => !index.Contains($"({name})", StringComparison.Ordinal))
                .Select(name => $"{directory}/README.md is missing a link to {name}"));
        }

        const string message =
            "A page no index links is a page a reader reaches only by guessing the URL, and in Confluence "
            + "it still hangs in the tree, so nothing looks wrong. Unlinked:";

        unlinked.ShouldBeEmpty(message);
    }

    [Fact]
    public void The_root_index_links_every_section()
    {
        var root = Read(RootIndex);

        var missing = SectionDirectories
            .Where(directory => !root.Contains($"({directory}/README.md)", StringComparison.Ordinal))
            .ToList();

        const string message =
            "The root index's table is how a reader finds a section at all: it is the home page in "
            + "Confluence. Sections it does not link:";

        missing.ShouldBeEmpty(message);

        // And nothing beyond those, so a deleted section cannot leave a dangling row.
        var linked = Regex.Matches(
                root,
                @"\((?<section>\d\d-[a-z-]+)/README\.md\)",
                RegexOptions.ExplicitCapture,
                TimeSpan.FromSeconds(1))
            .Select(match => match.Groups["section"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        linked.ShouldBe(SectionDirectories.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void The_root_index_does_not_duplicate_the_install_the_repository_readme_owns()
    {
        var root = Read(RootIndex);

        const string message =
            "This page says in its own words that duplicating the install is how a wiki starts lying, and "
            + "then carried `dotnet tool install --local DocuMe.Cli` — which skips the GitHub Packages "
            + "feed the repository README says is required, names the wrong scope, and omits that nothing "
            + "is published yet. The install story lives in README.md; link to it, do not restate it.";

        root.ShouldNotContain("dotnet tool install", Case.Insensitive, message);
        root.ShouldNotContain("dotnet nuget add source", Case.Insensitive, message);
    }

    [Fact]
    public void The_reference_index_declares_every_source_root_its_pages_derive_from()
    {
        var tree = WikiTree.Load(Path.Combine(RepoRoot, "docs", "wiki"));

        var index = tree.Pages.Single(page => string.Equals(page.Path, "20-reference/README.md", StringComparison.Ordinal));
        var declared = index.Parsed.Frontmatter.Sources;

        var childRoots = tree.Pages
            .Where(page => page.Path.StartsWith("20-reference/", StringComparison.Ordinal))
            .Where(page => !string.Equals(page.Path, index.Path, StringComparison.Ordinal))
            .SelectMany(page => page.Parsed.Frontmatter.Sources)
            .Select(SourceRoot)
            .Where(root => root.StartsWith("src/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Vacuous-pass guard: children with no src globs would make the loop below assert nothing.
        childRoots.Count.ShouldBeGreaterThanOrEqualTo(2);

        // Root-to-root, and the declared side is the prefix: `src/DocuMe.Core/**` covers a child's
        // `src/DocuMe.Core/Config/*.cs`, while a narrower declaration than the child's does not.
        var declaredRoots = declared.Select(SourceRoot).ToList();

        var undeclared = childRoots
            .Where(root => !declaredRoots.Any(prefix => root.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        const string message =
            "The reference index tells readers which code its three pages track, and `sources` is the "
            + "machine-readable half of the same claim — it is what `docume drift` reads. When the prose "
            + "named src/DocuMe.Core and the frontmatter did not, a Core change never marked this page "
            + "stale. Source roots the children derive from but this index does not declare:";

        undeclared.ShouldBeEmpty(message);
    }

    [Fact]
    public void The_concepts_index_counts_the_lifecycle_stages_the_lifecycle_page_documents()
    {
        var stages = Regex.Count(
            Read("docs/wiki/10-concepts/lifecycle.md"),
            @"^## \d+\. ",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(1));

        stages.ShouldBeGreaterThan(0, "lifecycle.md has no numbered stage headings, so the count below is vacuous.");

        const string message =
            "The concepts index promises a stage count and the lifecycle page is where a reader counts "
            + "them. Expected the index to say:";

        var claim = $"{NumberWords[stages]} stages";
        Read("docs/wiki/10-concepts/README.md").ShouldContain(claim, Case.Insensitive, $"{message} '{claim}'");
    }

    /// <summary>
    /// The templates that install Claude Code or invoke it headlessly: the model-driven half, derived
    /// from the shipped files rather than from a list someone remembers to update.
    /// </summary>
    private static List<string> ModelDrivenWorkflows() =>
        WorkflowTemplates()
            .Where(path =>
            {
                var body = File.ReadAllText(path);
                return body.Contains("@anthropic-ai/claude-code", StringComparison.Ordinal)
                    || body.Contains("claude -p ", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> WorkflowTemplates() =>
        Directory.GetFiles(Path.Combine(RepoRoot, "templates", "workflows"), "*.yml")
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The fixed prefix of a glob, up to its first wildcard: <c>src/DocuMe.Core/Config/*.cs</c> → <c>src/DocuMe.Core/</c>.</summary>
    private static string SourceRoot(string glob)
    {
        var wildcard = glob.IndexOf('*', StringComparison.Ordinal);
        var head = wildcard < 0 ? glob : glob[..wildcard];
        var lastSlash = head.LastIndexOf('/');
        return lastSlash < 0 ? head : head[..(lastSlash + 1)];
    }

    /// <summary>
    /// The one blank-line-delimited paragraph containing <paramref name="marker"/>. These index pages
    /// carry no <c>##</c> headings to scope by, and an unscoped search is satisfied by any sentence on
    /// the page — which is how a list check passes over the list it was written to hold.
    /// </summary>
    private static string Paragraph(string relativePath, string marker)
    {
        var paragraphs = Read(relativePath)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(block => block.Contains(marker, StringComparison.Ordinal))
            .ToList();

        paragraphs.Count.ShouldBe(1, $"{relativePath} should have exactly one paragraph naming '{marker}'.");

        return paragraphs[0];
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. relativePath.Split('/')]));

    private static string RepoRoot { get; } = Locate();

    /// <summary>Walks up to the directory holding <c>DocuMe.slnx</c>: the wiki ships in the tree.</summary>
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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the wiki index pages cannot be found.");
    }
}
