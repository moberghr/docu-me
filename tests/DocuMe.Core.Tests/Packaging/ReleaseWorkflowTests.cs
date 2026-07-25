using Shouldly;
using YamlDotNet.RepresentationModel;

namespace DocuMe.Core.Tests.Packaging;

/// <summary>
/// The release workflow, <c>.github/workflows/release.yml</c> (PLAN.md §12): one tag <c>vX.Y.Z</c>
/// releases CLI, Core and the plugin off a single version.
/// </summary>
/// <remarks>
/// <para>
/// This file runs once per release, on a tag, and every mistake in it is discovered at the worst
/// moment: after the tag is pushed, in front of whoever is waiting for the package. Two of its
/// failures are also irreversible — a package pushed to a feed cannot be unpublished, and a tag that
/// has cut a release is not re-tagged. So the assertions here are about the order and the guards, not
/// about the yaml being pretty.
/// </para>
/// <para>
/// What motivates each one. A release that packs before it tests publishes a red build (rule §8.3, in
/// the one place where it cannot be undone). A trigger that also fires on a branch publishes from
/// every commit. Missing <c>packages: write</c> fails at the push, after the build and the tests have
/// already run. And the version guard is the whole reason the workflow starts the way it does: §12's
/// single version now lives in three files plus the tag, <see cref="PluginManifestTests"/> can only
/// see the three, and a plugin pinned to a version that was never released is one Claude Code keeps
/// its cached copy of forever.
/// </para>
/// </remarks>
public sealed class ReleaseWorkflowTests
{
    /// <summary>The files the guard has to read, because each one carries the version separately.</summary>
    private static readonly string[] VersionSites =
    [
        "Directory.Build.props",
        "plugin/.claude-plugin/plugin.json",
        "plugin/README.md",
    ];

    private const string Feed = "nuget.pkg.github.com";

    [Fact]
    public void The_release_workflow_is_where_Actions_looks_for_it()
    {
        // Not redundant with the tests below: they all parse this path, so a workflow that moved (or
        // was never added) would turn each of them into a vacuous pass on an empty document.
        File.Exists(WorkflowPath).ShouldBeTrue($"No release workflow at {WorkflowPath} (PLAN.md §12).");
    }

    [Fact]
    public void It_fires_on_a_version_tag_and_on_nothing_else()
    {
        var triggers = Mapping(Root(), "on");

        // A `workflow_dispatch` or a `schedule` next to the tag trigger would publish a package off
        // something that is not a release. Read as a scalar key on purpose: a typed deserializer
        // resolves `on` to a boolean under YAML 1.1 and the assertion would be about the wrong key.
        Keys(triggers).ShouldBe(["push"], "release.yml must trigger on a tag push and nothing else.");

        var push = Mapping(triggers, "push");

        // The one that matters: `branches:` here and every commit to main cuts a release.
        Keys(push).ShouldBe(["tags"], "release.yml must filter on tags only — no branches.");

        var patterns = Sequence(push, "tags").Select(Scalar).ToList();

        patterns.ShouldNotBeEmpty("release.yml has an empty tag filter, which matches every tag.");
        patterns.ShouldAllBe(pattern => pattern.StartsWith('v'), "§12 releases from `vX.Y.Z` tags.");
    }

    [Fact]
    public void It_asks_for_the_two_permissions_a_release_needs()
    {
        var permissions = Mapping(Root(), "permissions");

        // Both fail late and only in the job that needs them: without `packages` the push 403s after
        // the build and the whole test suite have run, and without `contents` the release is not cut
        // after the package is already on the feed — the half-released state this file exists to avoid.
        Value(permissions, "packages").ShouldBe("write", "The push to GitHub Packages needs packages: write.");
        Value(permissions, "contents").ShouldBe("write", "Cutting the GitHub Release needs contents: write.");
    }

    [Fact]
    public void It_verifies_the_tag_against_every_file_that_carries_the_version()
    {
        var guard = Steps()[IndexOfRun("Directory.Build.props")];
        var script = Run(guard);

        // The tag half of §12's single version. PluginManifestTests asserts the three files agree with
        // each other and with the built assembly; nothing but this step can compare them to the tag,
        // and a tag that disagrees is exactly how a plugin ends up pinned to a version nobody shipped.
        var missing = VersionSites
            .Where(site => !script.Contains(site, StringComparison.Ordinal))
            .ToList();

        missing.ShouldBeEmpty("The version guard does not read every file that carries the version (§12).");

        script.ShouldContain(
            "GITHUB_REF_NAME",
            customMessage: "The version guard never reads the tag, so it cannot be comparing anything to it.");

        // Before the build, not merely before the push: the guard is cheap and the failure it catches
        // is a half-bumped release, so paying for a full build first only makes the answer slower.
        IndexOfRun("Directory.Build.props").ShouldBeLessThan(
            IndexOfRun("dotnet build"),
            "The version guard must run before the build — it is the cheapest check in the file.");
    }

