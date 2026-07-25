using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DocuMe.Core.Config;
using DocuMe.Core.Scaffolding;
using DocuMe.Core.State;
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
        ".config/dotnet-tools.json",
        "tools/render-mermaid.mjs",
        ".gitignore",
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
    /// The failure this whole file exists one layer above: every scaffolded workflow runs
    /// <c>dotnet tool restore</c> before <c>dotnet tool run docume</c>, and restore in a repo with no
    /// manifest fails — so a consumer would <c>init</c>, push, and get a red check on their first
    /// docs job. The entry shape is the one the SDK itself writes, verified against
    /// <c>dotnet tool install DocuMe.Cli --local</c>.
    /// </summary>
    [Fact]
    public void Scaffold_pins_the_tool_the_workflows_restore()
    {
        var result = Manifest(ProjectScaffolder.Scaffold(_dir));

        result.Action.ShouldBe(ScaffoldAction.Created);
        result.Note.ShouldBeNull();

        var manifest = ReadManifest();
        manifest["version"]!.GetValue<int>().ShouldBe(1);
        manifest["isRoot"]!.GetValue<bool>().ShouldBeTrue();

        var tool = manifest["tools"]!["docume.cli"].ShouldNotBeNull();
        tool["commands"]!.AsArray().Select(c => c!.GetValue<string>()).ShouldBe(["docume"]);
        tool["rollForward"]!.GetValue<bool>().ShouldBeFalse();
    }

    /// <summary>
    /// The pinned version has to be the one this build publishes, checked against
    /// <c>Directory.Build.props</c> rather than against the code's own way of finding it — §12 keeps a
    /// single <c>&lt;Version&gt;</c> there for CLI, Core, plugin and action, so that file is the
    /// independent answer. It also catches the one way reading it off the assembly goes wrong: the SDK
    /// stamps <c>InformationalVersion</c> as <c>0.1.0+&lt;commit sha&gt;</c> (SourceLink is on by
    /// default since .NET 8), and NuGet publishes <c>0.1.0</c>, so a pin keeping the metadata restores
    /// nothing.
    /// </summary>
    [Fact]
    public void Scaffold_pins_the_version_this_build_declares()
    {
        ProjectScaffolder.Scaffold(_dir);

        var pinned = ReadManifest()["tools"]!["docume.cli"]!["version"]!.GetValue<string>();

        pinned.ShouldBe(DeclaredVersion());
        pinned.ShouldNotContain("+", Case.Sensitive);

        // And it really is the running assembly's version, not a copy of the props file that drifted.
        var informational = typeof(DocumeState).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        informational.ShouldStartWith(pinned);
    }

    /// <summary>
    /// A manifest is shared ground: the consumer keeps their own tools in it, so this is an add, not
    /// an overwrite. Skipping it instead — the create-or-skip rule every other target follows — would
    /// leave <c>docume</c> unpinned in exactly the repos most likely to already have a manifest.
    /// </summary>
    [Fact]
    public void Scaffold_adds_the_pin_to_an_existing_manifest_and_keeps_its_other_tools()
    {
        WriteManifest("""
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "dotnet-ef": {
                  "version": "9.0.0",
                  "commands": [ "dotnet-ef" ]
                }
              }
            }
            """);

        var result = Manifest(ProjectScaffolder.Scaffold(_dir));

        result.Action.ShouldBe(ScaffoldAction.Updated);
        result.Note.ShouldNotBeNull().ShouldContain("docume.cli");

        var tools = ReadManifest()["tools"]!;
        tools["docume.cli"]!["version"]!.GetValue<string>().ShouldBe(DeclaredVersion());
        tools["dotnet-ef"]!["version"]!.GetValue<string>().ShouldBe("9.0.0");
    }

    /// <summary>
    /// A consumer who deliberately held the tool at an older version did not ask <c>init</c> to undo
    /// that (rule §9.4), but they do have a reason to know the templates they just scaffolded came
    /// from a newer one.
    /// </summary>
    [Fact]
    public void Scaffold_leaves_an_existing_docume_pin_alone_and_says_so()
    {
        WriteManifest("""
            {
              "version": 1,
              "isRoot": true,
              "tools": { "docume.cli": { "version": "0.0.1", "commands": [ "docume" ] } }
            }
            """);

        var result = Manifest(ProjectScaffolder.Scaffold(_dir));

        result.Action.ShouldBe(ScaffoldAction.Skipped);
        result.Note.ShouldNotBeNull().ShouldContain("0.0.1");
        ReadManifest()["tools"]!["docume.cli"]!["version"]!.GetValue<string>().ShouldBe("0.0.1");
    }

    /// <summary>
    /// Same reasoning as the unreadable <c>docume.json</c>: <c>init</c> is the command a consumer runs
    /// to get out of a broken setup, so it cannot throw on one — and cannot fall back in silence.
    /// </summary>
    [Fact]
    public void Scaffold_notes_an_unreadable_manifest_instead_of_failing()
    {
        WriteManifest("""{ "version": 1, "tools": { """);

        var result = Manifest(ProjectScaffolder.Scaffold(_dir));

        result.Action.ShouldBe(ScaffoldAction.Skipped);
        result.Note.ShouldNotBeNull().ShouldContain("could not be read");
        result.Note.ShouldContain("dotnet tool restore");
        result.Note.ShouldNotContain("\n"); // printed under a table; JSON messages carry newlines
    }

    /// <summary>
    /// A pin whose <c>version</c> is not a string. Malformed, but still a file <c>init</c> has to
    /// survive reading — and the note has to say which pin it could not make sense of.
    /// </summary>
    [Fact]
    public void Scaffold_survives_a_pin_with_an_unreadable_version()
    {
        WriteManifest("""
            {
              "version": 1,
              "isRoot": true,
              "tools": { "docume.cli": { "version": 1, "commands": [ "docume" ] } }
            }
            """);

        var result = Manifest(ProjectScaffolder.Scaffold(_dir));

        result.Action.ShouldBe(ScaffoldAction.Skipped);
        result.Note.ShouldNotBeNull().ShouldContain("unreadable version");
    }

    /// <summary>
    /// The comment tolerance DocuMe's own JSON files get is deliberately withheld here: the SDK reads
    /// this file too and rejects comments, and a lenient read followed by a rewrite would delete them.
    /// </summary>
    [Fact]
    public void Scaffold_refuses_to_rewrite_a_manifest_it_cannot_round_trip()
    {
        const string commented = """
            {
              // held back on purpose, see ADR-7
              "version": 1,
              "isRoot": true,
              "tools": {}
            }
            """;
        WriteManifest(commented);

        Manifest(ProjectScaffolder.Scaffold(_dir)).Action.ShouldBe(ScaffoldAction.Skipped);
        File.ReadAllText(Full(".config/dotnet-tools.json")).ShouldBe(commented);
    }

    [Fact]
    public void Scaffold_creates_a_gitignore_when_the_repo_has_none()
    {
        var result = Gitignore(ProjectScaffolder.Scaffold(_dir));

        result.Action.ShouldBe(ScaffoldAction.Created);
        var lines = File.ReadAllLines(Full(".gitignore"));
        lines.ShouldContain("node_modules/");
        lines.ShouldContain(line => line.StartsWith('#'), "the entry should say why it is there");
    }

    /// <summary>
    /// The one target that is not create-or-skip on the consumer side either: a real repo already has
    /// a <c>.gitignore</c> full of its own rules, and skipping would leave the render script's
    /// <c>node_modules</c> tree committable.
    /// </summary>
    [Fact]
    public void Scaffold_appends_to_an_existing_gitignore_without_touching_its_rules()
    {
        const string theirs = "bin/\nobj/\n";
        File.WriteAllText(Full(".gitignore"), theirs);

        var result = Gitignore(ProjectScaffolder.Scaffold(_dir));

        result.Action.ShouldBe(ScaffoldAction.Updated);
        result.Note.ShouldNotBeNull().ShouldContain("node_modules/");

        File.ReadAllText(Full(".gitignore")).ShouldStartWith(theirs);
        AppendedBlock(File.ReadAllLines(Full(".gitignore")), after: 2);
    }

    /// <summary>
    /// A last rule with no line terminator after it. Appending straight onto that would glue the
    /// comment onto the end of their rule and silently change what it matches, so the terminator has
    /// to be supplied before the block — and the blank line separating the sections still has to be
    /// there, which is what tells this case apart from the terminated one.
    /// </summary>
    [Fact]
    public void Scaffold_terminates_an_unterminated_gitignore_before_appending()
    {
        File.WriteAllText(Full(".gitignore"), "*.user");

        Gitignore(ProjectScaffolder.Scaffold(_dir)).Action.ShouldBe(ScaffoldAction.Updated);

        var lines = File.ReadAllLines(Full(".gitignore"));
        lines[0].ShouldBe("*.user");
        AppendedBlock(lines, after: 1);
    }

    /// <summary>
    /// The three lines DocuMe appends, starting at <paramref name="after"/>: a blank separator, the
    /// comment saying why, then the entry. Asserted positionally because the separator is the part a
    /// mistake loses silently — the entry itself is still present either way.
    /// </summary>
    private static void AppendedBlock(string[] lines, int after)
    {
        lines.Length.ShouldBe(after + 3);
        lines[after].ShouldBeEmpty("the appended section should be set off by a blank line");
        lines[after + 1].ShouldStartWith("#");
        lines[after + 2].ShouldBe("node_modules/");
    }

    /// <summary>
    /// Every spelling that already ignores the same tree. Appending a seventh redundant line on each
    /// <c>init</c> is the failure this guards, and it only shows up in repos that wrote it their way.
    /// </summary>
    [Theory]
    [InlineData("node_modules")]
    [InlineData("node_modules/")]
    [InlineData("/node_modules")]
    [InlineData("/node_modules/")]
    [InlineData("**/node_modules")]
    [InlineData("**/node_modules/")]
    public void Scaffold_leaves_a_gitignore_that_already_covers_node_modules(string spelling)
    {
        var theirs = $"bin/\n  {spelling}  \nobj/\n";
        File.WriteAllText(Full(".gitignore"), theirs);

        Gitignore(ProjectScaffolder.Scaffold(_dir)).Action.ShouldBe(ScaffoldAction.Skipped);
        File.ReadAllText(Full(".gitignore")).ShouldBe(theirs);
    }

    /// <summary>
    /// The render script's result, found by extension rather than by position: which path it lands
    /// on is the point of three of these tests, so the lookup must not assume one.
    /// </summary>
    private static ScaffoldResult RenderScript(IReadOnlyList<ScaffoldResult> results)
        => results.Single(r => r.RelativePath.EndsWith(".mjs", StringComparison.Ordinal));

    private static ScaffoldResult Manifest(IReadOnlyList<ScaffoldResult> results)
        => results.Single(r => string.Equals(
            r.RelativePath,
            ".config/dotnet-tools.json",
            StringComparison.Ordinal));

    private static ScaffoldResult Gitignore(IReadOnlyList<ScaffoldResult> results)
        => results.Single(r => string.Equals(r.RelativePath, ".gitignore", StringComparison.Ordinal));

    private void WriteManifest(string json)
    {
        var path = Full(".config/dotnet-tools.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private JsonObject ReadManifest()
        => JsonNode.Parse(File.ReadAllText(Full(".config/dotnet-tools.json")))!.AsObject();

    /// <summary>
    /// The single version of §12, read from the tree rather than from the code under test.
    /// </summary>
    private static string DeclaredVersion()
    {
        var props = System.IO.Path.Combine(RepoRoot(), "Directory.Build.props");
        var element = XDocument
            .Load(props)
            .Descendants("Version")
            .SingleOrDefault()
            ?? throw new InvalidOperationException($"No single <Version> element in {props}.");

        return element.Value;
    }

    private void WriteConfig(string json)
        => File.WriteAllText(System.IO.Path.Combine(_dir, "docume.json"), json);

    private string Full(string relativePath)
        => System.IO.Path.Combine([_dir, .. relativePath.Split('/')]);

    /// <summary>
    /// The shipped templates are read from the tree, not from a copy beside the test assembly: the
    /// whole point of these assertions is that the reviewed file and the scaffolded one are one file.
    /// </summary>
    private static string TemplateDirectory(string kind)
        => System.IO.Path.Combine(RepoRoot(), "templates", kind);

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the repo root cannot be found.");
    }
}
