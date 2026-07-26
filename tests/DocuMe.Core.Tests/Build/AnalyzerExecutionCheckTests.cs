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
/// it the same way; the sampler is <c>.mtk/paths-118/</c>.
/// </para>
/// <para>
/// Every branch of the script that can go red is executed here. <c>.mtk/paths-118/mutate-analyzer-check.py</c>
/// proved the same ground 8/8 at iter118, but a harness someone has to remember to run is evidence
/// with a shelf life: the script's own guards were proven once and asserted by nothing, so an edit
/// that deleted the "no report at all" branch would leave the suite green while the CI step started
/// passing vacuously. The harness stays as the place to add a case quickly; these tests are what
/// keeps it honest between runs. That the tests are gates rather than decoration is itself measured,
/// 6/6, by <c>.mtk/paths-119/mutate-script.py</c> — which breaks one branch of the script per case
/// and requires the matching test, and only that test, to go red.
/// </para>
/// </remarks>
public sealed partial class AnalyzerExecutionCheckTests
{
    private const string ScriptPath = "tools/verify-analyzers.sh";

    private const string SampleLog = "tests/DocuMe.Core.Tests/Build/report-analyzer.sample.log";

    /// <summary>A flag the script does not take, spelled close enough to one it does to be a typo.</summary>
    private static readonly string[] UnknownArgument = ["--report"];

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

        var result = RunOnLog(string.Join('\n', mutated));

        var passed = $"{ScriptPath} passed a report in which {assembly} did not run:\n"
            + result.Diagnostics;
        var unnamed = "The failure has to name the pack that stopped running; a bare non-zero "
            + $"exit sends the next reader back to the raw report.\n{result.Diagnostics}";

