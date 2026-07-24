using DocuMe.Core.Config;
using DocuMe.Core.Scaffolding;
using Shouldly;

namespace DocuMe.Core.Tests.Scaffolding;

public sealed class ProjectScaffolderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docume-scaffold-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly string[] ExpectedFiles =
    [
        "docume.json",
        "docs/wiki/README.md",
        "docs/wiki/_meta/STYLE.md",
        "docs/wiki/_meta/state.json",
    ];

    [Fact]
    public void Scaffold_EmptyDirectory_CreatesEverything()
    {
        var results = ProjectScaffolder.Scaffold(_dir);

        results.Select(r => r.RelativePath).ShouldBe(ExpectedFiles);
        results.ShouldAllBe(r => r.Action == ScaffoldAction.Created);

        foreach (var relative in ExpectedFiles)
        {
            var full = System.IO.Path.Combine([_dir, .. relative.Split('/')]);
            File.Exists(full).ShouldBeTrue($"expected {relative} to be written");
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
}
