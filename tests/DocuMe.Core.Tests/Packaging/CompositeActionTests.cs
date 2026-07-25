using System.Diagnostics;
using Shouldly;
using YamlDotNet.RepresentationModel;

namespace DocuMe.Core.Tests.Packaging;

/// <summary>
/// The composite action, <c>actions/action.yml</c> (PLAN.md §3, §12): install the repo-pinned CLI and
/// run it, so a consumer writing a docs job of their own does not restate the three lines every
/// scaffolded workflow in <c>templates/workflows/</c> opens with.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this repository consumes this file — it runs in <em>other people's</em> workflows, off a
/// floating <c>@vN</c> ref, which means a mistake in it is discovered by a consumer and fixed by a
/// release. There is no CI job that can catch it either: a composite action only executes when some
/// workflow calls it, so its shell is executed here the way <see cref="ReleaseWorkflowTests"/> executes
/// release.yml's, with a stub <c>dotnet</c> on <c>PATH</c>.
/// </para>
/// <para>
/// Two of the assertions below are the reason this file exists rather than being three lines nobody
/// tests. The <c>args</c> input reaches the shell through the environment, never through
/// <c>${{ }}</c> interpolated into a <c>run:</c> block — a caller passing something derived from an
/// issue title or a page comment (rule §0.2) would otherwise be handing this action a command line.
/// And the missing-manifest guard is the failure a consumer who copied a workflow in by hand actually
/// hits, where the SDK's own wording never mentions DocuMe.
/// </para>
/// </remarks>
public sealed class CompositeActionTests : IDisposable
{
    private const string ToolManifest = ".config/dotnet-tools.json";

    private readonly List<string> _scratch = [];

