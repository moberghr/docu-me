using System.Diagnostics;
using System.Text.Json.Nodes;
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
/// single version now lives in three files plus the tag, <see cref="Plugin.PluginManifestTests"/> can only
/// see the three, and a plugin pinned to a version that was never released is one Claude Code keeps
/// its cached copy of forever.
/// </para>
/// <para>
/// Its two shell steps are executed, not read. Both are one-shot and neither can be rehearsed on a
/// runner: the version guard runs once per tag and the release it refuses is the only signal it works,
/// and the notes step runs <em>after</em> the packages are already on the feed, so its failure leaves a
/// released-but-unannounced version. The notes are also the text a human pastes into the Moberg
/// marketplace repository, which makes wrong content worse than a crash — a bad paste installs a plugin
/// that fails silently. The scripts come out of the shipped yaml, never retyped.
/// </para>
/// </remarks>
public sealed class ReleaseWorkflowTests : IDisposable
{
    /// <summary>The files the guard has to read, because each one carries the version separately.</summary>
    private static readonly string[] VersionSites =
    [
        "Directory.Build.props",
        "plugin/.claude-plugin/plugin.json",
        "plugin/README.md",
    ];

    private const string Feed = "nuget.pkg.github.com";

    private readonly List<string> _scratch = [];

    public void Dispose()
    {
        foreach (var directory in _scratch.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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

    // ---- the two shell steps, executed rather than read --------------------------------------------

    /// <summary>
    /// The guard's happy path, and the vacuity guard for every refusal below it: if the harness could not
    /// drive one accepted release, a step dying on its first line would read as a pass everywhere else.
    /// </summary>
    /// <remarks>
    /// The fixture is the repository's own three version files, unmodified, with the tag derived from
    /// them — so this also fails on a half-bumped tree, which is correct and not a second copy of
    /// <see cref="Plugin.PluginManifestTests"/>: that one compares the files to each other at desk time, this one
    /// runs the shell that compares them to the tag.
    /// </remarks>
    [Fact]
    public void The_version_guard_accepts_a_tag_all_three_files_agree_with()
    {
        var guard = RunVersionGuard($"v{CurrentVersion()}", NewVersionTree());

        guard.Code.ShouldBe(0, guard.Diagnostics);
        guard.Outputs.ShouldContainKeyAndValue("version", CurrentVersion());
    }

    /// <summary>
    /// A half-bumped release: two files moved to the tag and one did not. Each site gets its own case
    /// because the guard reads each with a different tool, and a release that ships with any one of them
    /// stale is the failure the whole step exists for.
    /// </summary>
    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("plugin/.claude-plugin/plugin.json")]
    [InlineData("plugin/README.md")]
    public void The_version_guard_refuses_the_release_when_one_file_lags(string lagging)
    {
        var guard = RunVersionGuard("v9.9.9", NewVersionTree("9.9.9", lagging));

        guard.Code.ShouldNotBe(0, $"A release with a stale {lagging} was allowed through.\n{guard.Diagnostics}");
        guard.Outputs.ShouldNotContainKey("version", "A refused release still published a version output.");

        // The annotation has to name the file, because the fix is to bump that one and re-push the tag.
        var named = guard.Annotations.Exists(line => line.Contains(lagging, StringComparison.Ordinal));
        named.ShouldBeTrue($"No ::error:: named {lagging}. Got:\n{string.Join('\n', guard.Annotations)}");
    }

    /// <summary>
    /// A props file with no <c>&lt;Version&gt;</c> at all. The sed prints nothing and exits 0, so this is
    /// the shape that could have compared empty-to-empty and released whatever was on the tag.
    /// </summary>
    [Fact]
    public void The_version_guard_refuses_a_props_file_carrying_no_version()
    {
        const string Props = "<Project>\n  <PropertyGroup>\n    <LangVersion>latest</LangVersion>\n  </PropertyGroup>\n</Project>\n";
        var tree = NewVersionTree();
        File.WriteAllText(Path.Combine(tree, "Directory.Build.props"), Props);

        var guard = RunVersionGuard("v0.1.0", tree);

        guard.Code.ShouldNotBe(0, $"A props file with no version released anyway.\n{guard.Diagnostics}");
    }

    /// <summary>
    /// <c>LangVersion</c> sits directly above <c>Version</c> in this repository's props file, and the sed
    /// that reads one must not match the other.
    /// </summary>
    [Fact]
    public void The_version_guard_does_not_read_LangVersion_as_the_package_version()
    {
        const string Props = "<Project>\n  <PropertyGroup>\n    <LangVersion>latest</LangVersion>\n    <Version>3.4.5</Version>\n  </PropertyGroup>\n</Project>\n";
        var tree = NewVersionTree("3.4.5", lagging: "Directory.Build.props");
        File.WriteAllText(Path.Combine(tree, "Directory.Build.props"), Props);

        var guard = RunVersionGuard("v3.4.5", tree);

        guard.Code.ShouldBe(0, guard.Diagnostics);
        guard.Outputs.ShouldContainKeyAndValue("version", "3.4.5");
    }

    /// <summary>
    /// A version file it cannot read at all must stop the release, not compare against an empty string.
    /// Every read here is a bare assignment for that reason — a substitution nested inside an argument is
    /// invisible to <c>set -e</c>, which is how the same shape went silently wrong in docs-refresh.yml.
    /// </summary>
    [Theory]
    [InlineData("Directory.Build.props", null)]
    [InlineData("plugin/README.md", null)]
    [InlineData("plugin/.claude-plugin/plugin.json", "{ \"version\": ")]
    public void The_version_guard_stops_when_a_version_file_cannot_be_read(string site, string? corrupt)
    {
        var tree = NewVersionTree();
        var path = Path.Combine(tree, site.Replace('/', Path.DirectorySeparatorChar));

        if (corrupt is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, corrupt);
        }

        var guard = RunVersionGuard("v0.1.0", tree);

        guard.Code.ShouldNotBe(0, $"An unreadable {site} did not stop the release.\n{guard.Diagnostics}");
        guard.Outputs.ShouldNotContainKey("version", $"An unreadable {site} still published a version output.");
    }

