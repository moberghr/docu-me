using System.Reflection;
using System.Security.Cryptography;
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

    /// <summary>
    /// Rule §9.4's "never overwrite" half, over every target instead of a sample of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "report skips" half is the <c>ShouldAllBe</c> below, derived from the actual results
    /// list, so a fourteenth target is covered by it for free. The other half is what needs the
    /// consumer edits: a scaffolder generates the same bytes from the same inputs, so an untouched
    /// second run is byte-identical to an untouched first one <em>whether or not</em> it rewrote
    /// what it found. Only an edit that was not there when the templates were written can tell the
    /// two apart.
    /// </para>
    /// <para>
    /// Measured, which is why this is no longer a spot check: with only <c>docume.json</c> compared
    /// and one workflow edited, a <c>StateTarget</c> that re-saved an empty state on every run
    /// passed the whole suite. <c>_meta/state.json</c> is the one target <c>init</c> writes
    /// <em>empty</em> and that carries every page id, hash and approval from the first publish
    /// onward — simultaneously the least visible to a byte comparison and the most costly to lose.
    /// </para>
    /// </remarks>
    [Fact]
    public void Scaffold_SecondRun_SkipsExistingFilesWithoutModifying()
    {
        var first = ProjectScaffolder.Scaffold(_dir);
        var pristine = Snapshot();
        pristine.Count.ShouldBe(first.Count);

        foreach (var result in first)
        {
            EditAsAConsumer(result.RelativePath);
        }

        var edited = Snapshot();
        edited.Keys.ShouldBe(pristine.Keys);

        // Vacuous-pass guard: a target EditAsAConsumer left alone would be compared against bytes
        // the scaffolder itself produced, which is exactly the weakness this test used to have.
        foreach (var path in pristine.Keys)
        {
            var unedited = $"{path} was not changed, so comparing it after a second run proves nothing.";
            edited[path].ShouldNotBe(pristine[path], unedited);
        }

        var second = ProjectScaffolder.Scaffold(_dir);

        second.Select(r => r.RelativePath).ShouldBe(first.Select(r => r.RelativePath));
        second.ShouldAllBe(r => r.Action == ScaffoldAction.Skipped);

        const string because = "A second scaffold changed a file the consumer had edited. Rule §9.4: "
            + "init never overwrites what is already there, it reports the skip.";

        Snapshot().ShouldBe(edited, because);
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
    [Theory]
    [InlineData(AgentRail.Claude)]
    [InlineData(AgentRail.Copilot)]
    public void Scaffold_ships_the_workflow_templates_byte_for_byte(AgentRail rail)
    {
        var target = System.IO.Path.Combine(_dir, rail.ToString());
        Directory.CreateDirectory(target);
        ProjectScaffolder.Scaffold(target, agent: rail);

        var shipped = Directory
            .GetFiles(System.IO.Path.Combine(target, ".github", "workflows"), "*.yml")
            .ToDictionary(path => System.IO.Path.GetFileName(path)!, path => path, StringComparer.Ordinal);

        // Vacuous-pass guard. Every assertion below is inside a loop over the templates that ship on
        // this rail, so a scaffold that wrote nothing at all would pass the byte comparison by never
        // reaching it — and the rail plumbing is exactly the kind of change that can write nothing.
        shipped.ShouldNotBeEmpty($"the {rail} rail scaffolded no workflow at all.");

        foreach (var source in Directory.GetFiles(TemplateDirectory("workflows"), "*.yml"))
        {
            var template = System.IO.Path.GetFileName(source)!;
            var consumer = ConsumerName(template);

            // A template belonging to the OTHER rail must not be here — under its own name or the
            // bare one. This is the half of the contract the rail introduced: shipping both spellings
            // would give a consumer two nightly jobs contending for one concurrency group.
            if (RailOf(template) is { } owner && owner != rail)
            {
                shipped.ShouldNotContainKey(
                    template,
                    $"{template} belongs to the {owner} rail but was shipped on the {rail} one.");

                continue;
            }

            shipped.ShouldContainKey(
                consumer,
                $"{template} ships on the {rail} rail but no {consumer} was written.");

            File.ReadAllBytes(shipped[consumer]).ShouldBe(
                File.ReadAllBytes(source),
                $"{template} was not shipped verbatim as {consumer}.");
        }
    }

    /// <summary>
    /// A template's consumer-facing name: the rail infix taken back off, so both
    /// <c>docs-refresh.claude.yml</c> and <c>docs-refresh.copilot.yml</c> land as
    /// <c>docs-refresh.yml</c>. Deliberately re-derived here rather than asked of
    /// <c>BundledTemplates</c> — a test that computes the expected name with the same code that
    /// produced it would agree with any bug they share.
    /// </summary>
    private static string ConsumerName(string template) =>
        RailOf(template) is null
            ? template
            : System.IO.Path.GetFileNameWithoutExtension(
                System.IO.Path.GetFileNameWithoutExtension(template)) + ".yml";

    /// <summary>The rail a template is written for, or <see langword="null"/> when it serves both.</summary>
    private static AgentRail? RailOf(string template) =>
        Enum.TryParse<AgentRail>(
            System.IO.Path.GetExtension(System.IO.Path.GetFileNameWithoutExtension(template)).TrimStart('.'),
            ignoreCase: true,
            out var rail)
            ? rail
            : null;

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
    [Theory]
    [InlineData(AgentRail.Claude)]
    [InlineData(AgentRail.Copilot)]
    public void Scaffold_ships_every_workflow_in_the_tree(AgentRail rail)
    {
        var target = System.IO.Path.Combine(_dir, rail.ToString());
        Directory.CreateDirectory(target);
        var results = ProjectScaffolder.Scaffold(target, agent: rail);

        const string prefix = ".github/workflows/";
        var shipped = results
            .Select(r => r.RelativePath)
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..])
            .OrderBy(name => name, StringComparer.Ordinal);

        // Exactly one spelling of each: a railed template contributes its bare name once, from
        // whichever variant this rail selected, and the four rail-agnostic ones contribute themselves.
        // Distinct() is what would hide the bug this asserts against — two variants both shipping —
        // so the expectation is built with it and the comparison below is against the raw list.
        var inTree = Directory
            .GetFiles(TemplateDirectory("workflows"), "*.yml")
            .Select(System.IO.Path.GetFileName)
            .Where(name => RailOf(name!) is not { } owner || owner == rail)
            .Select(name => ConsumerName(name!))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var collided = $"two templates claim the same consumer-facing name on the {rail} rail, so one "
            + "would overwrite the other and a consumer would silently get whichever came last.";

        inTree.Distinct(StringComparer.Ordinal).Count().ShouldBe(inTree.Count, collided);

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

    /// <summary>
    /// Same reasoning as the render script, on the three files that make up the wiki: the skeleton has
    /// to land in the tree the other commands read, which is whatever <c>wiki.root</c> names (PLAN.md
    /// §5.1). A <c>docs/wiki/README.md</c> written while the config points at <c>documentation/</c> is a
    /// page nothing ever publishes and a state file nothing ever loads.
    /// </summary>
    [Fact]
    public void Scaffold_puts_the_skeleton_where_an_existing_config_points()
    {
        WriteConfig("""{"confluence":{"baseUrl":"https://x.atlassian.net/wiki","spaceKey":"SBX"},"wiki":{"root":"documentation"}}""");

        var results = ProjectScaffolder.Scaffold(_dir);

        results.Select(r => r.RelativePath).ShouldBe(
        [
            "docume.json",
            "documentation/README.md",
            "documentation/_meta/STYLE.md",
            "documentation/_meta/state.json",
            .. ExpectedFiles[4..],
        ]);

        File.Exists(Full("documentation/_meta/state.json")).ShouldBeTrue();
        Directory.Exists(Full("docs")).ShouldBeFalse("the default root was scaffolded as well");
    }

    [Fact]
    public void Scaffold_refuses_a_wiki_root_that_escapes_the_target_directory()
    {
        // One level down, for the reason the renderer's escape test explains: the escape then lands in
        // this test's own temp directory rather than the parent every other instance shares.
        var repo = System.IO.Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(
            System.IO.Path.Combine(repo, "docume.json"),
            """{"confluence":{"baseUrl":"https://x.atlassian.net/wiki","spaceKey":"SBX"},"wiki":{"root":"../escaped"}}""");

        var results = ProjectScaffolder.Scaffold(repo);

        var state = results.Single(r => r.RelativePath.EndsWith(
            ProjectScaffolder.StateFile,
            StringComparison.Ordinal));

        state.RelativePath.ShouldBe("docs/wiki/_meta/state.json");
        state.Note.ShouldNotBeNull().ShouldContain("wiki.root");
        Directory
            .Exists(System.IO.Path.Combine(_dir, "escaped"))
            .ShouldBeFalse("the scaffolder wrote outside the directory it was given");
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
    /// Rule §9.4's "never overwrite" is what makes a half-written target permanent: the next run sees
    /// a file at that name, reports <see cref="ScaffoldAction.Skipped"/> with nothing to say about it,
    /// and by design never writes there again. Measured on the shipped CLI before this was fixed —
    /// truncate <c>docume.json</c> to nothing and re-run <c>init</c>, and it stays at 0 bytes while the
    /// table calls it skipped, the same word a healthy file earns.
    /// </summary>
    /// <remarks>
    /// The failure is injected by putting a directory where the write lands its sibling temp — a write
    /// that cannot start, standing in for the disk filling up or the process being killed. The
    /// <c>.tmp</c> suffix is named literally here and in <c>ProjectScaffolder.TemporarySuffix</c>;
    /// changing it there turns this red rather than leaving it vacuous, which is why it is spelled out.
    /// </remarks>
    [Fact]
    public void Scaffold_leaves_no_file_behind_when_a_written_target_cannot_finish()
    {
        Directory.CreateDirectory(Full("docume.json.tmp"));

        Should.Throw<SystemException>(() => ProjectScaffolder.Scaffold(_dir));

        File.Exists(Full("docume.json")).ShouldBeFalse();
    }

    /// <summary>
    /// The same invariant for the templates copied byte for byte. Worth its own test because the cost
    /// of a silent skip differs: a truncated <c>render-mermaid.mjs</c> is a syntactically broken script
    /// that fails at publish time, milestones away from the <c>init</c> that left it there.
    /// </summary>
    [Fact]
    public void Scaffold_leaves_no_file_behind_when_a_copied_template_cannot_finish()
    {
        Directory.CreateDirectory(Full("tools/render-mermaid.mjs.tmp"));

        Should.Throw<SystemException>(() => ProjectScaffolder.Scaffold(_dir));

        File.Exists(Full("tools/render-mermaid.mjs")).ShouldBeFalse();
    }

    /// <summary>
    /// The other half of the family, and the one that destroys rather than poisons: the manifest merge
    /// rewrites a file that already holds the consumer's own pins, so a truncated write costs them
    /// content <c>init</c> never had and cannot restore.
    /// </summary>
    [Fact]
    public void Scaffold_leaves_an_existing_tool_manifest_intact_when_the_rewrite_cannot_finish()
    {
        WriteManifest("""
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "csharpier": { "version": "0.28.2", "commands": [ "dotnet-csharpier" ] }
              }
            }
            """);

        var theirs = File.ReadAllText(Full(".config/dotnet-tools.json"));
        Directory.CreateDirectory(Full(".config/dotnet-tools.json.tmp"));

        Should.Throw<SystemException>(() => ProjectScaffolder.Scaffold(_dir));

        File.ReadAllText(Full(".config/dotnet-tools.json")).ShouldBe(theirs);
    }

    /// <summary>
    /// And the same for the append: a <c>.gitignore</c> is hand-maintained, so losing it to a truncated
    /// rewrite loses work no regeneration brings back.
    /// </summary>
    [Fact]
    public void Scaffold_leaves_an_existing_gitignore_intact_when_the_append_cannot_finish()
    {
        var theirs = $"bin/{Environment.NewLine}obj/{Environment.NewLine}*.user{Environment.NewLine}";
        File.WriteAllText(Full(".gitignore"), theirs);
        Directory.CreateDirectory(Full(".gitignore.tmp"));

        Should.Throw<SystemException>(() => ProjectScaffolder.Scaffold(_dir));

        File.ReadAllText(Full(".gitignore")).ShouldBe(theirs);
    }

    /// <summary>
    /// A successful scaffold leaves the targets and nothing else. <c>init</c> is the first command a
    /// consumer runs and its output is committed by hand, so a leftover temp is junk a person has to
    /// notice — which is why the write deletes its own rather than keeping it as evidence the way
    /// <c>StateStore.Save</c> does.
    /// </summary>
    [Fact]
    public void Scaffold_leaves_no_temporary_file_behind()
    {
        ProjectScaffolder.Scaffold(_dir);

        Directory
            .EnumerateFiles(_dir, "*.tmp", SearchOption.AllDirectories)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// A write that got as far as its temp file and then failed cleans the temp up, rather than leaving
    /// a junk sibling in a repo whose owner commits this output by hand.
    /// </summary>
    /// <remarks>
    /// The failure is injected at the rename rather than at the temp write, by putting a directory where
    /// the target itself belongs: the only shape that leaves a real, deletable temp behind to be cleaned
    /// up. Measured, which is why it is a separate test — the four injections above all fail before a
    /// temp exists, so with only those, deleting the cleanup branch entirely changed nothing.
    /// </remarks>
    [Fact]
    public void Scaffold_removes_its_temporary_file_when_the_write_fails_partway()
    {
        Directory.CreateDirectory(Full("docume.json"));

        Should.Throw<SystemException>(() => ProjectScaffolder.Scaffold(_dir));

        File.Exists(Full("docume.json.tmp")).ShouldBeFalse();
    }

    /// <summary>
    /// A temp file a run killed hard enough to skip its own cleanup did leave behind must not fail the
    /// next <c>init</c>, and must not survive it either. That is what makes the name deterministic
    /// rather than random.
    /// </summary>
    [Fact]
    public void Scaffold_overwrites_a_temporary_file_left_by_a_killed_run()
    {
        File.WriteAllText(Full("docume.json.tmp"), "{ half a config");

        ProjectScaffolder.Scaffold(_dir);

        ConfigLoader.Load(Full("docume.json")).Confluence.SpaceKey.ShouldBe("SPACE");
        File.Exists(Full("docume.json.tmp")).ShouldBeFalse();
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

    /// <summary>
    /// A consumer's own edit to one scaffolded file — realistic in the sense that decides this test:
    /// it must not change what the next run <em>decides</em>. Three targets constrain it. The tool
    /// manifest and <c>.gitignore</c> are shared ground whose skip turns on their content, so each
    /// keeps the thing DocuMe put there (the <c>docume</c> pin, a <c>node_modules</c> line);
    /// <c>docume.json</c> has to stay loadable because three targets' paths are read back out of it.
    /// Everything else is a file DocuMe owns outright and nothing reads, so the default is an
    /// appended line — which is also what a fourteenth target gets, and if its skip turns out to
    /// depend on its content the <c>ShouldAllBe</c> fails loudly rather than quietly passing.
    /// </summary>
    private void EditAsAConsumer(string relativePath)
    {
        var fullPath = Full(relativePath);

        if (string.Equals(relativePath, ".gitignore", StringComparison.Ordinal))
        {
            File.AppendAllText(fullPath, $"*.user{Environment.NewLine}");
            return;
        }

        if (string.Equals(relativePath, ".config/dotnet-tools.json", StringComparison.Ordinal))
        {
            AddAToolOfTheirOwn(fullPath);
            return;
        }

        if (string.Equals(relativePath, ConfigLoader.DefaultFileName, StringComparison.Ordinal))
        {
            // The first edit any consumer makes: replacing the placeholder with their space.
            var filledIn = File.ReadAllText(fullPath)
                .Replace("\"SPACE\"", "\"CONSUMER\"", StringComparison.Ordinal);

            File.WriteAllText(fullPath, filledIn);
            return;
        }

        if (relativePath.EndsWith(ProjectScaffolder.StateFile, StringComparison.Ordinal))
        {
            StateStore.Save(fullPath, PublishedState());
            return;
        }

        File.AppendAllText(fullPath, $"{Environment.NewLine}edited by the consumer{Environment.NewLine}");
    }

    /// <summary>
    /// The manifest as a consumer keeps it: DocuMe's pin plus one of theirs. Left in place by
    /// <see cref="ProjectScaffolder"/>'s merge, which is the branch this exercises.
    /// </summary>
    private static void AddAToolOfTheirOwn(string fullPath)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(fullPath))!.AsObject();
        var tools = manifest["tools"]!.AsObject();

        tools["dotnet-reportgenerator-globaltool"] = new JsonObject
        {
            ["version"] = "5.3.11",
            ["commands"] = new JsonArray("reportgenerator"),
        };

        File.WriteAllText(fullPath, manifest.ToJsonString());
    }

    /// <summary>
    /// What <c>_meta/state.json</c> holds from the first publish onward (PLAN.md §5.3) — the record
    /// that a re-run of <c>init</c> must not touch, since nothing else knows which Confluence page
    /// a markdown file already owns.
    /// </summary>
    private static DocumeState PublishedState() => new()
    {
        LastPublishedSha = "0f1e2d3c4b5a6978",
        Pages = new Dictionary<string, PageState>(StringComparer.Ordinal)
        {
            ["README.md"] = new PageState
            {
                PageId = "451871005",
                Title = "Documentation",
                ContentHash = "e3b0c44298fc1c14",
                PublishedVersion = 3,
            },
        },
    };

    /// <summary>Every file in the scaffolded tree, as relative-path to content hash.</summary>
    private SortedDictionary<string, string> Snapshot()
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            var hash = SHA256.HashData(File.ReadAllBytes(file));
            snapshot[System.IO.Path.GetRelativePath(_dir, file)] = Convert.ToHexString(hash);
        }

        return snapshot;
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
