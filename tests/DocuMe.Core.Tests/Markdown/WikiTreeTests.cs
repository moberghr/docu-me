using DocuMe.Core.Config;
using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// The whole-tree link map (PLAN.md §6.2 steps 1-2): the walk, title resolution and
/// validation, attachment naming, and the per-page resolvers the converter consumes.
/// </summary>
public sealed class WikiTreeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docume-wikitree-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_resolves_titles_from_frontmatter_override_and_first_h1()
    {
        var tree = BuildTree();

        tree.TitleFor("README.md").ShouldBe("Home");
        tree.TitleFor("domains/loans/README.md").ShouldBe("Loans Domain");
        tree.TitleFor("domains/architecture/overview.md").ShouldBe("Architecture & Design");
    }

    [Fact]
    public void Load_orders_pages_by_path_so_a_publish_run_is_machine_independent()
    {
        var tree = BuildTree();

        tree.Pages.Select(page => page.Path).ShouldBe(
        [
            "README.md",
            "domains/architecture/overview.md",
            "domains/loans/README.md",
        ]);
    }

    [Fact]
    public void Load_separates_assets_from_pages()
    {
        var tree = BuildTree();

        tree.Assets.ShouldBe(["domains/loans/local.png", "images/architecture.png", "images/sub/deep.png"]);
        tree.Pages.ShouldAllBe(page => page.Path.EndsWith(".md", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_applies_wiki_exclude_globs()
    {
        var tree = BuildTree();

        // _meta/** is the default exclude (§5.1): neither its pages nor its assets are in scope.
        tree.TitleFor("_meta/STYLE.md").ShouldBeNull();
        tree.Pages.ShouldNotContain(page => page.Path.StartsWith("_meta/", StringComparison.Ordinal));
        tree.Assets.ShouldNotContain("_meta/logo.png");
    }

    [Fact]
    public void Load_leaves_a_dot_directory_out_of_scope_so_tooling_metadata_is_never_content()
    {
        Write(".claude/notes.md", "# Agent Notes\n");
        Write(".claude/analytics.json", "{}");
        Write(".vscode/settings.json", "{}");
        Write(".editorconfig", "root = true\n");

        var tree = BuildTree();

        tree.TitleFor(".claude/notes.md").ShouldBeNull();
        tree.Pages.ShouldNotContain(page => page.Path.StartsWith('.'));
        tree.Assets.ShouldNotContain(".claude/analytics.json");
        tree.Assets.ShouldNotContain(".vscode/settings.json");
        tree.Assets.ShouldNotContain(".editorconfig");
    }

    [Fact]
    public void Load_survives_an_untitled_markdown_file_in_a_dot_directory_instead_of_failing_the_whole_tree()
    {
        // A repo whose wiki.root is its own root carries .github/PULL_REQUEST_TEMPLATE.md, which has
        // no title by design. In scope it is a page with no title, and one of those fails Load for
        // EVERY page (the errors list is whole-tree), so a file nobody wrote as content would stop
        // the entire publish.
        Write(".github/PULL_REQUEST_TEMPLATE.md", "- [ ] tests\n");
        Write(".github/ISSUE_TEMPLATE/bug.md", "---\nname: Bug\n---\n");

        var tree = BuildTree();

        tree.Pages.Select(page => page.Path).ShouldContain("README.md");
    }

    [Fact]
    public void Load_re_includes_a_dot_path_named_in_extra_pages()
    {
        // The structural exclusion is not a wall: extraPages is how a consumer publishes one
        // deliberately, exactly as it re-includes an excluded _meta page.
        Write(".well-known/policy.md", "# Ignored\n");
        var config = new WikiConfig
        {
            ExtraPages = [new ExtraPage { Path = ".well-known/policy.md", Title = "Docs Policy" }],
        };

        var tree = BuildTree(config);

        tree.TitleFor(".well-known/policy.md").ShouldBe("Docs Policy");
    }

    [Fact]
    public void Load_re_includes_an_extra_page_under_its_configured_title()
    {
        var config = new WikiConfig
        {
            ExtraPages = [new ExtraPage { Path = "_meta/GAPS.md", Title = "Open Questions for the Team" }],
        };

        var tree = BuildTree(config);

        // The config title wins over the file's own H1 ("Gaps") — renaming a page for
        // publication is exactly why extraPages exists.
        tree.TitleFor("_meta/GAPS.md").ShouldBe("Open Questions for the Team");
        tree.TitleFor("_meta/STYLE.md").ShouldBeNull();
    }

    [Fact]
    public void Load_fails_when_an_extra_page_path_does_not_exist()
    {
        var config = new WikiConfig
        {
            ExtraPages = [new ExtraPage { Path = "_meta/NOPE.md", Title = "Missing" }],
        };

        var ex = Should.Throw<WikiTreeException>(() => BuildTree(config));

        ex.Errors.ShouldHaveSingleItem().ShouldContain("_meta/NOPE.md");
    }

    [Fact]
    public void Load_fails_when_a_page_has_neither_frontmatter_title_nor_h1()
    {
        Write("orphan.md", "Just a paragraph, no heading.\n");

        var ex = Should.Throw<WikiTreeException>(() => BuildTree());

        ex.Errors.ShouldHaveSingleItem().ShouldContain("'orphan.md' has no title");
    }

    [Fact]
    public void Load_fails_listing_every_page_that_claims_a_duplicate_title()
    {
        // Case-only differences count as duplicates: Confluence is not reliably
        // case-sensitive about titles, so over-reporting is the safe direction.
        Write("copy.md", "# loans domain\n");

        var ex = Should.Throw<WikiTreeException>(() => BuildTree());

        var error = ex.Errors.ShouldHaveSingleItem();
        error.ShouldContain("'copy.md'");
        error.ShouldContain("'domains/loans/README.md'");
        error.ShouldContain("unique");
    }

    [Fact]
    public void Load_reports_every_problem_in_one_pass()
    {
        Write("orphan.md", "no title here\n");
        Write("copy.md", "# Loans Domain\n");

        var ex = Should.Throw<WikiTreeException>(() => BuildTree());

        // A 79-page adoption run wants the whole list, not one error per run.
        ex.Errors.Count.ShouldBe(2);
        ex.Message.ShouldContain(_dir);
    }

    [Fact]
    public void Load_fails_when_two_assets_flatten_to_one_attachment_filename()
    {
        // The one residual ambiguity of flattening '/' to '_'.
        Write("a_b/c.png", "x");
        Write("a/b_c.png", "x");

        var ex = Should.Throw<WikiTreeException>(() => BuildTree());

        ex.Errors.ShouldHaveSingleItem().ShouldContain("a_b_c.png");
    }

    [Fact]
    public void Load_fails_when_the_wiki_root_does_not_exist()
    {
        Should.Throw<DirectoryNotFoundException>(() => WikiTree.Load(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void Attachment_name_flattens_the_whole_path_so_it_never_depends_on_the_rest_of_the_tree()
    {
        WikiTree.FlattenToAttachmentName("images/sub/deep.png").ShouldBe("images_sub_deep.png");
        WikiTree.FlattenToAttachmentName("logo.png").ShouldBe("logo.png");

        // The property that matters for §8: the name is a function of the path alone, so a
        // page's body cannot change because an unrelated file appeared somewhere else.
        var before = BuildTree().AttachmentNameFor("images/architecture.png");
        Write("elsewhere/architecture.png", "x");
        BuildTree().AttachmentNameFor("images/architecture.png").ShouldBe(before);
    }

    [Fact]
    public void Resolvers_resolve_links_relative_to_the_linking_page()
    {
        var tree = BuildTree();
        var fromLoans = tree.ResolversFor("domains/loans/README.md").Link;
        var fromRoot = tree.ResolversFor("README.md").Link;

        fromLoans("../architecture/overview.md").ShouldBe("Architecture & Design");
        fromLoans("./README.md").ShouldBe("Loans Domain");
        fromLoans("../../README.md").ShouldBe("Home");
        fromRoot("domains/loans/README.md").ShouldBe("Loans Domain");
    }

    [Fact]
    public void Resolvers_decode_percent_escapes_in_a_link()
    {
        Write("my page.md", "# My Page\n");
        var tree = BuildTree();

        tree.ResolversFor("README.md").Link("my%20page.md").ShouldBe("My Page");
    }

    [Fact]
    public void Resolvers_return_null_for_links_the_tree_cannot_name()
    {
        var tree = BuildTree();
        var link = tree.ResolversFor("domains/loans/README.md").Link;

        // Each of these makes the converter fail loud rather than publish a broken link.
        link("../../../outside.md").ShouldBeNull();          // climbs above the wiki root
        link("/docs/wiki/README.md").ShouldBeNull();         // repo-absolute: needs the repo root
        link("../../_meta/STYLE.md").ShouldBeNull();         // excluded, so it has no page
        link("missing.md").ShouldBeNull();                   // no such file
        link("../loans/").ShouldBeNull();                    // a directory is not a page
    }

    [Fact]
    public void Resolvers_resolve_images_relative_to_the_linking_page()
    {
        var tree = BuildTree();
        var fromLoans = tree.ResolversFor("domains/loans/README.md").Attachment;

        fromLoans("../../images/sub/deep.png").ShouldBe("images_sub_deep.png");
        fromLoans("local.png").ShouldBe("domains_loans_local.png");
        fromLoans("../../images/missing.png").ShouldBeNull();
    }

    [Fact]
    public void Resolvers_name_a_shared_asset_identically_from_every_page_that_uses_it()
    {
        var tree = BuildTree();

        // Same file, same attachment name, so two pages referencing one diagram agree —
        // and neither page's hash moves when the other one changes.
        tree.ResolversFor("README.md").Attachment("images/architecture.png")
            .ShouldBe(tree.ResolversFor("domains/loans/README.md").Attachment("../../images/architecture.png"));
    }

    [Fact]
    public void Diagram_resolver_names_the_attachment_from_the_diagram_source()
    {
        const string Source = "graph TD\nA --> B";
        var tree = BuildTree();

        tree.ResolversFor("README.md").Diagram(Source).ShouldBe(MermaidAttachmentName.ForSource(Source));

        // It never returns null, so a mermaid page always converts and a render failure
        // surfaces at publish (MermaidRenderer) rather than at convert time.
        WikiTree.DiagramResolver("flowchart LR\n  a --> b").ShouldNotBeNull();
    }

    [Fact]
    public void ResolversFor_rejects_a_path_that_is_not_a_page_of_this_tree()
    {
        var tree = BuildTree();

        Should.Throw<ArgumentException>(() => tree.ResolversFor("domains/loans/nope.md"));
    }

    [Fact]
    public void Converter_publishes_a_page_link_and_an_attachment_through_the_tree()
    {
        var tree = BuildTree();
        var page = tree.Pages.Single(p => string.Equals(p.Path, "domains/loans/README.md", StringComparison.Ordinal));
        var resolvers = tree.ResolversFor(page.Path);

        var storage = ConfluenceStorageConverter.Convert(
            page.Parsed.Body,
            resolvers.Link,
            resolvers.Attachment,
            resolvers.Diagram);

        // The seam end to end: real tree, real converter, no test doubles.
        storage.ShouldContain("<ac:link><ri:page ri:content-title=\"Architecture &amp; Design\"/>");
        storage.ShouldContain("<ri:attachment ri:filename=\"images_sub_deep.png\"/>");
    }

    [Fact]
    public void Converter_fails_loud_on_a_link_the_tree_cannot_resolve()
    {
        Write("broken.md", "# Broken\n\nSee [gone](nowhere.md).\n");
        var tree = BuildTree();
        var resolvers = tree.ResolversFor("broken.md");

        var ex = Should.Throw<NotSupportedException>(() => ConfluenceStorageConverter.Convert(
            tree.Pages.Single(p => string.Equals(p.Path, "broken.md", StringComparison.Ordinal)).Parsed.Body,
            resolvers.Link,
            resolvers.Attachment,
            resolvers.Diagram));

        ex.Message.ShouldContain("nowhere.md");
    }

    private WikiTree BuildTree(WikiConfig? config = null)
    {
        Write("README.md", "# Home\n\n![arch](images/architecture.png)\n");
        Write(
            "domains/loans/README.md",
            "---\ntitle: Loans Domain\n---\n# Loans\n\nSee [architecture](../architecture/overview.md) and ![deep](../../images/sub/deep.png).\n");
        Write("domains/architecture/overview.md", "# Architecture & Design\n");
        Write("_meta/STYLE.md", "# Style\n");
        Write("_meta/GAPS.md", "# Gaps\n");
        Write("_meta/logo.png", "x");
        Write("images/architecture.png", "x");
        Write("images/sub/deep.png", "x");
        Write("domains/loans/local.png", "x");

        return WikiTree.Load(_dir, config);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