    [Fact]
    public void It_tests_before_it_packs_and_packs_before_it_pushes()
    {
        var build = IndexOfRun("dotnet build");
        var test = IndexOfRun("dotnet test");
        var pack = IndexOfRun("dotnet pack");
        var push = IndexOfRun("dotnet nuget push");

        var steps = new[] { build, test, pack, push };

        steps.ShouldAllBe(index => index >= 0, "release.yml is missing a build, test, pack or push step.");

        // The irreversible one. A package on a feed cannot be unpublished, so the test run is the last
        // moment at which a bad release is still free — rule §8.3 with no way to amend the commit.
        const string OrderMessage = "release.yml must build, then test, then pack, then push: "
            + "a package that is already on the feed cannot be unpublished.";

        build.ShouldBeLessThan(test, OrderMessage);
        test.ShouldBeLessThan(pack, OrderMessage);
        pack.ShouldBeLessThan(push, OrderMessage);
    }

    [Fact]
    public void Everything_it_ships_is_built_in_Release()
    {
        var offenders = Steps()
            .Select(Run)
            .SelectMany(script => script.Split('\n'))
            .Where(line => line.Contains("dotnet build", StringComparison.Ordinal)
                || line.Contains("dotnet test", StringComparison.Ordinal)
                || line.Contains("dotnet pack", StringComparison.Ordinal))
            .Where(line => !line.Contains("--configuration Release", StringComparison.Ordinal))
            .ToList();

        // A Debug pack is a valid nupkg. It installs, it runs, and nothing about it says it is the
        // wrong build until someone profiles it.
        offenders.ShouldBeEmpty("release.yml builds, tests or packs outside Release configuration.");
    }

    [Fact]
    public void It_pushes_to_GitHub_Packages_with_the_workflow_token()
    {
        var push = Run(Steps()[IndexOfRun("dotnet nuget push")]);

        // §12 names GitHub Packages, and the reason it is the default rather than nuget.org is that
        // GITHUB_TOKEN already reaches the owner's own feed: no new secret, nothing to rotate.
        Value(JobEnvironment(), "FEED").ShouldContain(
            Feed,
            customMessage: $"§12 publishes to GitHub Packages; the feed does not point at {Feed}.");

        push.ShouldContain("$FEED", customMessage: "The push step does not use the declared feed.");

        // Re-runnability, and it is not hypothetical: the release step after this one can fail on its
        // own, and a re-run would then hit a 409 here and never reach it.
        push.ShouldContain(
            "--skip-duplicate",
            customMessage: "Without --skip-duplicate a re-run fails at the push instead of resuming past it.");
    }

    [Fact]
    public void No_credential_is_written_into_the_workflow()
    {
        var text = Text();

        // Rule §1.1, and the release token in particular: it is a secret reference or it is a leak,
        // because a workflow file is as public as the repository.
        text.ShouldContain(
            "${{ secrets.GITHUB_TOKEN }}",
            customMessage: "The workflow token must come from secrets, not from a literal.");
        text.ShouldNotContain(
            "ghp_",
            customMessage: "A personal access token is written into release.yml (rule §1.1).");
    }

    [Fact]
    public void A_release_never_touches_Confluence()
    {
        // Rule §0.1 as a file assertion. Cutting a release is not publishing documentation, and the
        // production space is write-locked until the M7 gate — a release workflow that carried the
        // credentials would be one tag away from writing to it.
        Text().ShouldNotContain(
            "DOCUME_CONFLUENCE_",
            customMessage: "release.yml holds a Confluence credential — a release publishes packages, not pages.");
    }

    [Fact]
    public void The_release_notes_carry_the_marketplace_entry_pinned_to_this_tag()
    {
        var notes = Run(Steps()[IndexOfRun("gh release create")]);

        // §12's "plugin marketplace ref update" is the one step of a release that this repository
        // cannot perform: the Moberg marketplace is a different repository, so the entry is pasted in
        // by hand. Putting it in the release notes with `ref` already filled in is what keeps that
        // paste from being the place a stale version is introduced.
        notes.ShouldContain("git-subdir", customMessage: "The release notes lost the marketplace entry (§12).");
        notes.ShouldContain(
            "\"ref\": \"$TAG\"",
            customMessage: "The marketplace entry in the release notes is not pinned to the tag being released.");
    }