    public void Dispose()
    {
        foreach (var directory in _scratch.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_action_is_where_a_consumer_ref_points()
    {
        // §3's tree lists `actions/action.yml` and §12 promises `moberghr/docu-me/actions@v1`, which
        // resolves to this exact path — GitHub looks for `action.yml` in the referenced directory and
        // fails the calling workflow if it is anywhere else.
        File.Exists(ActionPath).ShouldBeTrue($"No composite action at {ActionPath} (PLAN.md §3, §12).");
    }

    [Fact]
    public void It_is_a_composite_action_that_installs_then_runs()
    {
        var runs = Mapping(Root(), "runs");

        Value(runs, "using").ShouldBe(
            "composite",
            "§12's action wraps install+run, which only a composite action can do.");

        var restore = IndexOfRun("dotnet tool restore");
        var run = IndexOfRun("dotnet tool run docume");

        restore.ShouldBeGreaterThanOrEqualTo(0, "The action never restores the pinned tool.");
        run.ShouldBeGreaterThanOrEqualTo(0, "The action never runs docume.");

        // `dotnet tool run` on a manifest that was never restored fails, and the failure names the
        // tool rather than the missing restore.
        restore.ShouldBeLessThan(run, "The action must restore the tool manifest before it runs docume.");

        var uses = Steps()
            .Select(step => Value(step, "uses", fallback: string.Empty))
            .Where(value => value.Length is not 0)
            .ToList();

        uses.ShouldContain(
            value => value.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal),
            "The action must install the SDK; a consumer's runner is not guaranteed to have one.");
    }

    [Fact]
    public void It_pins_no_DocuMe_version_of_its_own()
    {
        // The whole reason this action can float on `@v1`. The version comes from the consumer's
        // `.config/dotnet-tools.json`, written by `docume init` off the tool that scaffolded them; an
        // action that also named a version would override that pin, or contradict it.
        var text = Text();

        text.ShouldNotContain(
            "dotnet tool install",
            customMessage: "The action installs a tool version instead of restoring the consumer's pin (§12).");
        text.ShouldNotContain(
            "--version",
            customMessage: "The action names a DocuMe version; the consumer's manifest is the pin.");
    }

    [Fact]
    public void The_args_input_is_required_and_never_reaches_the_shell_through_an_expression()
    {
        var args = Mapping(Mapping(Root(), "inputs"), "args");

        Value(args, "required").ShouldBe("true", "An action that runs `docume` with no arguments has nothing to do.");

        // The injection surface, asserted structurally as well as executed below: a `${{ }}` inside a
        // `run:` block is substituted before bash ever sees the script, so the input becomes shell
        // source rather than an argument. Callers pass values derived from untrusted text (rule §0.2).
        var interpolated = Steps()
            .Select(Run)
            .Where(script => script.Contains("${{", StringComparison.Ordinal))
            .ToList();

        interpolated.ShouldBeEmpty(
            $"An action input is interpolated into a shell script:\n{string.Join("\n---\n", interpolated)}");

        // And the route it does take, so removing the env wiring fails here rather than silently
        // leaving `$ARGS` unset — which under `nounset` would be a red step and under neither would
        // run a bare `docume`.
        Text().ShouldContain(
            "ARGS: ${{ inputs.args }}",
            customMessage: "The args input no longer reaches the run step through the environment.");
    }

    [Fact]
    public void Every_shell_step_names_bash()
    {
        // A composite action step with a `run:` and no `shell:` is not a defaulting-to-bash step: it is
        // a validation error that fails the calling workflow before any of it executes.
        var missing = Steps()
            .Where(step => Run(step).Length is not 0)
            .Where(step => !string.Equals(Value(step, "shell", fallback: string.Empty), "bash", StringComparison.Ordinal))
            .ToList();

        missing.ShouldBeEmpty("A composite action's run step must declare `shell:` — it has no default.");
    }

    // ---- the shell, executed rather than read -------------------------------------------------------

    /// <summary>
    /// The restore step against a repo <c>docume init</c> has scaffolded: the manifest is there, so the
    /// guard passes and <c>dotnet tool restore</c> is what actually runs.
    /// </summary>
    [Fact]
    public void The_restore_step_restores_when_the_repo_carries_a_tool_manifest()
    {
        var repo = NewConsumerRepo(withManifest: true);
        var run = RunStep(RestoreStep, repo);

        run.Code.ShouldBe(0, run.Diagnostics);
        run.Argv.ShouldBe(["tool", "restore"], "The restore step no longer runs `dotnet tool restore`.");
    }

    /// <summary>
    /// The guard. A repo holding these workflows without the manifest they need is what copying them in
    /// by hand rather than running <c>docume init</c> leaves behind, and the SDK's own error for it never
    /// says the word DocuMe.
    /// </summary>
    [Fact]
    public void The_restore_step_refuses_a_repo_with_no_pinned_tool_and_names_the_fix()
    {
        var repo = NewConsumerRepo(withManifest: false);
        var run = RunStep(RestoreStep, repo);

        run.Code.ShouldNotBe(0, $"A repo with no tool manifest was allowed to continue.\n{run.Diagnostics}");
        run.Argv.ShouldBeEmpty("The step ran dotnet anyway, so the guard is not gating anything.");

        var annotation = run.Output
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("::error::", StringComparison.Ordinal));

        annotation.ShouldNotBeNull($"The refusal wrote no ::error:: annotation. Got:\n{run.Output}");
        annotation.ShouldContain("docume init", customMessage: "The refusal does not name the command that fixes it.");
        annotation.ShouldContain(ToolManifest, customMessage: "The refusal does not name the file that is missing.");
    }

    /// <summary>
    /// One input string becomes an argv. This is the behaviour the unquoted expansion is there for, so it
    /// is asserted rather than left to the comment beside it.
    /// </summary>
    [Theory]
    [InlineData("publish --dry-run", new[] { "tool", "run", "docume", "publish", "--dry-run" })]
    [InlineData("drift --format json", new[] { "tool", "run", "docume", "drift", "--format", "json" })]
    [InlineData("status", new[] { "tool", "run", "docume", "status" })]
    public void The_run_step_word_splits_the_input_into_arguments(string args, string[] expected)
    {
        var repo = NewConsumerRepo(withManifest: true);
        var run = RunStep(DocumeStep, repo, args);

        run.Code.ShouldBe(0, run.Diagnostics);
        run.Argv.ShouldBe(expected);
    }

