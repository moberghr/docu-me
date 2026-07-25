using DocuMe.Core.Config;
using DocuMe.Core.Scaffolding;
using Shouldly;

namespace DocuMe.Core.Tests.Scaffolding;

public sealed class ProjectScaffolderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docume-scaffold-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Every target of a bare <c>docume init</c> (PLAN.md §6.1), in the order it is reported.
    /// Spelled out rather than derived: this list is what a consumer's repo looks like afterwards,
    /// and a test that computed it from the same source as the code would assert nothing.
    /// </summary>
    private static readonly string[] ExpectedFiles =
    [
        "docume.json",
        "docs/wiki/README.md",
        "docs/wiki/_meta/STYLE.md",
        "docs/wiki/_meta/state.json",
        ".github/workflows/docs-drift-pr.yml",
        ".github/workflows/docs-drift.yml",
        ".github/workflows/docs-feedback.yml",
        ".github/workflows/docs-publish.yml",
        ".github/workflows/docs-refresh.yml",
        ".github/workflows/docs-sync.yml",
        "tools/render-mermaid.mjs",
    ];

    [Fact]
    public void Scaffold_EmptyDirectory_CreatesEverything()
    {
        var results = ProjectScaffolder.Scaffold(_dir);

        results.Select(r => r.RelativePath).ShouldBe(ExpectedFiles);
        results.ShouldAllBe(r => r.Action == ScaffoldAction.Created);

        foreach (var relative in ExpectedFiles)
        {
            File.Exists(Full(relative)).ShouldBeTrue($"expected {relative} to be written");
        }
    }

    [Fact]
    public void Scaffold_SecondRun_SkipsExistingFilesWithoutModifying()
    {
        ProjectScaffolder.Scaffold(_dir);
        var configPath = System.IO.Path.Combine(_dir, "docume.json");
        var firstBytes = File.ReadAllText(configPath);

        var second = ProjectScaffolder.Scaffold(_dir);

        second.ShouldAllBe(r => r.Action == ScaffoldAction.Skipped);
        File.ReadAllText(configPath).ShouldBe(firstBytes); // untouched
    }

    [Fact]
    public void Scaffold_WithFlags_WritesReparseableConfig()
    {
        ProjectScaffolder.Scaffold(_dir, spaceKey: "AUR", baseUrl: "https://kvika.atlassian.net/wiki");

        var config = ConfigLoader.Load(System.IO.Path.Combine(_dir, "docume.json"));

        config.Confluence.SpaceKey.ShouldBe("AUR");
        config.Confluence.BaseUrl.ShouldBe("https://kvika.atlassian.net/wiki");
        config.Wiki.Root.ShouldBe("docs/wiki");
    }

    [Fact]
    public void Scaffold_DefaultConfig_IsValidAndParses()
    {
        ProjectScaffolder.Scaffold(_dir);

        // Placeholder config must still satisfy required-field validation so a
        // fresh repo does not fail to load before the user edits it.
        var config = ConfigLoader.Load(System.IO.Path.Combine(_dir, "docume.json"));

        ConfigLoader.Validate(config).ShouldBeEmpty();
    }

    /// <summary>
    /// The anti-fork assertion. The workflows in <c>templates/workflows/</c> are a tested contract
    /// (<see cref="Templates.WorkflowTemplateTests"/> reads that directory), so what
    /// <c>init</c> ships has to be those exact bytes and not a copy that drifted from them.
    /// </summary>
    [Fact]
    public void Scaffold_ships_the_workflow_templates_byte_for_byte()
    {
        ProjectScaffolder.Scaffold(_dir);

        foreach (var source in Directory.GetFiles(TemplateDirectory("workflows"), "*.yml"))
        {
            var shipped = Full($".github/workflows/{System.IO.Path.GetFileName(source)}");

            File.ReadAllBytes(shipped).ShouldBe(
                File.ReadAllBytes(source),
                $"{System.IO.Path.GetFileName(source)} was not shipped verbatim.");
        }
    }

    [Fact]
    public void Scaffold_ships_the_render_script_byte_for_byte()
    {
        ProjectScaffolder.Scaffold(_dir);

        var source = System.IO.Path.Combine(TemplateDirectory("tools"), "render-mermaid.mjs");

        File.ReadAllBytes(Full("tools/render-mermaid.mjs")).ShouldBe(File.ReadAllBytes(source));
    }

    /// <summary>
    /// A workflow added to <c>templates/workflows/</c> ships without anyone editing the scaffolder
    /// (the embed is a glob) — this pins the other direction, that none is silently left behind.
    /// </summary>
    [Fact]
    public void Scaffold_ships_every_workflow_in_the_tree()
    {
        var results = ProjectScaffolder.Scaffold(_dir);

        const string prefix = ".github/workflows/";
        var shipped = results
            .Select(r => r.RelativePath)
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..])
            .OrderBy(name => name, StringComparer.Ordinal);

        var inTree = Directory
            .GetFiles(TemplateDirectory("workflows"), "*.yml")
            .Select(System.IO.Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal);

        shipped.ShouldBe(inTree);
    }

    /// <summary>
    /// Rule §9.4, on the file where it bites hardest: every workflow template's header says
    /// "EDIT BEFORE USE — <c>branches:</c> must name your default branch", so a re-run that
    /// overwrote them would undo the one edit the template asks its consumer to make.
    /// </summary>
    [Fact]
    public void Scaffold_SecondRun_KeepsAConsumersWorkflowEdit()
    {
        ProjectScaffolder.Scaffold(_dir);
        var edited = Full(".github/workflows/docs-publish.yml");
        var consumerVersion = File.ReadAllText(edited)
            .Replace("branches: [main]", "branches: [trunk]", StringComparison.Ordinal);
        File.WriteAllText(edited, consumerVersion);

        var second = ProjectScaffolder.Scaffold(_dir);

        second
            .Single(r => string.Equals(
                r.RelativePath,
                ".github/workflows/docs-publish.yml",
                StringComparison.Ordinal))
            .Action.ShouldBe(ScaffoldAction.Skipped);
        File.ReadAllText(edited).ShouldBe(consumerVersion);
    }

    /// <summary>
    /// The script has to land where <c>docume publish</c> will look for it, which is whatever
    /// <c>mermaid.renderer</c> names (PLAN.md §5.1) — not the default the scaffolder happens to know.
    /// </summary>
    [Fact]
    public void Scaffold_puts_the_render_script_where_an_existing_config_points()
    {
        WriteConfig("""{"confluence":{"baseUrl":"https://x.atlassian.net/wiki","spaceKey":"SBX"},"mermaid":{"renderer":"scripts/mermaid/render.mjs"}}""");

        var script = RenderScript(ProjectScaffolder.Scaffold(_dir));

        script.RelativePath.ShouldBe("scripts/mermaid/render.mjs");
        script.Note.ShouldBeNull();
        File.Exists(Full("scripts/mermaid/render.mjs")).ShouldBeTrue();
        File.Exists(Full("tools/render-mermaid.mjs")).ShouldBeFalse();
    }

    [Fact]
    public void Scaffold_refuses_a_renderer_path_that_escapes_the_target_directory()
    {
        // Scaffolded one level down, so the escape lands inside this test's own temp directory
        // instead of the shared parent every other test instance also owns. Asserting on a path
        // outside _dir would make this test read leftovers from unrelated runs.
        var repo = System.IO.Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(
            System.IO.Path.Combine(repo, "docume.json"),
            """{"confluence":{"baseUrl":"https://x.atlassian.net/wiki","spaceKey":"SBX"},"mermaid":{"renderer":"../escaped/render.mjs"}}""");

        var script = RenderScript(ProjectScaffolder.Scaffold(repo));

        script.RelativePath.ShouldBe("tools/render-mermaid.mjs");
        script.Note.ShouldNotBeNull().ShouldContain("../escaped/render.mjs");
        File.Exists(System.IO.Path.Combine(repo, "tools", "render-mermaid.mjs")).ShouldBeTrue();
        Directory
            .Exists(System.IO.Path.Combine(_dir, "escaped"))
            .ShouldBeFalse("the scaffolder wrote outside the directory it was given");
    }

    /// <summary>
    /// <c>init</c> is the command a consumer runs to get out of a broken setup, so an unreadable
    /// config cannot make it throw — but it must not fall back in silence either.
    /// </summary>
    [Fact]
    public void Scaffold_notes_an_unreadable_config_instead_of_failing()
    {
        WriteConfig("""{ "confluence": { "baseUrl": "https://x.atlassian.net/wiki" """);

        var script = RenderScript(ProjectScaffolder.Scaffold(_dir));

        script.RelativePath.ShouldBe("tools/render-mermaid.mjs");
        script.Action.ShouldBe(ScaffoldAction.Created);
        script.Note.ShouldNotBeNull().ShouldContain("docume.json could not be read");

        // The note is one line: it is printed under a table, and a raw JSON exception message
        // carries newlines that would break the layout.
        script.Note.ShouldNotContain("\n");
    }

    /// <summary>
    /// The render script's result, found by extension rather than by position: which path it lands
    /// on is the point of three of these tests, so the lookup must not assume one.
    /// </summary>
    private static ScaffoldResult RenderScript(IReadOnlyList<ScaffoldResult> results)
        => results.Single(r => r.RelativePath.EndsWith(".mjs", StringComparison.Ordinal));

    private void WriteConfig(string json)
        => File.WriteAllText(System.IO.Path.Combine(_dir, "docume.json"), json);

    private string Full(string relativePath)
        => System.IO.Path.Combine([_dir, .. relativePath.Split('/')]);

    /// <summary>
    /// The shipped templates are read from the tree, not from a copy beside the test assembly: the
    /// whole point of these assertions is that the reviewed file and the scaffolded one are one file.
    /// </summary>
    private static string TemplateDirectory(string kind)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return System.IO.Path.Combine(directory.FullName, "templates", kind);
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so templates/{kind} cannot be found.");
    }
}