    [Fact]
    public void The_release_notes_describe_the_plugin_the_way_plugin_json_does()
    {
        var notes = Run(Steps()[IndexOfRun("gh release create")]);

        // The description is what the /plugin Discover list shows before anything is fetched, so the
        // pasted entry needs it — an entry without one advertises the plugin as a blank line. It is read
        // out of plugin.json at release time rather than retyped here, which keeps this file from becoming
        // a third copy to drift, and has a second payoff: bash does not rescan an expanded value, so the
        // backticks inside the description stay literal without the escaping the rest of this unquoted
        // heredoc needs. Retyping it inline is how `docume` becomes a command substitution.
        notes.ShouldContain(
            "description=$(jq -r '.description' plugin/.claude-plugin/plugin.json)",
            customMessage: "The release notes stopped deriving the plugin description from plugin.json.");

        notes.ShouldContain(
            "\"description\": \"$description\",",
            customMessage: "The marketplace entry in the release notes carries no description (§12).");
    }

    private static string RepoRoot { get; } = Locate();

    private static string WorkflowPath { get; } =
        Path.Combine(RepoRoot, ".github", "workflows", "release.yml");

    private static string Text() => File.ReadAllText(WorkflowPath);

    private static YamlMappingNode Root()
    {
        var stream = new YamlStream();
        using var reader = new StringReader(Text());

        // Load, not Deserialize: an indentation slip is the failure hand-written yaml actually has,
        // and GitHub reports it as "invalid workflow file" only once the tag is already pushed.
        stream.Load(reader);

        stream.Documents.Count.ShouldBe(1, "release.yml should be one yaml document.");

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    /// <summary>The steps of the single release job, in the order the runner executes them.</summary>
    private static List<YamlMappingNode> Steps()
    {
        var jobs = Mapping(Root(), "jobs");

        jobs.Children.Count.ShouldBe(1, "release.yml should be one job; the ordering assertions assume it.");

        var job = (YamlMappingNode)jobs.Children.First().Value;
        var steps = (YamlSequenceNode)job.Children.Single(child => IsKey(child.Key, "steps")).Value;

        return steps.OfType<YamlMappingNode>().ToList();
    }

    private static YamlMappingNode JobEnvironment()
    {
        var jobs = Mapping(Root(), "jobs");
        var job = (YamlMappingNode)jobs.Children.First().Value;

        return Mapping(job, "env");
    }

    /// <summary>The shell of a step, or empty for the steps that only say <c>uses:</c>.</summary>
    private static string Run(YamlMappingNode step)
    {
        var run = step.Children.FirstOrDefault(child => IsKey(child.Key, "run")).Value;

        return run is null ? string.Empty : Scalar(run);
    }

    /// <summary>The position of the first step whose shell mentions <paramref name="fragment"/>, or -1.</summary>
    private static int IndexOfRun(string fragment)
        => Steps().FindIndex(step => Run(step).Contains(fragment, StringComparison.Ordinal));

    private static IEnumerable<string> Keys(YamlMappingNode node)
        => node.Children.Select(child => Scalar(child.Key));

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
    {
        var child = parent.Children.FirstOrDefault(candidate => IsKey(candidate.Key, key)).Value;

        child.ShouldNotBeNull($"release.yml has no '{key}'.");

        return (YamlMappingNode)child;
    }

    private static IEnumerable<YamlNode> Sequence(YamlMappingNode parent, string key)
        => (YamlSequenceNode)parent.Children.Single(child => IsKey(child.Key, key)).Value;

    private static string Value(YamlMappingNode parent, string key)
    {
        var child = parent.Children.FirstOrDefault(candidate => IsKey(candidate.Key, key)).Value;

        child.ShouldNotBeNull($"release.yml has no '{key}'.");

        return Scalar(child);
    }

    private static bool IsKey(YamlNode node, string name)
        => string.Equals(Scalar(node), name, StringComparison.Ordinal);

    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value ?? string.Empty;

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>: the workflow ships in the tree and has no
    /// build artifact, so the shipped copy is the one under test.
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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so release.yml cannot be found.");
    }
}
