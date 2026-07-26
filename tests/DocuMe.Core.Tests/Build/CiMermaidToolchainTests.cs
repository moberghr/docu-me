using System.Text.Json.Nodes;
using DocuMe.Core.Tests.Markdown;
using Shouldly;
using YamlDotNet.RepresentationModel;

namespace DocuMe.Core.Tests.Build;

/// <summary>
/// That <c>.github/workflows/ci.yml</c> installs the mermaid renderer's npm dependency before it runs
/// the suite, so the renderer is actually exercised on a runner.
/// </summary>
/// <remarks>
/// <para>
/// The renderer's tests answer "can this machine render?" for themselves: <c>BundledRenderScript</c>
/// looks for <c>node_modules/beautiful-mermaid</c> and every site that needs it calls
/// <c>Assert.SkipUnless</c>. A machine without the package therefore does not go red — it runs seven
/// fewer tests and reports a green suite. That bargain is the right one for a first clone and the
/// wrong one for CI, and <c>node_modules/</c> is gitignored, so every run <c>ci.yml</c> described
/// before iter135 exercised the real renderer exactly zero times.
/// </para>
/// <para>
/// What that costs is specific rather than theoretical. The package is a reimplementation of mermaid
/// that accepts a subset of the dialect — it rejects <c>graph TD;</c> and <c>pie</c>, which is the
/// open question in <c>state.json -&gt; decisions.mermaidDialectGap</c> and the reason one golden case
/// does not publish — and it is pinned by <c>package.json</c> rather than vendored. A pin that moves,
/// or an install step someone deletes, changes what DocuMe can convert; with the renderer absent from
/// CI the first place that would surface is a consumer's wiki.
/// </para>
/// <para>
/// These assertions read the workflow rather than the runner deliberately. A check that only means
/// something where <c>GITHUB_ACTIONS</c> is set is a check nobody runs before pushing, which is the
/// shape iter134 was bitten by; this one fails on a laptop, in the same <c>dotnet test</c> that the
/// step it guards protects.
/// </para>
/// </remarks>
public sealed class CiMermaidToolchainTests
{
    private const string WorkflowPath = ".github/workflows/ci.yml";

    private const string SuiteCommand = "dotnet test";

    private const string InstallCommand = "npm ci";

    private const string SetupNode = "actions/setup-node@";

    /// <summary>
    /// The oldest Node major that can run the renderer. Same floor, and the same reason, as the one
    /// <c>WorkflowTemplateTests</c> holds the scaffolded templates to.
    /// </summary>
    private const int NodeFloor = 20;

    [Fact]
    public void Every_job_that_runs_the_suite_installs_the_renderer_dependency()
    {
        foreach (var job in SuiteJobs())
        {
            var missing = $"The '{job.Name}' job of {WorkflowPath} runs `{SuiteCommand}` without ever "
                + $"running `{InstallCommand}`. node_modules/ is gitignored, so every test that drives "
                + $"the real {BundledRenderScript.Package} skips itself there and the job stays green "
                + "while exercising no renderer at all.";

            job.Runs.ShouldContain(
                run => run.Contains(InstallCommand, StringComparison.Ordinal),
                customMessage: missing);
        }
    }

    [Fact]
    public void The_dependency_is_installed_before_the_suite_runs()
    {
        foreach (var job in SuiteJobs())
        {
            var install = IndexOfRun(job, InstallCommand);
            var suite = IndexOfRun(job, SuiteCommand);

            var absent = $"The '{job.Name}' job of {WorkflowPath} no longer runs `{InstallCommand}`.";

            install.ShouldBeGreaterThanOrEqualTo(0, absent);

            // Order is the whole assertion: an install step that lands after the Test step reads as
            // wired up in a diff and installs the package into a job that has already finished
            // skipping over it.
            var late = $"The '{job.Name}' job of {WorkflowPath} runs `{InstallCommand}` at step "
                + $"{install} and `{SuiteCommand}` at step {suite}, so the suite runs before the "
                + "renderer is installed and skips every test that needs it.";

            install.ShouldBeLessThan(suite, late);
        }
    }