    /// <summary>
    /// §12's "plugin marketplace ref update" is the one step of a release this repository cannot perform:
    /// the Moberg marketplace is a different repository, so the entry is pasted in by hand. The release
    /// notes carry it with <c>ref</c> already filled in, which is what keeps that paste from being where a
    /// stale version enters.
    /// </summary>
    [Fact]
    public void The_release_notes_carry_a_marketplace_entry_pinned_to_this_tag()
    {
        var release = RunReleaseNotes("v1.2.3", "1.2.3");

        release.Code.ShouldBe(0, release.Diagnostics);

        var entry = release.MarketplaceEntry();
        ((string?)entry["source"]?["source"]).ShouldBe("git-subdir");
        ((string?)entry["source"]?["ref"]).ShouldBe("v1.2.3", "The pasted marketplace entry is not pinned to this tag.");
        ((string?)entry["source"]?["path"]).ShouldBe("plugin");
    }

    /// <summary>
    /// The regression this file was written for. The entry used to be hand-interpolated into the heredoc,
    /// so a description carrying a double quote emitted invalid json into the release notes — and nothing
    /// noticed, because the manifest stays valid and the break only surfaces when a human pastes it and
    /// the plugin fails to install. Built by jq, both fields are escaped for us.
    /// </summary>
    [Theory]
    [InlineData("Docs lifecycle for the \\\"repo is truth\\\" model.")]
    [InlineData("Backslash-terminated path: C:\\\\docs\\\\wiki")]
    [InlineData("A newline\\nin the middle.")]
    public void The_marketplace_entry_stays_valid_json_whatever_the_description_holds(string description)
    {
        var tree = NewVersionTree();
        var manifestPath = Path.Combine(tree, "plugin", ".claude-plugin", "plugin.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["description"] = JsonNode.Parse($"\"{description}\"");
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        var release = RunReleaseNotes("v1.2.3", "1.2.3", tree);

        release.Code.ShouldBe(0, release.Diagnostics);

        // Parsing it IS the assertion: an unescaped quote makes this throw, which is what shipped before.
        var entry = release.MarketplaceEntry();
        ((string?)entry["description"]).ShouldBe((string?)manifest["description"]);
    }

    /// <summary>
    /// The description is what the <c>/plugin</c> Discover list shows before anything is fetched, so the
    /// pasted entry needs it — an entry without one advertises the plugin as a blank line. It is read out
    /// of plugin.json at release time rather than retyped, which keeps this file from becoming a fourth
    /// copy of the same string.
    /// </summary>
    [Fact]
    public void The_release_notes_describe_the_plugin_the_way_plugin_json_does()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!;
        var release = RunReleaseNotes("v1.2.3", "1.2.3");