    /// <summary>
    /// The injection case, executed. Bash does not rescan an expanded value for operators, so a
    /// semicolon or a substitution inside <c>args</c> arrives as a literal argument and nothing runs.
    /// Both payloads would create the file if the input were shell source instead of data.
    /// </summary>
    [Theory]
    [InlineData("publish; touch pwned.txt")]
    [InlineData("publish $(touch pwned.txt)")]
    [InlineData("publish `touch pwned.txt`")]
    [InlineData("publish && touch pwned.txt")]
    public void The_run_step_cannot_be_talked_into_running_a_second_command(string args)
    {
        var repo = NewConsumerRepo(withManifest: true);
        var run = RunStep(DocumeStep, repo, args);

        // The assertion is the absence of the side effect, not the exit code: a payload that ran and
        // then failed would still have run.
        File.Exists(Path.Combine(repo, "pwned.txt")).ShouldBeFalse(
            $"`args` was executed as shell rather than passed as arguments: {args}\n{run.Diagnostics}");

        // And it still reached docume as data, so the assertion above is not passing on a step that
        // merely died early. The operator arrives glued to its word — `publish;` is one argument — which
        // is precisely the evidence that bash split the value without reparsing it.
        run.Argv.Take(3).ShouldBe(
            ["tool", "run", "docume"],
            $"The step did not invoke the pinned tool.\n{run.Diagnostics}");
        run.Argv.Count.ShouldBeGreaterThan(3, $"The payload never reached docume at all.\n{run.Diagnostics}");
    }

    /// <summary>
    /// The drift guard for the two executed steps: they are found by name, so a rename fails here rather
    /// than turning every execution test above into a vacuous pass on an empty script.
    /// </summary>
    [Fact]
    public void The_executed_steps_are_the_ones_the_action_still_ships()
    {
        var scripts = new[] { RestoreStep, DocumeStep }.Select(ScriptAt).ToList();

        scripts.ShouldAllBe(script => script.Length != 0, "A step this file executes no longer has a shell.");
    }

    // ---- fixtures and process plumbing -------------------------------------------------------------

    /// <summary>Position of the restore step and of the docume step among <see cref="Steps"/>.</summary>
    private static int RestoreStep => IndexOfRun("dotnet tool restore");

    private static int DocumeStep => IndexOfRun("dotnet tool run docume");

    private static string RepoRoot { get; } = Locate();

    private static string ActionPath { get; } = Path.Combine(RepoRoot, "actions", "action.yml");

    /// <summary>
    /// A consumer repo as <c>docume init</c> leaves it, or without the manifest for the guard case.
    /// </summary>
    /// <remarks>
    /// Written rather than copied from this repository, which has no manifest of its own: DocuMe builds
    /// the tool, it does not consume it. Only the file's presence is what the action gates on — the shape
    /// of what <c>docume init</c> writes belongs to the scaffolding tests, not here.
    /// </remarks>
    private string NewConsumerRepo(bool withManifest)
    {
        var repo = NewScratch("action");

        if (withManifest)
        {
            const string Manifest = """
                {
                  "version": 1,
                  "isRoot": true,
                  "tools": {
                    "docume.cli": {
                      "version": "0.1.0",
                      "commands": ["docume"],
                      "rollForward": false
                    }
                  }
                }
                """;
            var relative = ToolManifest.Replace('/', Path.DirectorySeparatorChar);
            Directory.CreateDirectory(Path.Combine(repo, ".config"));
            File.WriteAllText(Path.Combine(repo, relative), Manifest);
        }

        return repo;
    }

