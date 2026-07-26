using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using YamlDotNet.RepresentationModel;

namespace DocuMe.Core.Tests.Build;

/// <summary>
/// <c>tools/verify-analyzers.sh</c> and its wiring into CI: the check that the four house analyzer
/// packs still <em>execute</em>, which <see cref="BuildStandardsTests"/> structurally cannot say.
/// </summary>
/// <remarks>
/// <para>
/// The gap it fills is a quiet one. <see cref="BuildStandardsTests"/> reads configuration, so it
/// catches a pack that was removed, relaxed or silenced. It cannot catch a pack that is configured
/// perfectly and does not run — a package that resolves but fails to load, an analyzer built against
/// a Roslyn the SDK no longer ships. That failure emits no diagnostic of its own, so the build stays
/// green, reports zero warnings, and enforces nothing. <c>/p:ReportAnalyzer=true</c> is the only
/// thing that says which analyzer assemblies actually ran.
/// </para>
/// <para>
/// Why the check is a shell script in CI and not a test here: it needs a <c>--no-incremental</c>
/// rebuild of the very assemblies the test host has loaded, which is not something to do from inside
/// <c>dotnet test</c>. What this class owns instead is everything about that script a test <em>can</em>
/// hold — that it is wired into the job that builds, that it is executable, that its expected pack
/// list cannot drift from <c>Directory.Build.props</c>, and that it actually goes red when a pack
/// stops appearing. The last one is executed, not read: the script runs against a captured report.
/// </para>
/// <para>
/// <c>report-analyzer.sample.log</c> is real MSBuild output, sampled — every analyzer row of every
/// compilation is verbatim, with the per-rule rows underneath thinned to three apiece and the repo
/// path rewritten to a runner's. Refresh it by re-running the build the script runs
/// (<c>dotnet build DocuMe.slnx --no-incremental -m:1 /p:ReportAnalyzer=true -v:d</c>) and sampling
/// it the same way; the sampler is <c>.mtk/paths-118/</c>. Mutation evidence that the script is a
/// real gate, 8/8: <c>.mtk/paths-118/mutate-analyzer-check.py</c>.
/// </para>
/// </remarks>
public sealed partial class AnalyzerExecutionCheckTests
{
    private const string ScriptPath = "tools/verify-analyzers.sh";

    private const string SampleLog = "tests/DocuMe.Core.Tests/Build/report-analyzer.sample.log";

    [Fact]
    public void The_check_expects_exactly_the_analyzer_packs_the_build_applies()
    {
        // Three copies of one list would otherwise drift: Directory.Build.props applies the packs,
        // BuildStandardsTests asserts they are referenced, and the script greps for the assemblies
        // they ship. A fifth pack added to the build would be checked by nobody.
        var applied = XDocument
            .Load(Path.Combine(RepoRoot, "Directory.Build.props"))
            .Descendants("PackageReference")
            .Where(reference => string.Equals(
                reference.Attribute("PrivateAssets")?.Value, "all", StringComparison.OrdinalIgnoreCase))
            .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        applied.ShouldNotBeEmpty(
            "Directory.Build.props applies no analyzer pack at all, so this comparison proves nothing.");

        var expected = ExpectedPacks();

        expected.ShouldNotBeEmpty($"{ScriptPath} names no packs, so it would pass on any build.");

        const string message = $"{ScriptPath} checks a different set of packs than "
            + "Directory.Build.props applies. Add the new pack to EXPECTED_PACKS as `<package "
            + "id>=<analyzer assembly name>`; the two halves are not always the same string.";

        expected.Keys.Order(StringComparer.Ordinal)
            .ShouldBe(applied.Order(StringComparer.Ordinal), message);
    }

    [Fact]
    public void Ci_runs_the_check_in_the_job_that_builds_the_solution()
    {
        var jobs = Mapping(CiRoot(), "jobs");

        var building = jobs.Children
            .Where(job => Runs(job.Value).Any(run => run.Contains("dotnet build", StringComparison.Ordinal)))
            .ToList();

        building.ShouldHaveSingleItem(
            "CI no longer has exactly one job that builds the solution, so this assertion no longer "
            + "knows where the analyzer check belongs.");

        var runs = Runs(building[0].Value).ToList();

        var message = $"The '{Scalar(building[0].Key)}' job builds the solution but never runs "
            + $"{ScriptPath}, so nothing in CI would notice an analyzer pack that stopped executing.";

        runs.ShouldContain(run => run.Contains(ScriptPath, StringComparison.Ordinal), message);
    }

    [Fact]
    public void The_check_is_executable_so_ci_can_invoke_it_by_path()
    {
        // ci.yml spells the step `run: tools/verify-analyzers.sh`. Without the mode bit committed
        // that is a permission-denied at the point the check was supposed to protect.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(Path.Combine(RepoRoot, ScriptPath));

        mode.HasFlag(UnixFileMode.UserExecute).ShouldBeTrue(
            $"{ScriptPath} is not executable ({mode}). `git update-index --chmod=+x {ScriptPath}`.");
    }