        result.Code.ShouldNotBe(0, passed);
        result.Error.ShouldContain(assembly, customMessage: unnamed);
    }

    [Fact]
    public void The_check_goes_red_when_the_log_holds_no_report_at_all()
    {
        // The vacuous-pass guard, and the only reason a green run means anything. With
        // /p:ReportAnalyzer=true dropped or the verbosity lowered below -v:d, the log carries no
        // report, every per-pack loop has nothing to iterate over, and a check missing this branch
        // would exit 0 having read nothing at all — the exact silence the script exists to break.
        const string log = """
            Build succeeded.
                0 Warning(s)
                0 Error(s)
            """;

        var result = RunOnLog(log);

        var passed = $"{ScriptPath} passed a log that holds no analyzer report, so a green step no "
            + $"longer says the packs ran.\n{result.Diagnostics}";
        var unnamed = "The failure has to name what went missing from the invocation — the reader "
            + $"is looking at a build that succeeded.\n{result.Diagnostics}";

        result.Code.ShouldNotBe(0, passed);
        result.Error.ShouldContain("ReportAnalyzer", customMessage: unnamed);
    }

    [Fact]
    public void The_check_goes_red_when_a_solution_project_never_compiled()
    {
        // A project quietly dropped from the build analyses nothing, and every compilation that DID
        // run still reports all four packs — so the per-pack loop stays silent and only the
        // solution-versus-log comparison can catch it.
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, SampleLog));
        var last = Array.FindLastIndex(lines, line => LogProject().IsMatch(line));

        const string single = $"{SampleLog} names fewer than two compilations, so dropping the last one "
            + "leaves nothing to compare against. Recapture it.";

        last.ShouldBeGreaterThan(0, single);

        var dropped = Path.GetFileName(
            LogProject().Match(lines[last]).Groups["path"].Value.Replace('\\', '/'));

        var result = RunOnLog(string.Join('\n', lines[..last]));

        var passed = $"{ScriptPath} passed a report missing {dropped} entirely. A project that "
            + $"never compiled was analysed by nothing.\n{result.Diagnostics}";
        var unnamed = $"The failure has to name the project that never compiled.\n{result.Diagnostics}";

        result.Code.ShouldNotBe(0, passed);
        result.Error.ShouldContain(dropped, customMessage: unnamed);
    }

    [Fact]
    public void The_check_reads_a_report_printed_in_the_runners_locale()
    {
        // The committed sample is a comma-locale capture; ubuntu-latest prints "6.813 seconds". The
        // script separates an assembly row from a rule row by ", Version=" precisely so the decimal
        // separator cannot matter — and without this case the suite only ever reads the separator
        // CI does not use.
        var sample = File.ReadAllText(Path.Combine(RepoRoot, SampleLog));
        var invariant = DecimalComma().Replace(sample, "${lead}.${fraction}");

        const string unchanged = $"{SampleLog} holds no decimal commas, so this case converts nothing and "
            + "proves nothing. It was recaptured on a machine whose locale matches CI's.";

        invariant.ShouldNotBe(sample, unchanged);

        var result = RunOnLog(invariant);

        result.Code.ShouldBe(0, result.Diagnostics);
    }

    [Fact]
    public void The_check_reads_a_project_name_msbuild_qualified_with_a_global_property()
    {
        // MSBuild names a project instance `X.csproj::TargetFramework=net10.0` once it carries
        // global properties. The script strips that suffix, and exit 0 is what proves the strip:
        // unstripped, the three reported names match no project in the solution and the
        // never-compiled branch fires on all of them.
        var sample = File.ReadAllText(Path.Combine(RepoRoot, SampleLog));

        var qualified = LogProject()
            .Replace(sample, @"from project ""${path}::TargetFramework=net10.0""");

        const string unchanged = $"{SampleLog} names no project the way MSBuild does, so this case rewrote "
            + "nothing. Recapture it.";

        qualified.ShouldNotBe(sample, unchanged);

        var result = RunOnLog(qualified);

        result.Code.ShouldBe(0, result.Diagnostics);

        var leaked = "The qualifier reached the report, so the project names CI prints cannot be "
            + $"matched against the solution by eye.\n{result.Diagnostics}";

        result.Output.ShouldNotContain("::TargetFramework", customMessage: leaked);
    }

    [Fact]
    public void The_check_tells_its_own_misuse_apart_from_analyzers_that_did_not_run()
    {
        // CI reads the exit code before it reads the text. Exit 1 means "look at the build"; exit 2
        // means "look at the invocation" — a log path that moved, a flag that was renamed. Collapse
        // the two and the next reader hunts an analyzer regression that never happened.
        var absent = RunCheck(
            Path.Combine(Path.GetTempPath(), $"docume-analyzer-{Guid.NewGuid():N}.log"));

        var missing = $"A --log file that is not there is misuse, not a failed analyzer run, and "
            + $"has to exit 2.\n{absent.Diagnostics}";

        absent.Code.ShouldBe(2, missing);

        var unknown = RunScript(UnknownArgument);

        var accepted = $"{ScriptPath} accepted an argument it does not understand instead of "
            + $"exiting 2, so a renamed flag would be silently ignored.\n{unknown.Diagnostics}";

        unknown.Code.ShouldBe(2, accepted);
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

    /// <summary>Runs the check over <paramref name="log"/>, written to a scratch file.</summary>
    private static CheckRun RunOnLog(string log)
    {
        var path = Path.Combine(Path.GetTempPath(), $"docume-analyzer-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, log);

        try
        {
            return RunCheck(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CheckRun RunCheck(string log) => RunScript(["--log", log]);

    private static CheckRun RunScript(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo("bash")
        {
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add(Path.Combine(RepoRoot, ScriptPath));

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

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

    /// <summary>The marker MSBuild prints ~20 lines above each report, naming what it compiled.</summary>
    [GeneratedRegex(
        @"from project ""(?<path>[^""]+)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex LogProject();

    /// <summary>
    /// A decimal comma, which is the only place in the report a comma sits between two digits —
    /// <c>, Version=</c> and the rule lists are all comma-space.
    /// </summary>
    [GeneratedRegex(
        @"(?<lead>[0-9]),(?<fraction>[0-9])",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DecimalComma();

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
