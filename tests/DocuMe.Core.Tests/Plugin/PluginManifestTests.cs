using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DocuMe.Core.Scaffolding;
using Shouldly;

namespace DocuMe.Core.Tests.Plugin;

/// <summary>
/// The packaging manifests of the Claude Code plugin (PLAN.md §11, §12): <c>plugin/.claude-plugin/plugin.json</c>
/// and the repo-root <c>.claude-plugin/marketplace.json</c> that distributes it.
/// </summary>
/// <remarks>
/// <para>
/// Neither file is read by a line of C#, so nothing else in this suite would notice either one being wrong.
/// Every failure mode below is silent in the same way: the plugin still looks installed, and something a
/// user asked for simply never happens.
/// </para>
/// <para>
/// The three that motivate the file. A manifest in the wrong place, or a component directory that drifted
/// into <c>.claude-plugin/</c> next to it, loads a plugin with no skills at all. A <c>version</c> that stops
/// matching <c>Directory.Build.props</c> pins the plugin to a string a release no longer bumps, and Claude
/// Code then keeps its cached copy forever — §12's single-version rule (one number across CLI, Core, plugin
/// and action) is the thing that breaks, and it breaks by users quietly staying behind. A marketplace
/// <c>source</c> that points at a directory that is not there fails only on someone else's install.
/// </para>
/// <para>
/// What is deliberately *not* asserted: that the manifest lists its skills. It does not, and should not —
/// <c>skills/</c> is scanned by default, so listing them would add a second place to edit when
/// <c>docs-loop</c> lands. This file walks the tree instead, which is the same thing Claude Code does.
/// <see cref="SkillContractTests"/> covers what is inside each SKILL.md.
/// </para>
/// </remarks>
public sealed class PluginManifestTests
{
    /// <summary>
    /// The name users type: <c>/plugin install docume@docume</c>. Both halves are public-facing and both are
    /// written down in <c>plugin/README.md</c>, so neither is free to change quietly.
    /// </summary>
    private const string PluginName = "docume";

    /// <summary>
    /// The marketplace's own name, and the reason it is not <c>moberg</c>. §11 distributes DocuMe through the
    /// existing Moberg marketplace, which MTK also ships from — and Claude Code registers one marketplace per
    /// name, so adding a second under a name already taken *replaces* the first. A marketplace here called
    /// <c>moberg</c> would evict MTK from the machine of anyone who added both.
    /// </summary>
    private const string MarketplaceName = "docume";

    [Fact]
    public void The_manifest_is_where_Claude_Code_looks_for_it()
    {
        // Not a redundant existence check: every other test in this class reads this file, so a manifest that
        // moved would turn all of them into vacuous passes on a null.
        File.Exists(ManifestPath).ShouldBeTrue($"No plugin manifest at {ManifestPath} (PLAN.md §3, §11).");
    }

