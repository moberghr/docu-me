using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;

namespace DocuMe.Core.Tests.Build;

/// <summary>
/// That every external toolchain this repository resolves at build time is either pinned, or floating
/// by a declaration that states why.
/// </summary>
/// <remarks>
/// <para>
/// This class exists because the invariant was broken and cost a red CI. Run 30283057704 — the first
/// push this repository ever made — failed <c>build-test</c> with two CA1875 errors on code nobody had
/// touched, while <c>dotnet build</c> on the machine that wrote it reported 0 warnings and 0 errors.
/// The runner's own log names the cause: it resolved SDK 10.0.302, this machine has 10.0.100, and
/// <c>global.json</c> asks for 10.0.100 with <c>rollForward: latestFeature</c>, which permits the whole
/// 10.0.3xx band. <c>Directory.Build.props</c> then sets <c>AnalysisLevel</c> to
/// <c>latest-recommended</c>, so the set of enabled rules is whatever the resolved SDK recommends, and
/// <c>TreatWarningsAsErrors</c> turns each new one into a build error. A newer feature band therefore
/// reddens CI with no code change and no local signal.
/// </para>
/// <para>
/// What makes it worth a test rather than a comment is that the repository already applies the
/// opposite rule everywhere else, deliberately and in writing. Every NuGet version is pinned centrally.
/// <c>ci.yml</c> pins the Claude Code CLI to an exact release, and says why: "so a Claude Code release
/// cannot turn this red on an untouched manifest" — precisely the failure the SDK band then delivered.
/// The scaffolded consumer tool manifest is written with <c>rollForward: false</c>. Node is pinned and
/// <see cref="CiMermaidToolchainTests"/> holds it there. The SDK feature band is the one exception, and
/// nothing anywhere said so out loud.
/// </para>
/// <para>
/// The honest limit, stated because the failure it describes is exactly the kind this class cannot see:
/// these assertions read <em>declarations</em>, so they prove what a toolchain is allowed to resolve to,
/// never what it did resolve to. No test running on 10.0.100 can discover a rule that ships in 10.0.302.
/// That gap is CI's to close, and the only thing that closes it locally is pinning the band — which is
/// an open decision (<c>state.json -&gt; decisions.analyzerBandDrift</c>) and not this class's to make.
/// </para>
/// <para>
/// Mutation-checked, one cell per branch including the vacuity refusal:
/// <c>tools/loop/mutate-toolchain-pinning.py</c>.
/// </para>
/// </remarks>
public sealed partial class ToolchainPinningTests
{
    /// <summary>
    /// The rollForward policy <c>global.json</c> carries today. Any of MSBuild's band-widening
    /// policies would do; this is the measured one, and the tripwire below asserts it verbatim.
    /// </summary>
    private const string FloatingRollForward = "latestFeature";

    /// <summary>
    /// The <c>AnalysisLevel</c> <c>Directory.Build.props</c> carries today. The <c>latest</c> prefix is
    /// the floating half: a version in its place (<c>10.0-recommended</c> is accepted by this SDK's
    /// analyzer targets, measured) would hold the rule set still while the SDK moved.
    /// </summary>
    private const string FloatingAnalysisLevel = "latest-recommended";

    /// <summary>The CI run whose log is the evidence for every claim in this class's remarks.</summary>
    private const string EvidenceRun = "30283057704";

    /// <summary>How far above an install step the pin-me instruction may sit and still be found.</summary>
    private const int InstructionWindow = 8;

    /// <summary>Directories holding workflow-shaped YAML that can install a global npm package.</summary>
    /// <remarks>
    /// Paired with the tree by <see cref="Every_workflow_shaped_yaml_in_the_tree_is_one_this_scan_reads"/>,
    /// because this list is the bound on every fact in the class and was compared against nothing until
    /// iter194: <see cref="WorkflowFiles"/> skips a root that no longer exists with a bare
    /// <c>continue</c>, and a run step outside these three was never anybody's business.
    /// </remarks>
    private static readonly string[] WorkflowDirectories =
    [
        Path.Combine(".github", "workflows"),
        Path.Combine("templates", "workflows"),
        "actions",
    ];

    /// <summary>The prefix of this repository's own CI, as opposed to the templates it ships.</summary>
    private const string OwnWorkflows = ".github/";