    [Fact]
    public void The_check_passes_on_a_report_where_every_pack_ran()
    {
        var result = RunCheck(Path.Combine(RepoRoot, SampleLog));

        result.Code.ShouldBe(0, result.Diagnostics);

        // Vacuous-pass guard: a check that found no compilation to look at would also exit 0 if the
        // "no report at all" branch were ever removed.
        foreach (var project in SolutionProjects())
        {
            var message = $"{SampleLog} carries no report for {project}, so the sample no longer "
                + "covers the solution. Recapture it.";

            result.Output.ShouldContain(project, customMessage: message);
        }
    }

    [Fact]
    public void The_check_goes_red_when_one_pack_stops_running_in_one_compilation()
    {
        // The failure this whole thing exists for, and the hardest one to spot by eye: the pack is
        // still referenced, still configured, still runs everywhere else, and has quietly stopped
        // running in one project.
        var sample = File.ReadAllLines(Path.Combine(RepoRoot, SampleLog));
        var assembly = ExpectedPacks().Values.First();
        var dropped = false;

        var mutated = sample
            .Where(line =>
            {
                if (dropped || !line.Contains($"{assembly}, Version=", StringComparison.Ordinal))
                {
                    return true;
                }

                dropped = true;

                return false;
            })
            .ToList();

        dropped.ShouldBeTrue($"{SampleLog} never mentions {assembly}, so nothing was mutated.");

        var path = Path.Combine(Path.GetTempPath(), $"docume-analyzer-{Guid.NewGuid():N}.log");
        File.WriteAllLines(path, mutated);

        try
        {
            var result = RunCheck(path);
            var passed = $"{ScriptPath} passed a report in which {assembly} did not run:\n"
                + result.Diagnostics;
            var unnamed = "The failure has to name the pack that stopped running; a bare non-zero "
                + $"exit sends the next reader back to the raw report.\n{result.Diagnostics}";

            result.Code.ShouldNotBe(0, passed);
            result.Error.ShouldContain(assembly, customMessage: unnamed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The script's <c>EXPECTED_PACKS</c> array, read as <c>package id → analyzer assembly</c>.
    /// </summary>
    private static Dictionary<string, string> ExpectedPacks()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, ScriptPath));
        var array = PackArray().Match(text);

        array.Success.ShouldBeTrue(
            $"{ScriptPath} no longer declares an EXPECTED_PACKS=( … ) array, so this class is "
            + "reading nothing.");

        return PackEntry()
            .Matches(array.Groups["body"].Value)
            .ToDictionary(
                match => match.Groups["package"].Value,
                match => match.Groups["assembly"].Value,
                StringComparer.Ordinal);
    }

    private static IEnumerable<string> SolutionProjects() => ProjectPath()
        .Matches(File.ReadAllText(Path.Combine(RepoRoot, "DocuMe.slnx")))
        .Select(match => match.Groups["path"].Value.Replace('\\', '/').Split('/')[^1]);

    private static CheckRun RunCheck(string log)
    {
        var info = new ProcessStartInfo("bash")
        {
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add(Path.Combine(RepoRoot, ScriptPath));
        info.ArgumentList.Add("--log");
        info.ArgumentList.Add(log);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("bash did not start.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new CheckRun(process.ExitCode, output, error);
    }

    private static YamlMappingNode CiRoot()
    {
        var stream = new YamlStream();
        using var reader = new StringReader(
            File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml")));

        stream.Load(reader);

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    /// <summary>The <c>run:</c> script of every step of <paramref name="job"/>.</summary>
    private static IEnumerable<string> Runs(YamlNode job)
    {
        if (((YamlMappingNode)job).Children
            .FirstOrDefault(child => IsKey(child.Key, "steps")).Value is not YamlSequenceNode steps)
        {
            yield break;
        }

        foreach (var step in steps.Children.OfType<YamlMappingNode>())
        {
            if (step.Children.FirstOrDefault(child => IsKey(child.Key, "run")).Value
                is YamlScalarNode run)
            {
                yield return run.Value ?? string.Empty;
            }
        }
    }

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
        => (YamlMappingNode)parent.Children.Single(child => IsKey(child.Key, key)).Value;

    private static bool IsKey(YamlNode node, string name)
        => string.Equals(Scalar(node), name, StringComparison.Ordinal);

    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value ?? string.Empty;

    [GeneratedRegex(
        @"EXPECTED_PACKS=\((?<body>[^)]*)\)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackArray();

    [GeneratedRegex(
        @"^\s*""(?<package>[^=""]+)=(?<assembly>[^""]+)""\s*$",
        RegexOptions.ExplicitCapture | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackEntry();

    [GeneratedRegex(
        @"<Project\s+Path=""(?<path>[^""]+)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProjectPath();

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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the repo files cannot be found.");
    }

    private sealed record CheckRun(int Code, string Output, string Error)
    {
        internal string Diagnostics => $"""
            verify-analyzers.sh exited {Code}.
            stdout: {Output}
            stderr: {Error}
            """;
    }
}