    [Fact]
    public void The_Node_that_runs_the_renderer_is_pinned_and_recent_enough()
    {
        foreach (var job in SuiteJobs())
        {
            var unpinned = $"The '{job.Name}' job of {WorkflowPath} installs the renderer without a "
                + $"single {SetupNode} step pinning a node-version, so the Node rendering the "
                + "diagrams is whatever the runner image happens to ship that week.";

            job.NodeVersions.ShouldHaveSingleItem(customMessage: unpinned);

            var version = job.NodeVersions[0];
            var parsed = int.TryParse(version.Split('.')[0], out var major);

            var unreadable = $"The '{job.Name}' job pins node-version '{version}', which does not "
                + "start with a major version number.";

            parsed.ShouldBeTrue(unreadable);

            var stale = $"The '{job.Name}' job pins Node {version}; the renderer needs {NodeFloor} "
                + "or newer.";

            major.ShouldBeGreaterThanOrEqualTo(NodeFloor, stale);
        }
    }

    [Fact]
    public void What_npm_ci_installs_is_the_package_the_skip_check_looks_for()
    {
        // The two ends this class exists to hold together. `npm ci` installs what package.json
        // declares; the suite skips on what BundledRenderScript probes for. Renaming or dropping the
        // dependency on either side is silent — the install still succeeds, the tests still skip.
        var lockfile = Path.Combine(RepoRoot, "package-lock.json");

        const string unlocked = $"`{InstallCommand}` installs from package-lock.json and fails "
            + "outright without one, so CI would go red at the install step.";

        File.Exists(lockfile).ShouldBeTrue(unlocked);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "package.json")));
        var declared = manifest?["devDependencies"]?.AsObject();

        const string undeclared = $"package.json does not declare {BundledRenderScript.Package} as a "
            + $"devDependency, so `{InstallCommand}` does not install the package the renderer's "
            + "tests look for.";

        declared.ShouldNotBeNull(undeclared);
        declared.ShouldContainKey(BundledRenderScript.Package, customMessage: undeclared);
    }

    private sealed record Job(string Name, IReadOnlyList<string> Runs, IReadOnlyList<string> NodeVersions);

    /// <summary>Every job of the workflow that runs the test suite, asserted to be at least one.</summary>
    private static List<Job> SuiteJobs()
    {
        var jobs = Mapping(Root(), "jobs").Children
            .Select(entry => Read(Scalar(entry.Key), entry.Value))
            .Where(job => job.Runs.Any(run => run.Contains(SuiteCommand, StringComparison.Ordinal)))
            .ToList();

        // Anti-vacuity: every assertion here iterates this list, so a renamed step or a restructured
        // workflow would turn the whole class green by checking nothing.
        const string none = $"No job in {WorkflowPath} runs `{SuiteCommand}`, so nothing in this "
            + "class is asserting anything about where the renderer gets installed.";

        jobs.ShouldNotBeEmpty(none);

        return jobs;
    }

    private static int IndexOfRun(Job job, string command)
    {
        for (var index = 0; index < job.Runs.Count; index++)
        {
            if (job.Runs[index].Contains(command, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static Job Read(string name, YamlNode job)
    {
        var runs = new List<string>();
        var versions = new List<string>();

        foreach (var step in Steps(job))
        {
            if (Value(step, "run") is { } run)
            {
                runs.Add(run);
            }

            if (Value(step, "uses") is not { } uses
                || !uses.StartsWith(SetupNode, StringComparison.Ordinal))
            {
                continue;
            }

            if (step.Children.FirstOrDefault(child => IsKey(child.Key, "with")).Value
                is YamlMappingNode inputs
                && Value(inputs, "node-version") is { } version)
            {
                versions.Add(version);
            }
        }

        return new Job(name, runs, versions);
    }

    private static IEnumerable<YamlMappingNode> Steps(YamlNode job)
    {
        if (((YamlMappingNode)job).Children
            .FirstOrDefault(child => IsKey(child.Key, "steps")).Value is not YamlSequenceNode steps)
        {
            return [];
        }

        return steps.Children.OfType<YamlMappingNode>();
    }

    private static string? Value(YamlMappingNode node, string key)
        => node.Children.FirstOrDefault(child => IsKey(child.Key, key)).Value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static YamlMappingNode Root()
    {
        var stream = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot, WorkflowPath)));

        stream.Load(reader);

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
        => (YamlMappingNode)parent.Children.Single(child => IsKey(child.Key, key)).Value;

    private static bool IsKey(YamlNode node, string name)
        => string.Equals(Scalar(node), name, StringComparison.Ordinal);

    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value ?? string.Empty;

    private static string RepoRoot { get; } = Locate();

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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so {WorkflowPath} cannot be found.");
    }
}