    /// <summary>Build output, scratch and vendored trees: YAML there is nothing this repository runs.</summary>
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.Ordinal) { ".git", ".mtk", "bin", "obj", "node_modules" };

    /// <summary>
    /// Global installs that are deliberately unpinned, and why. Both are shipped templates whose
    /// comment tells the consumer to pick the pin, which is a different thing from this repository
    /// leaving its own toolchain floating: the consumer owns that runner and that write token.
    /// </summary>
    /// <remarks>
    /// Paired both ways below. An entry that stops being floating is a stale declaration — it exempts
    /// nothing and hides that the tree changed — and a floating install with no entry is the defect
    /// this class is named after.
    /// </remarks>
    private static readonly Dictionary<string, string> FloatingByDesign = new(StringComparer.Ordinal)
    {
        ["templates/workflows/docs-feedback.claude.yml"] =
            "shipped template; the consumer picks the pin, and the step's own comment says so",
        ["templates/workflows/docs-feedback.copilot.yml"] =
            "shipped template; the consumer picks the pin, and the step's own comment says so",
        ["templates/workflows/docs-refresh.claude.yml"] =
            "shipped template; the consumer picks the pin, and the step's own comment says so",
        ["templates/workflows/docs-refresh.copilot.yml"] =
            "shipped template; the consumer picks the pin, and the step's own comment says so",
    };

    [Fact]
    public void The_analyzer_rule_set_still_follows_whatever_sdk_the_runner_resolves()
    {
        // A TRIPWIRE ON AN OPEN DECISION, NOT A DEFECT REPORT. It asserts the hazard is still exactly
        // where it was measured, so settling decisions.analyzerBandDrift cannot happen quietly and
        // cannot happen without this class being revisited in the same change.
        var rollForward = Sdk()?["rollForward"]?.GetValue<string>();
        var analysisLevel = Property("AnalysisLevel");

        var message = $"global.json rollForward is '{rollForward}' and Directory.Build.props "
            + $"AnalysisLevel is '{analysisLevel}'; this class was written against "
            + $"'{FloatingRollForward}' and '{FloatingAnalysisLevel}'. If you have just pinned one of "
            + $"them, that IS state.json -> decisions.analyzerBandDrift being settled: delete this "
            + $"test and its cells in tools/loop/mutate-toolchain-pinning.py in the same change. If "
            + $"you have not, a third value has appeared and the analysis in CI run {EvidenceRun} no "
            + "longer describes what this build does.";

        rollForward.ShouldBe(FloatingRollForward, message);
        analysisLevel.ShouldBe(FloatingAnalysisLevel, message);
    }

    [Fact]
    public void This_repositorys_own_ci_installs_no_floating_global_package()
    {
        var own = GlobalInstalls()
            .Where(install => install.File.StartsWith(OwnWorkflows, StringComparison.Ordinal))
            .ToList();

        // ANTI-VACUITY, AND IT HAS TO BE THIS FACT'S OWN. The class's other guard counts the UNION of
        // the three scan roots, and the two shipped templates hold that union up by themselves — so a
        // `.github/` slice matching nothing passes here while asserting nothing whatsoever. Measured
        // rather than argued: respelling ci.yml's step as `npm i -g …@latest`, an ordinary spelling
        // this scan's regex does not take, left the whole suite green with a floating install sitting
        // in this repository's own CI.
        own.ShouldNotBeEmpty(
            "No global install was found under " + OwnWorkflows + ". This repository has installed "
            + "the Claude Code CLI globally in ci.yml since that job shipped, so zero matches means "
            + "the step is spelled some way this scan does not read — `npm i -g`, a composite "
            + "action, a setup step — and not that nothing floats. If CI has genuinely stopped "
            + "installing global packages, delete this fact in that change rather than leaving it "
            + "green over an empty list.");

        var floating = own
            .Where(install => !install.IsPinned)
            .Select(install => $"{install.File}:{install.Line} installs {install.Package}@{install.Reference}")
            .ToList();

        floating.ShouldBeEmpty(
            "A floating global install in this repository's own CI is unattended drift with nobody to "
            + "notice it: the job runs on push, against this repository's token, and the thing it "
            + "installs can change between two runs of the same commit. That is the argument ci.yml "
            + "already makes for the Claude Code CLI pin, in a comment, next to the step.");
    }

    [Fact]
    public void Every_floating_global_install_is_declared_with_its_reason()
    {
        var installs = GlobalInstalls();

        // ANTI-VACUITY. Every direction below filters this list, so a renamed command or a restructured
        // workflow set would turn the whole class green while asserting nothing whatsoever about
        // pinning. There are three global installs in the tree and there has never been none.
        installs.ShouldNotBeEmpty(
            "No `npm install -g` was found anywhere under "
            + string.Join(", ", WorkflowDirectories)
            + ". Either the install step is spelled differently now — in which case this class is "
            + "reading for a command nothing runs — or the scan is broken. It is not evidence that "
            + "nothing floats.");

        var undeclared = installs
            .Where(install => !install.IsPinned)
            .Where(install => !FloatingByDesign.ContainsKey(install.File))
            .Select(install => $"{install.File}:{install.Line} installs {install.Package}@{install.Reference}")
            .ToList();

        undeclared.ShouldBeEmpty(
            "Pin it to an exact version, or declare it in FloatingByDesign with the reason it may "
            + "float. An undeclared floating toolchain is how CI run " + EvidenceRun + " went red on "
            + "code nobody had touched.");

        var floatingFiles = installs
            .Where(install => !install.IsPinned)
            .Select(install => install.File)
            .ToHashSet(StringComparer.Ordinal);

        var stale = FloatingByDesign.Keys
            .Where(file => !floatingFiles.Contains(file))
            .ToList();

        stale.ShouldBeEmpty(
            "FloatingByDesign declares these files as carrying a deliberately unpinned install and "
            + "they no longer do. A stale declaration exempts nothing and reads as coverage; drop the "
            + "entry in the change that pinned the install.");
    }

    [Fact]
    public void Every_declared_floating_install_still_tells_the_reader_to_pin_it()
    {
        // The declaration above says these float because the CONSUMER picks the pin. That claim is only
        // true while the template actually says so, next to the step, where someone editing it looks.
        // Delete the comment and the entry above becomes an assertion about a promise nothing keeps.
        var silent = GlobalInstalls()
            .Where(install => !install.IsPinned)
            .Where(install => FloatingByDesign.ContainsKey(install.File))
            .Where(install => !IsPrecededByPinInstruction(install))
            .Select(install => $"{install.File}:{install.Line}")
            .ToList();

        silent.ShouldBeEmpty(
            $"FloatingByDesign exempts these because the template tells the consumer to pin the "
            + $"version, but no comment within {InstructionWindow} lines above the step mentions "
            + "pinning. Restore the instruction, or pin the install and drop the declaration.");
    }

    /// <summary>
    /// The scan's own bounds, paired with the tree in both directions: every declared root names a
    /// directory that exists, and every YAML in the tree that runs a step is one the scan reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every fact in this class is a claim about three directories and one file extension, and
    /// until iter194 nothing compared either against the tree.</strong> The failure is silent from both
    /// sides. <see cref="WorkflowFiles"/> passes over a declared root that has moved with a bare
    /// <c>continue</c>, so the class keeps its names and covers less; and a run step that lands outside
    /// those bounds — a composite action under <c>.github/actions/</c>, a workflow written
    /// <c>.yaml</c>, which GitHub reads and the <c>*.yml</c> enumeration does not — is invisible to a
    /// class whose whole subject is that nothing floats anywhere.
    /// </para>
    /// <para>
    /// Measured, one mutation per bound, full suite each: dropping <c>"actions"</c> from the list,
    /// planting a floating global install in a new <c>.github/actions/</c> composite action, and
    /// planting one in a <c>.yaml</c> workflow inside a declared root all left 1,452 tests green.
    /// </para>
    /// <para>
    /// The population is YAML that <em>runs a step</em>, not every YAML file. A config the tree merely
    /// declares — a dependabot manifest, a schema — cannot carry an <c>npm install -g</c>, so pulling it
    /// in would make this fact fail for files no fact in the class has an opinion about.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_workflow_shaped_yaml_in_the_tree_is_one_this_scan_reads()
    {
        var stale = WorkflowDirectories
            .Where(directory => !Directory.Exists(Path.Combine(RepoRoot, directory)))
            .ToList();

        stale.ShouldBeEmpty(
            "WorkflowDirectories declares a scan root that does not exist, and WorkflowFiles skips "
            + "one of those without a word. Every fact in this class then covers less than its name "
            + "says. Repoint the entry, or drop it in the same change that removed the directory.");

        var read = WorkflowFiles()
            .Select(candidate => candidate.File)
            .ToHashSet(StringComparer.Ordinal);

        var unread = RunStepYaml()
            .Where(file => !read.Contains(file))
            .ToList();

        unread.ShouldBeEmpty(
            "These files run steps and nothing in this class can see them: the scan reads *.yml under "
            + string.Join(", ", WorkflowDirectories)
            + " and nowhere else. A step free to `npm install -g` outside those bounds makes "
            + "\"no floating global install\" a claim about three directories rather than about this "
            + "repository. Widen the root — or the extension — in the same change that added the file.");
    }

    private sealed record Install(string File, int Line, string Package, string Reference)
    {
        /// <summary>
        /// An exact release, which is the only reference that resolves to the same bytes twice. A range
        /// operator, a dist-tag (<c>latest</c>, <c>next</c>) or a bare major all float.
        /// </summary>
        public bool IsPinned => ExactVersion().IsMatch(Reference);
    }

    /// <summary>
    /// Every <c>npm install -g</c> across the workflow-shaped YAML in the tree, with the file, the
    /// 1-based line and the package spec split at the reference.
    /// </summary>
    private static List<Install> GlobalInstalls()
    {
        var installs = new List<Install>();

        foreach (var (file, lines) in WorkflowFiles())
        {
            for (var index = 0; index < lines.Length; index++)
            {
                var match = GlobalInstall().Match(lines[index]);

                if (!match.Success)
                {
                    continue;
                }

                var spec = match.Groups["spec"].Value;
                var separator = spec.LastIndexOf('@');

                // A scoped name starts with '@', so a separator at 0 is the scope and not a reference.
                if (separator <= 0)
                {
                    installs.Add(new Install(file, index + 1, spec, string.Empty));
                    continue;
                }

                installs.Add(new Install(
                    file,
                    index + 1,
                    spec[..separator],
                    spec[(separator + 1)..]));
            }
        }

        return installs;
    }

    private static bool IsPrecededByPinInstruction(Install install)
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, install.File.Replace('/', Path.DirectorySeparatorChar)));
        var first = Math.Max(0, install.Line - 1 - InstructionWindow);

        for (var index = first; index < install.Line - 1; index++)
        {
            var line = lines[index].TrimStart();

            if (line.StartsWith('#') && line.Contains("pin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every YAML file under the workflow directories, keyed by a repository-relative path with forward
    /// slashes so the declaration above reads the same on either platform.
    /// </summary>
    private static IEnumerable<(string File, string[] Lines)> WorkflowFiles()
    {
        foreach (var directory in WorkflowDirectories)
        {
            var root = Path.Combine(RepoRoot, directory);

            if (!Directory.Exists(root))
            {
                continue;
            }

            var files = Directory
                .EnumerateFiles(root, "*.yml", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal);

            foreach (var path in files)
            {
                var relative = Path.GetRelativePath(RepoRoot, path).Replace(Path.DirectorySeparatorChar, '/');

                yield return (relative, File.ReadAllLines(path));
            }
        }
    }

    /// <summary>
    /// Every YAML file in the tree that runs a step, repository-relative with forward slashes so it
    /// compares against <see cref="WorkflowFiles"/> directly. Both extensions, because GitHub reads
    /// both and only one of them is enumerated.
    /// </summary>
    private static IEnumerable<string> RunStepYaml()
    {
        var files = new List<string>();

        Walk(new DirectoryInfo(RepoRoot), string.Empty, files);

        return files
            .Where(file => file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Where(RunsAStep);
    }

    private static bool RunsAStep(string file)
    {
        var path = Path.Combine(RepoRoot, file.Replace('/', Path.DirectorySeparatorChar));

        return File.ReadLines(path)
            .Select(line => line.TrimStart())
            .Any(line => line.StartsWith("run:", StringComparison.Ordinal)
                || line.StartsWith("- run:", StringComparison.Ordinal));
    }

    private static void Walk(DirectoryInfo directory, string prefix, List<string> files)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            files.Add(prefix + file.Name);
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            if (!SkippedDirectories.Contains(child.Name))
            {
                Walk(child, $"{prefix}{child.Name}/", files);
            }
        }
    }

    private static JsonNode? Sdk()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "global.json")))?["sdk"];

    /// <summary>A solution-wide property's value from <c>Directory.Build.props</c>.</summary>
    private static string? Property(string name)
    {
        var props = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"));

        return props.Descendants(name).Select(element => element.Value.Trim()).SingleOrDefault();
    }

    [GeneratedRegex(
        @"npm\s+install\s+(?:-g|--global)\s+(?<spec>\S+)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex GlobalInstall();

    [GeneratedRegex(
        @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ExactVersion();

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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the toolchain declarations cannot be found.");
    }
}