        release.Code.ShouldBe(0, release.Diagnostics);

        var entry = release.MarketplaceEntry();
        var description = (string?)entry["description"] ?? string.Empty;
        description.ShouldBe((string?)manifest["description"]);
        ((string?)entry["name"]).ShouldBe((string?)manifest["name"]);

        // The backticks inside the description are the reason it is expanded rather than written inline:
        // bash does not rescan an expanded value, so `docume` stays text instead of becoming a command.
        var literal = description.Contains('`', StringComparison.Ordinal);
        literal.ShouldBeTrue("plugin.json's description lost its backticks, so this no longer proves they survive.");
    }

    /// <summary>
    /// The install line names the version being released and the feed it went to, and no <c>$VAR</c>
    /// survives anywhere in the notes — an unexpanded one is a broken command pasted by whoever installs.
    /// </summary>
    [Fact]
    public void The_release_notes_leave_nothing_unexpanded()
    {
        var release = RunReleaseNotes("v1.2.3", "1.2.3");

        release.Code.ShouldBe(0, release.Diagnostics);
        release.Notes.Contains("--version 1.2.3", StringComparison.Ordinal)
            .ShouldBeTrue($"The install line does not name the version.\n{release.Notes}");
        release.Notes.Contains(Feed, StringComparison.Ordinal)
            .ShouldBeTrue($"The install line does not name the feed.\n{release.Notes}");

        var leftovers = release.Notes.Split('\n').Where(line => line.Contains('$', StringComparison.Ordinal)).ToList();
        leftovers.ShouldBeEmpty($"Unexpanded shell variables reached the release notes:\n{string.Join('\n', leftovers)}");
    }

    /// <summary>
    /// What the release is actually created with: the packages that were packed, resolved rather than left
    /// as a glob, and <c>--verify-tag</c>.
    /// </summary>
    /// <remarks>
    /// The glob is worth pinning because <c>gh</c> is handed <c>"$PACKAGES"/*.nupkg</c> unquoted: if it ever
    /// matched nothing, bash passes the pattern through as a literal filename. And <c>--verify-tag</c> is
    /// the difference between a release of the tag that was pushed and a release of a tag this workflow
    /// invented, which is not something a re-run can take back.
    /// </remarks>
    [Fact]
    public void The_release_uploads_the_packages_it_packed_and_will_not_invent_the_tag()
    {
        var release = RunReleaseNotes("v1.2.3", "1.2.3");

        release.Code.ShouldBe(0, release.Diagnostics);
        release.Argv.ShouldContain("--verify-tag", "The release no longer refuses to invent a missing tag.");
        release.Argv.ShouldContain("--notes-file", "The release stopped attaching the notes it just wrote.");

        var assets = release.Argv.Where(argument => argument.EndsWith(".nupkg", StringComparison.Ordinal)).ToList();
        assets.Count.ShouldBe(2, $"Expected both packages as assets, got: {string.Join(' ', release.Argv)}");

        var unexpanded = release.Argv.Exists(argument => argument.Contains('*', StringComparison.Ordinal));
        unexpanded.ShouldBeFalse("The asset glob reached gh unexpanded, so the release would carry no packages.");
    }

    /// <summary>
    /// A release that already exists is adopted, not fallen over. This is what v0.3.0 cost: a release cut
    /// by hand minutes before the tag push made <c>gh release create</c> exit 1 AFTER both packages had
    /// reached the feed, which took the floating-tag move down with it and left <c>@v0</c> advertising the
    /// previous release while the new one was live.
    /// </summary>
    /// <remarks>
    /// Everything above this step is irreversible by the time it runs — a package on the feed cannot be
    /// unpublished — so the one recoverable failure mode has to be recovered from. It is also what makes a
    /// re-run work: <c>--skip-duplicate</c> already makes the feed push a no-op on a second run, and this
    /// makes the release step one.
    /// </remarks>
    [Fact]
    public void A_release_that_already_exists_is_adopted_rather_than_failing_the_run()
    {
        var release = RunReleaseNotes("v1.2.3", "1.2.3", releaseExists: true);

        release.Code.ShouldBe(0, release.Diagnostics);

        release.Argv.ShouldContain("upload", "The adopt path no longer uploads this run's packages.");
        release.Argv.ShouldContain(
            "--clobber",
            "Without --clobber an asset of the same name from an earlier attempt fails the upload.");
        release.Argv.ShouldNotContain(
            "create",
            "The step still tries to create a release it has just been told exists.");

        var assets = release.Argv.Where(argument => argument.EndsWith(".nupkg", StringComparison.Ordinal)).ToList();
        assets.Count.ShouldBe(2, $"Both packages must reach the existing release: {string.Join(' ', release.Argv)}");

        release.Argv.ShouldContain("--notes-file", "The adopt path stopped writing the notes it built.");
    }

    /// <summary>
    /// The other branch, asserted so the adopt path cannot quietly become the only one. A first release
    /// must still be created, with the guards the create path alone can carry.
    /// </summary>
    [Fact]
    public void A_release_that_does_not_exist_yet_is_created_and_not_uploaded_to()
    {
        var release = RunReleaseNotes("v1.2.3", "1.2.3");

        release.Code.ShouldBe(0, release.Diagnostics);
        release.Argv.ShouldContain("create", "A tag with no release must still get one.");
        release.Argv.ShouldNotContain(
            "upload",
            "The step uploaded to a release it had just been told does not exist.");
    }

    // ---- the floating major tag (rule §8.2a) --------------------------------------------------------

    /// <summary>
    /// The move is the last thing a release does, and that ordering is the whole safety argument for it:
    /// the action's own job is <c>dotnet tool restore</c>, so a major ref pointing at a commit whose
    /// packages never reached the feed turns every consumer's docs job red.
    /// </summary>
    [Fact]
    public void The_floating_major_tag_moves_only_after_the_release_exists()
    {
        var steps = Steps();
        var move = steps.FindIndex(step =>
            string.Equals(Value(step, "name", fallback: string.Empty), TagMoveStep, StringComparison.Ordinal));

        move.ShouldBeGreaterThanOrEqualTo(0, $"release.yml has no step named '{TagMoveStep}'.");
        move.ShouldBe(steps.Count - 1, "The tag move must be the final step — every step above it can fail.");
        IndexOfRun("gh release create").ShouldBeLessThan(
            move,
            "The floating major tag must not move before the release it advertises has been cut.");
    }

    /// <summary>
    /// §12's <c>moberghr/docu-me/actions@vN</c> resolving to this release. The major is derived from the
    /// version rather than hardcoded to <c>v1</c>: this repository releases 0.x, and a <c>v1</c> pointing
    /// at 0.1.0 would advertise a 1.x nobody shipped.
    /// </summary>
    [Theory]
    [InlineData("v0.1.0", "0.1.0", "v0")]
    [InlineData("v1.2.3", "1.2.3", "v1")]
    [InlineData("v2.0.0", "2.0.0", "v2")]
    public void The_release_points_the_floating_major_tag_at_itself(string tag, string version, string major)
    {
        var repo = NewThrowawayRepo();
        var move = RunTagMove(tag, version, repo);

        move.Code.ShouldBe(0, move.Diagnostics);
        OriginRev(repo, major).ShouldBe(repo.Head, $"{major} does not point at the released commit.");
    }

    /// <summary>
    /// The force half, which is the only reason this needed authorizing: the second release of a major has
    /// to move a ref that already exists, and a plain push is rejected as non-fast-forward.
    /// </summary>
    [Fact]
    public void The_second_release_of_a_major_moves_the_tag_off_the_first()
    {
        var repo = NewThrowawayRepo(existingMajorTag: "v1");

        OriginRev(repo, "v1").ShouldBe(repo.FirstCommit, "The fixture did not publish the stale tag.");

        var move = RunTagMove("v1.2.3", "1.2.3", repo);

        move.Code.ShouldBe(0, move.Diagnostics);
        OriginRev(repo, "v1").ShouldBe(repo.Head, "v1 was not force-moved to the new release.");
    }

    /// <summary>
    /// A prerelease must not become what <c>@vN</c> resolves to. The tag filter is <c>v*.*.*</c>, which
    /// matches <c>v1.0.0-rc.1</c> as happily as <c>v1.0.0</c>, so this is reachable rather than theoretical.
    /// </summary>
    [Theory]
    [InlineData("v1.0.0-rc.1", "1.0.0-rc.1")]
    [InlineData("v2.0.0-beta", "2.0.0-beta")]
    public void A_prerelease_leaves_the_floating_major_tag_where_it_is(string tag, string version)
    {
        var repo = NewThrowawayRepo(existingMajorTag: "v1");
        var move = RunTagMove(tag, version, repo);

        // Exit 0, not a failure: the packages are already on the feed and the release is already cut, so
        // a red step here would report a successful release as broken.
        move.Code.ShouldBe(0, move.Diagnostics);
        OriginRev(repo, "v1").ShouldBe(repo.FirstCommit, $"A prerelease moved the floating major tag ({tag}).");

        // A green step that did nothing is indistinguishable from a green step that worked, unless it
        // says so — and this is the one release where `@vN` deliberately does not follow the tag.
        move.Summary.ShouldContain(
            "prerelease",
            customMessage: $"The skipped move left no trace in the step summary. Got: '{move.Summary}'");
    }

    /// <summary>
    /// Rule §8.2a is one exception for one tag. Branch history stays untouchable under §8.2, and the step
    /// runs with a force-push in it — so this asserts the blast radius rather than trusting the wording.
    /// </summary>
    [Fact]
    public void Moving_the_tag_never_rewrites_a_branch()
    {
        var repo = NewThrowawayRepo(existingMajorTag: "v1");
        var before = OriginRev(repo, "refs/heads/main");

        var move = RunTagMove("v1.2.3", "1.2.3", repo);

        move.Code.ShouldBe(0, move.Diagnostics);
        OriginRev(repo, "refs/heads/main").ShouldBe(before, "The tag move rewrote a branch on the origin (§8.2).");

        // The static half: the force in that step is spent on one ref, and it is a tag.
        var script = ScriptOf(TagMoveStep);
        var forced = script
            .Split('\n')
            .Where(line => line.Contains("push", StringComparison.Ordinal))
            .ToList();

        forced.Count.ShouldBe(1, $"The tag-move step pushes more than once:\n{string.Join('\n', forced)}");
        forced[0].ShouldContain("$major", customMessage: "The force-push targets something other than the derived tag.");
    }

    /// <summary>
    /// The drift guard for everything above: every executed step is found by name, so a rename would fail
    /// here instead of turning every execution test into a silent skip.
    /// </summary>
    [Fact]
    public void The_executed_steps_are_the_ones_the_workflow_still_ships()
    {
        var names = Steps().Where(step => Run(step).Length is not 0).Select(step => Value(step, "name")).ToList();

        names.ShouldContain(VersionGuardStep, $"release.yml no longer has a step named '{VersionGuardStep}'.");
        names.ShouldContain(CutReleaseStep, $"release.yml no longer has a step named '{CutReleaseStep}'.");
        names.ShouldContain(TagMoveStep, $"release.yml no longer has a step named '{TagMoveStep}'.");
    }

    // ---- fixtures and process plumbing -------------------------------------------------------------
    private const string VersionGuardStep = "Verify the tag is the single version";
    private const string CutReleaseStep = "Cut the GitHub Release";
    private const string TagMoveStep = "Move the floating major tag";

    private static string RepoRoot { get; } = Locate();

    private static string ManifestPath { get; } =
        Path.Combine(RepoRoot, "plugin", ".claude-plugin", "plugin.json");

    /// <summary>
    /// The version the tree currently carries. Read from the manifest rather than restated here, so a
    /// version bump does not have to touch this file.
    /// </summary>
    private static string CurrentVersion()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath));

        return (string?)manifest?["version"]
            ?? throw new InvalidOperationException("plugin.json carries no version for the guard to agree with.");
    }

    /// <summary>
    /// A throwaway tree holding the three files the guard reads, copied from the repository so a passing
    /// case is the shape that actually ships. With <paramref name="bumpTo"/> set, every site except
    /// <paramref name="lagging"/> moves to that version — the half-bumped release.
    /// </summary>
    private string NewVersionTree(string? bumpTo = null, string? lagging = null)
    {
        var tree = NewScratch("release");
        Directory.CreateDirectory(Path.Combine(tree, "plugin", ".claude-plugin"));

        var current = CurrentVersion();

        foreach (var site in VersionSites)
        {
            var relative = site.Replace('/', Path.DirectorySeparatorChar);
            var text = File.ReadAllText(Path.Combine(RepoRoot, relative));

            if (bumpTo is not null && !string.Equals(site, lagging, StringComparison.Ordinal))
            {
                text = text.Replace(current, bumpTo, StringComparison.Ordinal);
            }

            File.WriteAllText(Path.Combine(tree, relative), text);
        }

        return tree;
    }

    /// <summary>Runs the shipped version guard against <paramref name="tree"/>, as a tag push would.</summary>
    private GuardRun RunVersionGuard(string tag, string tree)
    {
        var output = Path.Combine(tree, "github-output");
        File.WriteAllText(output, string.Empty);

        var environment = BaseEnvironment(tree);
        environment["GITHUB_REF_NAME"] = tag;
        environment["GITHUB_OUTPUT"] = output;
        environment["GITHUB_STEP_SUMMARY"] = Path.Combine(tree, "summary.md");

        var result = Shell(ScriptOf(VersionGuardStep), tree, environment);

        return new GuardRun(result.Code, result.Output, result.Error, VersionGuardStep, ReadOutputs(output));
    }

    /// <summary>
    /// Runs the shipped release-notes step with a <c>gh</c> on <c>PATH</c> that only records its argument
    /// list, so the notes file and the asset glob are inspectable without cutting a release.
    /// </summary>
    private ReleaseRun RunReleaseNotes(
        string tag,
        string version,
        string? tree = null,
        bool releaseExists = false)
    {
        var work = tree ?? NewVersionTree();
        var runnerTemp = Path.Combine(work, "runner-temp");
        var packages = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(runnerTemp);
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(packages, $"DocuMe.Cli.{version}.nupkg"), "nupkg");
        File.WriteAllText(Path.Combine(packages, $"DocuMe.Core.{version}.nupkg"), "nupkg");

        var argv = Path.Combine(work, "gh-argv.txt");
        var environment = BaseEnvironment(work);
        environment["PATH"] = $"{StubGh(work, argv, releaseExists)}{Path.PathSeparator}{environment["PATH"]}";
        environment["RUNNER_TEMP"] = runnerTemp;
        environment["TAG"] = tag;
        environment["VERSION"] = version;
        environment["FEED"] = $"https://{Feed}/moberghr/index.json";
        environment["PACKAGES"] = packages;
        environment["GITHUB_REPOSITORY"] = "moberghr/docu-me";
        environment["GITHUB_STEP_SUMMARY"] = Path.Combine(work, "summary.md");

        var result = Shell(ScriptOf(CutReleaseStep), work, environment);
        var notesPath = Path.Combine(runnerTemp, "release-notes.md");

        return new ReleaseRun(
            result.Code,
            result.Output,
            result.Error,
            CutReleaseStep,
            File.Exists(notesPath) ? File.ReadAllText(notesPath) : string.Empty,
            File.Exists(argv) ? File.ReadAllLines(argv).ToList() : []);
    }

    /// <summary>
    /// A throwaway repository with an on-disk origin, two commits, and optionally a stale major tag
    /// already published — never this repository and never its origin, which the step under test
    /// force-pushes to.
    /// </summary>
    /// <remarks>
    /// The origin is a bare repo beside the working copy, so the push is real: a fixture that stubbed
    /// <c>git</c> would prove the step spells a command rather than that the ref ends up where a
    /// consumer's <c>@vN</c> will look for it. No committer identity is configured anywhere, which also
    /// pins the tag as lightweight — an annotated one would fail here exactly as it would on a runner.
    /// </remarks>
    private ThrowawayRepo NewThrowawayRepo(string? existingMajorTag = null)
    {
        var root = NewScratch("tagmove");
        var origin = Path.Combine(root, "origin.git");
        var work = Path.Combine(root, "work");

        Directory.CreateDirectory(work);
        Git(root, "init", "--bare", origin);
        Git(work, "init", "-b", "main");
        Git(work, "remote", "add", "origin", origin);

        Commit(work, "first.txt");
        var first = Git(work, "rev-parse", "HEAD").Trim();

        if (existingMajorTag is not null)
        {
            Git(work, "tag", existingMajorTag);
        }

        Commit(work, "second.txt");
        Git(work, "push", "origin", "main");

        if (existingMajorTag is not null)
        {
            Git(work, "push", "origin", existingMajorTag);
        }

        return new ThrowawayRepo(work, origin, first, Git(work, "rev-parse", "HEAD").Trim());
    }

    /// <summary>Runs the shipped tag-move step against <paramref name="repo"/>, as the release's last step.</summary>
    private static TagMoveRun RunTagMove(string tag, string version, ThrowawayRepo repo)
    {
        var summary = Path.Combine(repo.Work, "summary.md");
        var environment = BaseEnvironment(repo.Work);
        environment["TAG"] = tag;
        environment["VERSION"] = version;
        environment["GITHUB_STEP_SUMMARY"] = summary;

        var result = Shell(ScriptOf(TagMoveStep), repo.Work, environment);

        return new TagMoveRun(
            result.Code,
            result.Output,
            result.Error,
            TagMoveStep,
            File.Exists(summary) ? File.ReadAllText(summary) : string.Empty);
    }

    /// <summary>What <paramref name="reference"/> resolves to on the origin, or null if it is not there.</summary>
    private static string? OriginRev(ThrowawayRepo repo, string reference)
    {
        var result = GitResult(repo.Origin, "rev-parse", reference);

        return result.Code is 0 ? result.Output.Trim() : null;
    }

    /// <summary>A commit adding one file, with the identity passed per-invocation so no config is written.</summary>
    private static void Commit(string repository, string file)
    {
        File.WriteAllText(Path.Combine(repository, file), file);
        Git(repository, "add", "-A");
        Git(
            repository,
            "-c",
            "user.name=DocuMe tests",
            "-c",
            "user.email=tests@example.invalid",
            "commit",
            "-m",
            $"add {file}");
    }

    private static string Git(string repository, params string[] arguments)
    {
        var result = GitResult(repository, arguments);

        result.Code.ShouldBe(
            0,
            $"git {string.Join(' ', arguments)} failed in {repository}:\n{result.Output}\n{result.Error}");

        return result.Output;
    }

    private static ProcessResult GitResult(string repository, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Cleared for the same reason the step's own environment is: a developer's global git config
        // must not be able to supply something a runner would not have.
        info.Environment.Clear();
        info.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        info.Environment["HOME"] = repository;
        info.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        info.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";

        // git 2.54 answers an init that no config level gives a branch name with a multi-line advice
        // on stderr, and this helper nulls every config level, so every init advises. Written into a
        // redirected pipe that is only read after stdout, that advice has wedged real runs on macOS.
        // Pinning the name git used all along keeps init silent without changing what any test sees.
        info.Environment["GIT_CONFIG_COUNT"] = "1";
        info.Environment["GIT_CONFIG_KEY_0"] = "init.defaultBranch";
        info.Environment["GIT_CONFIG_VALUE_0"] = "master";

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("git did not start.");

        // stderr is drained concurrently, never after stdout: with both streams redirected, a
        // sequential read deadlocks the moment the child fills the unread pipe. git 2.54 started
        // writing the init.defaultBranch advice to stderr, and `git init --bare` hung the whole
        // suite here, blocked in write(2) with nobody reading.
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, output, error.GetAwaiter().GetResult());
    }

    /// <summary>The shell of the step named <paramref name="name"/>, extracted from the shipped yaml.</summary>
    private static string ScriptOf(string name)
    {
        var step = Steps().Find(candidate =>
            candidate.Children.Any(child => IsKey(child.Key, "name"))
            && string.Equals(Value(candidate, "name"), name, StringComparison.Ordinal));

        step.ShouldNotBeNull($"release.yml has no step named '{name}'.");

        return Run(step);
    }

    /// <summary>
    /// A <c>gh</c> that records its arguments one per line and does nothing else. One per line so the
    /// <c>*.nupkg</c> glob's expansion is visible: a release that uploads the literal pattern is a release
    /// with no assets on it.
    /// </summary>
    /// <summary>
    /// A <c>gh</c> that records every invocation and answers <c>release view</c> the way the runner would.
    /// </summary>
    /// <param name="releaseExists">
    /// What <c>gh release view</c> reports. The step branches on it, and the two branches are different
    /// commands, so a stub that always said "found" or always said "missing" could only ever test one.
    /// </param>
    private static string StubGh(string root, string argv, bool releaseExists = false)
    {
        var bin = Path.Combine(root, "stub-bin");

        // Appends: the step can now call gh more than once, and overwriting would leave only the last.
        var script = $"""
            #!/bin/bash
            printf '%s\n' "$@" >> '{argv}'
            if [ "$1" = "release" ] && [ "$2" = "view" ]; then
              exit {(releaseExists ? 0 : 1)}
            fi
            exit 0
            """;
        var path = CreateFile(bin, "gh", script);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return bin;
    }

    /// <summary>
    /// The environment cleared down to what a runner guarantees. Cleared rather than inherited so a
    /// variable this repository happens to export cannot stand in for one the workflow must set itself.
    /// </summary>
    private static Dictionary<string, string> BaseEnvironment(string home)
        => new(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = home,
        };

    /// <summary>The <c>key=value</c> lines a step appended to <c>$GITHUB_OUTPUT</c>.</summary>
    private static Dictionary<string, string> ReadOutputs(string path)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.Exists(path) ? File.ReadAllLines(path) : [])
        {
            var parts = line.Split('=', 2);

            if (parts.Length is 2)
            {
                outputs[parts[0]] = parts[1];
            }
        }

        return outputs;
    }

    private static ProcessResult Shell(string script, string workingDirectory, Dictionary<string, string> environment)
    {
        var path = CreateFile(workingDirectory, ".step.sh", script);
        var info = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(path);
        info.Environment.Clear();

        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("bash did not start.");

        // Concurrent for the reason GitResult states: a sequential double read deadlocks once the
        // child fills the pipe nobody is reading yet.
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, output, error.GetAwaiter().GetResult());
    }

    private static string CreateFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);

        return path;
    }

    private string NewScratch(string prefix)
    {
        var directory = Directory.CreateTempSubdirectory($"docume-{prefix}").FullName;
        _scratch.Add(directory);

        return directory;
    }

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

    /// <summary>As <see cref="Value(YamlMappingNode, string)"/>, but for keys a step need not carry.</summary>
    private static string Value(YamlMappingNode parent, string key, string fallback)
    {
        var child = parent.Children.FirstOrDefault(candidate => IsKey(candidate.Key, key)).Value;

        return child is null ? fallback : Scalar(child);
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

    private sealed record ProcessResult(int Code, string Output, string Error);

    /// <summary>
    /// A disposable git repository and the bare origin it pushes to, with the two commits the tag move is
    /// asserted against: <see cref="FirstCommit"/> is where a stale major tag sits, <see cref="Head"/> is
    /// the release.
    /// </summary>
    private sealed record ThrowawayRepo(string Work, string Origin, string FirstCommit, string Head);

    private abstract record StepRun(int Code, string Output, string Error, string Step)
    {
        /// <summary>Everything a failure needs, since the interesting half is usually on stderr.</summary>
        internal string Diagnostics => $"""
            "{Step}" exited {Code}.
            stdout: {Output}
            stderr: {Error}
            """;
    }

    private sealed record GuardRun(
        int Code, string Output, string Error, string Step, Dictionary<string, string> Outputs)
        : StepRun(Code, Output, Error, Step)
    {
        /// <summary>
        /// The <c>::error::</c> annotations the step wrote. These are the whole user interface of a refused
        /// release: the run is red on a tag, and the annotation is what says which file to bump.
        /// </summary>
        internal List<string> Annotations => Output
            .Split('\n')
            .Where(line => line.StartsWith("::error::", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// The tag move, plus the step summary it wrote. The summary is the only trace this step leaves on a
    /// green run, so it is also the only place a skipped move can say it skipped and why.
    /// </summary>
    private sealed record TagMoveRun(int Code, string Output, string Error, string Step, string Summary)
        : StepRun(Code, Output, Error, Step);

    private sealed record ReleaseRun(
        int Code, string Output, string Error, string Step, string Notes, List<string> Argv)
        : StepRun(Code, Output, Error, Step)
    {
        /// <summary>
        /// The json entry out of the release notes, parsed. Parsing is itself the assertion: this block is
        /// pasted into another repository's <c>marketplace.json</c>, so json that only looks right is the
        /// failure mode, and it used to be hand-interpolated.
        /// </summary>
        internal JsonNode MarketplaceEntry()
        {
            const string Fence = "```json";
            var start = Notes.IndexOf(Fence, StringComparison.Ordinal);

            start.ShouldBeGreaterThanOrEqualTo(0, $"The release notes carry no json entry (§12):\n{Notes}");

            var body = Notes[(start + Fence.Length)..];
            var end = body.IndexOf("```", StringComparison.Ordinal);

            end.ShouldBeGreaterThanOrEqualTo(0, $"The marketplace entry's json fence is never closed:\n{Notes}");

            var node = JsonNode.Parse(body[..end]);

            node.ShouldNotBeNull($"The marketplace entry parsed as null:\n{body[..end]}");

            return node;
        }
    }
}
