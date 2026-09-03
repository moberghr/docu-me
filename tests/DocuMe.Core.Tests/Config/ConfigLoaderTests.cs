using DocuMe.Core.Config;
using Shouldly;

namespace DocuMe.Core.Tests.Config;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docume-config-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteConfig(string json)
    {
        var path = System.IO.Path.Combine(_dir, ConfigLoader.DefaultFileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_ValidConfig_ReturnsModelWithValuesAndDefaults()
    {
        // Comments and a trailing comma exercise the tolerant read options.
        var path = WriteConfig(
            """
            {
              // consumer-owned, no secrets
              "confluence": {
                "baseUrl": "https://kvika.atlassian.net/wiki",
                "spaceKey": "AUR",
                "spaceId": "2431647748",
              },
              "wiki": { "root": "docs/wiki" }
            }
            """);

        var config = ConfigLoader.Load(path);

        config.Confluence.BaseUrl.ShouldBe("https://kvika.atlassian.net/wiki");
        config.Confluence.SpaceKey.ShouldBe("AUR");
        config.Confluence.SpaceId.ShouldBe("2431647748");
        config.Wiki.Root.ShouldBe("docs/wiki");

        // Defaults fill in unspecified sections (PLAN.md §5.1).
        config.Labels.Approved.ShouldBe("approved");
        config.Labels.Stale.ShouldBe("stale");
        config.Dashboard.Title.ShouldBe("Documentation Status");
        config.Drift.DefaultBranch.ShouldBe("dev");
        config.Mermaid.Renderer.ShouldBe("tools/render-mermaid.mjs");
        config.Wiki.HomePage.ShouldBe("README.md");
        config.Wiki.Exclude.ShouldContain("_meta/**");
        config.Wiki.MaxChildren.ShouldBe(WikiConfig.DefaultMaxChildren);
    }

    /// <summary>
    /// SC4: the one number the structure check takes. A repo that means its wide section raises it here,
    /// which is the whole answer to "12 is a guess" — it costs a line of config rather than a code change.
    /// </summary>
    [Fact]
    public void Load_ExplicitMaxChildren_OverridesTheDefault()
    {
        var path = WriteConfig(
            """
            {
              "confluence": {
                "baseUrl": "https://kvika.atlassian.net/wiki",
                "spaceKey": "AUR"
              },
              "wiki": { "root": "docs/wiki", "maxChildren": 40 }
            }
            """);

        ConfigLoader.Load(path).Wiki.MaxChildren.ShouldBe(40);
    }

    [Fact]
    public void Load_MissingRequiredFields_ThrowsWithErrorList()
    {
        var path = WriteConfig(
            """
            { "confluence": { "spaceId": "123" } }
            """);

        var ex = Should.Throw<ConfigValidationException>(() => ConfigLoader.Load(path));

        ex.Errors.ShouldContain("confluence.baseUrl is required");
        ex.Errors.ShouldContain("confluence.spaceKey is required");
        ex.Path.ShouldBe(path);
    }

    [Fact]
    public void Load_EmptyWikiRoot_ReportsWikiRootRequired()
    {
        // wiki.root defaults to "docs/wiki", so an explicit empty value is the
        // only way to reach the wiki.root validation branch.
        var path = WriteConfig(
            """
            {
              "confluence": { "baseUrl": "https://kvika.atlassian.net/wiki", "spaceKey": "AUR" },
              "wiki": { "root": "" }
            }
            """);

        var ex = Should.Throw<ConfigValidationException>(() => ConfigLoader.Load(path));

        ex.Errors.ShouldContain("wiki.root is required");
    }

    [Fact]
    public void Load_MissingFile_ThrowsConfigNotFound()
    {
        var path = System.IO.Path.Combine(_dir, "does-not-exist.json");

        var ex = Should.Throw<ConfigNotFoundException>(() => ConfigLoader.Load(path));

        ex.Path.ShouldBe(path);
    }
}