    [Fact]
    public void Only_the_manifest_lives_beside_the_manifest()
    {
        var directory = Path.GetDirectoryName(ManifestPath)!;

        var strays = Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, "plugin.json", StringComparison.Ordinal))
            .ToList();

        // Components (skills/, agents/, hooks/) belong at the plugin root, not inside .claude-plugin/. Put a
        // skills/ directory in here and Claude Code loads the plugin, reports no error, and exposes nothing.
        var message = $"{directory} holds more than plugin.json — components go at the plugin root, "
            + "not beside the manifest.";

        strays.ShouldBeEmpty(message);
    }

    [Fact]
    public void The_manifest_names_the_plugin_users_install()
    {
        var manifest = Manifest();

        // `name` is the only required field, and it is what namespaces every skill: `docume:docs-refresh`.
        Value(manifest, "name").ShouldBe(PluginName);
        Value(manifest, "description").Length.ShouldBeGreaterThan(
            40,
            "plugin.json needs a description: it is what the /plugin picker shows before an install.");
    }

    [Fact]
    public void The_manifest_version_is_the_one_version_the_release_bumps()
    {
        var declared = Value(Manifest(), "version");

        // §12: one version across CLI, Core, plugin and action. Directory.Build.props is where that number
        // lives, so this is the assertion that makes a release edit both files or go red.
        var propsMessage = $"plugin.json pins {declared} and Directory.Build.props says {SolutionVersion} — "
            + "§12 releases them off one version.";

        declared.ShouldBe(SolutionVersion, propsMessage);

        // And the same number the tool itself reports, which is what `init` pins into a consumer's
        // dotnet-tools.json (ProjectScaffolder.ResolveToolVersion). Closing this loop is what makes
        // "the plugin and the CLI are the same release" a fact rather than a convention.
        var assemblyMessage = $"plugin.json pins {declared} and the built assembly is {AssemblyVersion} — "
            + "rebuild, or the plugin ships pinned to a version that was never released.";

        declared.ShouldBe(AssemblyVersion, assemblyMessage);
    }

    [Fact]
    public void Every_skill_in_the_tree_is_discoverable_without_being_listed()
    {
        var skillsDirectory = Path.Combine(PluginDirectory, "skills");
        var skills = Directory.EnumerateDirectories(skillsDirectory)
            .Select(Path.GetFileName)
            .Select(name => name!)
            .ToList();

        skills.ShouldNotBeEmpty($"{skillsDirectory} has no skills, so the plugin exposes nothing.");

        var missing = skills
            .Where(skill => !File.Exists(Path.Combine(skillsDirectory, skill, "SKILL.md")))
            .ToList();

        // A skill directory without a SKILL.md is skipped in silence: the plugin installs, and `/docs-loop`
        // just is not there.
        missing.ShouldBeEmpty($"Skill directories under {skillsDirectory} with no SKILL.md.");

        // The manifest may override component paths, and this one deliberately does not — `skills/` is
        // scanned by default. An override added later without the directory to back it is a load error, so
        // if one appears, it has to resolve.
        var declared = Manifest()["skills"];

        if (declared is null)
        {
            return;
        }

        var paths = declared is JsonArray array
            ? array.Select(node => node!.GetValue<string>()).ToList()
            : [declared.GetValue<string>()];

        var unresolved = paths
            .Where(path => !Directory.Exists(Path.Combine(PluginDirectory, path)))
            .ToList();

        unresolved.ShouldBeEmpty($"plugin.json declares skill paths that do not exist under {PluginDirectory}.");
    }

    [Fact]
    public void The_marketplace_lists_the_plugin_at_a_source_that_resolves()
    {
        var marketplace = Marketplace();

        Value(marketplace, "name").ShouldBe(MarketplaceName);
        Value((JsonObject)marketplace["owner"]!, "name").ShouldNotBeNullOrWhiteSpace();

        var source = Value(Entry(), "source");

        // Relative sources resolve against the marketplace root — the directory holding .claude-plugin/,
        // not .claude-plugin/ itself — and must start with "./".
        source.ShouldStartWith("./", customMessage: "A relative plugin source must start with './'.");

        var resolved = Path.Combine(RepoRoot, source[2..]);
        var manifest = Path.Combine(resolved, ".claude-plugin", "plugin.json");

        // The install-time failure this catches: "Plugin directory not found at path", on someone else's
        // machine, after a rename in this repo that nothing here objected to.
        File.Exists(manifest).ShouldBeTrue($"marketplace.json source '{source}' has no plugin manifest at {manifest}.");
    }

    [Fact]
    public void The_marketplace_entry_carries_no_version_of_its_own()
    {
        // Claude Code accepts `version` in both places and lets plugin.json win. Carrying it twice would mean
        // two numbers to bump per release and one of them silently ignored — exactly the drift §12's
        // single-version rule exists to avoid. One copy, in plugin.json.
        Entry()["version"].ShouldBeNull(
            "The marketplace entry must not pin a version; plugin.json is the single copy (§12).");
    }

    [Fact]
    public void The_marketplace_entry_and_the_manifest_describe_the_same_plugin()
    {
        var entry = Entry();

        Value(entry, "name").ShouldBe(Value(Manifest(), "name"));

        // The entry's description is what a user reads in the Discover list, before anything is fetched; the
        // manifest's is what they read afterwards. Two different sentences means one of them is stale, and
        // there is no way to tell which from either side.
        Value(entry, "description").ShouldBe(
            Value(Manifest(), "description"),
            "The marketplace entry and plugin.json describe the plugin differently.");
    }

    private static string RepoRoot { get; } = Locate();

    private static string PluginDirectory { get; } = Path.Combine(RepoRoot, "plugin");

    private static string ManifestPath { get; } =
        Path.Combine(PluginDirectory, ".claude-plugin", "plugin.json");

    private static string MarketplacePath { get; } =
        Path.Combine(RepoRoot, ".claude-plugin", "marketplace.json");

    /// <summary>The <c>&lt;Version&gt;</c> in <c>Directory.Build.props</c>: §12's single version.</summary>
    private static string SolutionVersion { get; } = ReadSolutionVersion();

    /// <summary>The version of the built Core assembly, resolved the way <c>init</c> resolves its pin.</summary>
    private static string AssemblyVersion { get; } = ReadAssemblyVersion();

    private static JsonObject Manifest() => Read(ManifestPath);

    private static JsonObject Marketplace() => Read(MarketplacePath);

    /// <summary>The <c>docume</c> entry in the marketplace's <c>plugins</c> array.</summary>
    private static JsonObject Entry()
    {
        var plugins = Marketplace()["plugins"] as JsonArray;

        plugins.ShouldNotBeNull("marketplace.json has no `plugins` array.");

        var entry = plugins
            .OfType<JsonObject>()
            .SingleOrDefault(candidate => string.Equals(
                candidate["name"]?.GetValue<string>(),
                PluginName,
                StringComparison.Ordinal));

        entry.ShouldNotBeNull($"marketplace.json lists no plugin named '{PluginName}'.");

        return entry;
    }

    private static JsonObject Read(string path)
    {
        // Parse rather than deserialize: a trailing comma or an unquoted key is the mistake a hand-edited
        // manifest actually makes, and Claude Code reports it as a corrupt manifest. It throws here first.
        var node = JsonNode.Parse(File.ReadAllText(path));

        node.ShouldNotBeNull($"{path} is empty.");

        return (JsonObject)node;
    }

    private static string Value(JsonObject json, string key)
    {
        var node = json[key];

        node.ShouldNotBeNull($"No '{key}' in the manifest.");

        return node.GetValue<string>();
    }

    private static string ReadSolutionVersion()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"));
        var version = props.Descendants("Version").SingleOrDefault();

        version.ShouldNotBeNull("Directory.Build.props declares no <Version> (§12).");

        return version.Value.Trim();
    }

    private static string ReadAssemblyVersion()
    {
        var assembly = typeof(ProjectScaffolder).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // SourceLink appends "+<commit sha>"; NuGet and the manifest both publish the bare version.
        var metadata = informational.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? informational : informational[..metadata];
    }

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>. Both manifests ship in the tree, so the shipped
    /// copies are what gets read — there is no build artifact of either.
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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the plugin manifests cannot be found.");
    }
}