    /// <summary>
    /// Runs the shipped shell of one step against <paramref name="repo"/>, with a <c>dotnet</c> on
    /// <c>PATH</c> that records its argument list and nothing else.
    /// </summary>
    private static StepRun RunStep(int step, string repo, string? args = null)
    {
        var argv = Path.Combine(repo, "dotnet-argv.txt");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Cleared down to what a runner guarantees, so a variable this repository happens to export
            // cannot stand in for one the action must set itself.
            ["PATH"] = $"{StubDotnet(repo, argv)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
            ["HOME"] = repo,
        };

        if (args is not null)
        {
            environment["ARGS"] = args;
        }

        var result = Shell(ScriptAt(step), repo, environment);

        return new StepRun(
            result.Code,
            result.Output,
            result.Error,
            File.Exists(argv) ? File.ReadAllLines(argv).ToList() : []);
    }

    /// <summary>
    /// A <c>dotnet</c> that records its arguments one per line and exits 0. One per line so the word
    /// splitting of <c>$ARGS</c> is visible: an argument carrying a space would show up as one line.
    /// </summary>
    private static string StubDotnet(string root, string argv)
    {
        var bin = Path.Combine(root, "stub-bin");
        var script = $"""
            #!/bin/bash
            printf '%s\n' "$@" > '{argv}'
            exit 0
            """;
        var path = CreateFile(bin, "dotnet", script);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return bin;
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
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, output, error);
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

    private static string Text() => File.ReadAllText(ActionPath);

    private static YamlMappingNode Root()
    {
        var stream = new YamlStream();
        using var reader = new StringReader(Text());

        // Load, not Deserialize: an indentation slip is the failure hand-written yaml actually has, and
        // GitHub reports it as an invalid action only once a consumer's workflow calls this.
        stream.Load(reader);

        stream.Documents.Count.ShouldBe(1, "action.yml should be one yaml document.");

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static List<YamlMappingNode> Steps()
    {
        var steps = (YamlSequenceNode)Mapping(Root(), "runs")
            .Children
            .Single(child => IsKey(child.Key, "steps"))
            .Value;

        return steps.OfType<YamlMappingNode>().ToList();
    }

    private static string ScriptAt(int step)
    {
        var steps = Steps();

        step.ShouldBeInRange(0, steps.Count - 1, "action.yml no longer has the step this test executes.");

        return Run(steps[step]);
    }

    private static string Run(YamlMappingNode step)
    {
        var run = step.Children.FirstOrDefault(child => IsKey(child.Key, "run")).Value;

        return run is null ? string.Empty : Scalar(run);
    }

    private static int IndexOfRun(string fragment)
        => Steps().FindIndex(step => Run(step).Contains(fragment, StringComparison.Ordinal));

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
    {
        var child = parent.Children.FirstOrDefault(candidate => IsKey(candidate.Key, key)).Value;

        child.ShouldNotBeNull($"action.yml has no '{key}'.");

        return (YamlMappingNode)child;
    }

    private static string Value(YamlMappingNode parent, string key)
    {
        var child = parent.Children.FirstOrDefault(candidate => IsKey(candidate.Key, key)).Value;

        child.ShouldNotBeNull($"action.yml has no '{key}'.");

        return Scalar(child);
    }

    private static string Value(YamlMappingNode parent, string key, string fallback)
    {
        var child = parent.Children.FirstOrDefault(candidate => IsKey(candidate.Key, key)).Value;

        return child is null ? fallback : Scalar(child);
    }

    private static bool IsKey(YamlNode node, string name)
        => string.Equals(Scalar(node), name, StringComparison.Ordinal);

    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value ?? string.Empty;

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>: the action ships in the tree and has no
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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so actions/action.yml cannot be found.");
    }

    private sealed record ProcessResult(int Code, string Output, string Error);

    private sealed record StepRun(int Code, string Output, string Error, List<string> Argv)
    {
        /// <summary>Everything a failure needs, since the interesting half is usually on stderr.</summary>
        internal string Diagnostics => $"""
            The step exited {Code}.
            stdout: {Output}
            stderr: {Error}
            argv: {string.Join(' ', Argv)}
            """;
    }
}
