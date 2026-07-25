using System.Diagnostics;
using System.Text.Json.Nodes;
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

    private const string MermaidPackage = "beautiful-mermaid";

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

    /// <summary>
    /// The default SDK is the one the scaffolded workflows install, which is what this input's own
    /// description tells a consumer it is.
    /// </summary>
    /// <remarks>
    /// A caller who omits <c>dotnet-version</c> takes this default, and a caller who copied
    /// <c>templates/workflows/</c> instead takes the templates'. Nothing coupled them until now — every
    /// <c>with:</c> value in this repository's yaml was unread by any assertion — so bumping one and not
    /// the other would hand two consumers different SDKs for the same tool while the description here
    /// claimed otherwise. Compared against the templates rather than the TFM directly, because
    /// <c>WorkflowTemplateTests</c> already ties those to <c>Directory.Build.props</c>: this asserts the
    /// one link that class cannot see.
    /// </remarks>
    [Fact]
    public void Its_default_SDK_is_the_one_the_scaffolded_workflows_install()
    {
        var scaffolded = ScaffoldedSdkVersion();
        var input = Mapping(Mapping(Root(), "inputs"), "dotnet-version");

        Value(input, "required").ShouldBe(
            "false",
            "An input carrying a default a consumer is told to rely on cannot also be required.");

        var drifted = $"templates/workflows/ installs {scaffolded}, so this action's default must too — its "
            + "own description promises the two match (PLAN.md §12).";

        Value(input, "default").ShouldBe(scaffolded, drifted);
    }

    /// <summary>
    /// The <c>beautiful-mermaid</c> version <c>package.json</c> pins: the one
    /// <c>templates/tools/render-mermaid.mjs</c> is written against, and the single source every place
    /// that names the dependency is held to.
    /// </summary>
    private static string PinnedMermaidVersion()
    {
        var path = Path.Combine(RepoRoot, "package.json");
        var pinned = JsonNode.Parse(File.ReadAllText(path))?["devDependencies"]?[MermaidPackage]
            ?.GetValue<string>();

        pinned.ShouldNotBeNullOrEmpty($"{path} no longer pins {MermaidPackage}.");

        return pinned;
    }

    /// <summary>
    /// The <c>node-version</c> the scaffolded templates install, on the same one-value bar as
    /// <see cref="ScaffoldedSdkVersion"/>.
    /// </summary>
    private static string ScaffoldedNodeVersion() => ScaffoldedWith("node-version");

    /// <summary>
    /// The <c>dotnet-version</c> every template in <c>templates/workflows/</c> installs, asserted to be
    /// one value so this file compares against a single spelling rather than the first one it finds.
    /// </summary>
    private static string ScaffoldedSdkVersion() => ScaffoldedWith("dotnet-version");

    /// <summary>
    /// The single value <c>templates/workflows/</c> gives a <c>with:</c> key, so this file compares
    /// against one spelling rather than the first one it happens to find.
    /// </summary>
    private static string ScaffoldedWith(string key)
    {
        var directory = Path.Combine(RepoRoot, "templates", "workflows");
        var versions = new List<string>();

        foreach (var template in Directory.EnumerateFiles(directory, "*.yml"))
        {
            var lines = File.ReadAllLines(template)
                .Where(line => !line.TrimStart().StartsWith('#'))
                .Where(line => line.Contains($"{key}:", StringComparison.Ordinal));

            versions.AddRange(lines.Select(line => line.Split(':')[1].Trim().Trim('\'', '"')));
        }

        versions.ShouldNotBeEmpty($"No template under {directory} sets {key}.");

        // WorkflowTemplateTests owns "is it the right band"; here the only question is whether there is
        // one band to compare against at all.
        versions.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            1,
            $"templates/workflows/ sets more than one {key}: {string.Join(", ", versions.Distinct(StringComparer.Ordinal))}.");

        return versions[0];
    }

    /// <summary>
    /// The action provisions the diagram renderer for a <c>publish</c>, because it is the second way a
    /// consumer runs one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs-publish.yml</c> shipped without these two steps and could not publish a wiki holding a
    /// single ```mermaid fence: publish shells out to Node once per fence, and neither Node nor
    /// <c>beautiful-mermaid</c> is on a runner by default. This action is the other route to the same
    /// command — its own documented example is <c>args: publish --dry-run</c> — so it inherited the same
    /// bug from the same cause, an install step nobody asserted the absence of.
    /// </para>
    /// <para>
    /// Asserted against the scaffolded template rather than against literals: the two publish paths have
    /// to agree about what a publish needs, and the failure of one to follow the other is the thing worth
    /// catching.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_publish_path_provisions_the_renderer_the_scaffolded_publish_provisions()
    {
        var steps = Steps();
        var node = steps.FindIndex(step =>
            Value(step, "uses", fallback: string.Empty).StartsWith("actions/setup-node@", StringComparison.Ordinal));
        var install = steps.FindIndex(step => Run(step).Contains(MermaidPackage, StringComparison.Ordinal));

        const string missing = "The action installs the SDK but not the diagram renderer, so `args: publish` — "
            + "the example in its own documentation — fails any wiki holding one ```mermaid fence with the "
            + "renderer's exit 3. templates/workflows/docs-publish.yml carries both steps; this is the other "
            + "way a consumer runs publish (PLAN.md §4, §6.2 step 3, §12).";

        node.ShouldBeGreaterThanOrEqualTo(0, missing);
        install.ShouldBeGreaterThanOrEqualTo(0, missing);

        var run = IndexOfRun("dotnet tool run docume");

        node.ShouldBeLessThan(run, "The action sets Node up after the docume run that needs it.");
        install.ShouldBeLessThan(run, "The action installs the renderer's dependency after the run that needs it.");

        // The same version, from the same source of truth, as every other place that names it.
        var pinned = PinnedMermaidVersion();
        var drifted = $"The action installs a {MermaidPackage} other than the {pinned} package.json pins — "
            + "the version templates/tools/render-mermaid.mjs is written against.";

        Text().ShouldContain($"{MermaidPackage}@{pinned}", customMessage: drifted);

        var with = steps[node].Children
            .Where(child => IsKey(child.Key, "with"))
            .Select(child => (YamlMappingNode)child.Value)
            .Single();

        Value(with, "node-version").ShouldBe(
            ScaffoldedNodeVersion(),
            "The action installs a Node other than the templates'.");

        // And it is on by default for a publish. An opt-in renderer is the same bug behind an input:
        // the consumer who hits it is precisely the one who has not read far enough to know it exists.
        var mermaid = Mapping(Mapping(Root(), "inputs"), "mermaid");

        const string optIn = "The renderer is no longer provisioned by default, so `args: publish` fails a "
            + "wiki with a diagram unless the consumer knew to ask for it.";

        Value(mermaid, "required").ShouldBe("false", "An input carrying a working default cannot be required.");
        Value(mermaid, "default").ShouldBe("auto", optIn);
    }

    /// <summary>
    /// Both renderer steps are gated on the decision step, so a <c>drift</c> or a <c>sync</c> does not pay
    /// for a toolchain it never uses.
    /// </summary>
    [Fact]
    public void The_renderer_steps_are_gated_on_the_decision_step()
    {
        var gated = Steps()
            .Where(step =>
                Value(step, "uses", fallback: string.Empty).StartsWith("actions/setup-node@", StringComparison.Ordinal)
                || Run(step).Contains(MermaidPackage, StringComparison.Ordinal))
            .ToList();

        gated.Count.ShouldBe(2, "The renderer is no longer two steps; this test names them by shape.");

        const string ungated = "A renderer step runs unconditionally, so every non-publish invocation "
            + "installs a toolchain it does not use.";

        foreach (var step in gated)
        {
            Value(step, "if", fallback: string.Empty)
                .ShouldContain("steps.renderer.outputs.needed", customMessage: ungated);
        }
    }

    /// <summary>
    /// Every input the action accepts appears in the table on the page that teaches consumers to use it,
    /// and every row of that table is an input the action still has.
    /// </summary>
    /// <remarks>
    /// The wiki page is the only prose describing this action, and its example is the publish this slice
    /// fixed. An input added here and not there is invisible to the reader who needs it — which is how
    /// the renderer went missing in the first place — and a row describing an input that no longer exists
    /// is worse, since a consumer copying it gets an "unexpected input" warning and no behaviour.
    /// </remarks>
    [Fact]
    public void Its_inputs_are_the_ones_the_wiki_documents()
    {
        var page = Path.Combine(RepoRoot, "docs", "wiki", "30-automation", "workflows.md");

        File.Exists(page).ShouldBeTrue($"{page} is where this action is documented.");

        // The page carries several tables; only the one under this header describes the action's inputs.
        var lines = File.ReadAllLines(page).ToList();
        var header = lines.FindIndex(line => line.StartsWith("| Input | Required |", StringComparison.Ordinal));

        header.ShouldBeGreaterThanOrEqualTo(0, $"{page} no longer has the action's input table.");

        var documented = lines
            .Skip(header)
            .TakeWhile(line => line.StartsWith('|'))
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(line => line.Split('`')[1])
            .ToList();

        documented.ShouldNotBeEmpty($"{page}'s input table has no rows.");

        var declared = Mapping(Root(), "inputs").Children.Select(child => Scalar(child.Key)).ToList();

        foreach (var input in declared)
        {
            var undocumented = $"The action accepts `{input}` and {page} never mentions it, so nobody "
                + "reading the docs knows it exists.";

            documented.ShouldContain(input, undocumented);
        }

        foreach (var row in documented.Where(row => !declared.Contains(row, StringComparer.Ordinal)))
        {
            declared.ShouldContain(row, $"{page} documents an input `{row}` the action does not accept.");
        }
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
    /// The decision the two renderer steps are gated on, executed: <c>auto</c> reads the subcommand out of
    /// the argument string, and publish is the only one that renders anything.
    /// </summary>
    /// <remarks>
    /// An expression on the steps themselves cannot do this — <c>args</c> is one string and the condition
    /// is its first word — so the branch is shell, and shell that decides whether a publish works is shell
    /// worth running rather than reading. The leading-space case is why it splits positionally instead of
    /// trimming to the first space.
    /// </remarks>
    [Theory]
    [InlineData("auto", "publish", "true")]
    [InlineData("auto", "publish --dry-run --tree", "true")]
    [InlineData("auto", "   publish --dry-run", "true")]
    [InlineData("auto", "drift --format json", "false")]
    [InlineData("auto", "sync --comments", "false")]
    [InlineData("auto", "status", "false")]
    [InlineData("auto", "", "false")]
    [InlineData("false", "publish --dry-run", "false")]
    [InlineData("true", "drift --format json", "true")]
    public void The_renderer_is_provisioned_exactly_when_a_publish_needs_it(
        string mermaid,
        string args,
        string expected)
    {
        var repo = NewConsumerRepo(withManifest: true);
        var run = RunStep(RendererStep, repo, args, mermaid);

        run.Code.ShouldBe(0, run.Diagnostics);

        var wrong = $"mermaid={mermaid} with args '{args}' decided needed={run.Outputs.GetValueOrDefault("needed")}, "
            + $"expected {expected}.\n{run.Diagnostics}";

        run.Outputs.GetValueOrDefault("needed").ShouldBe(expected, wrong);
    }

    /// <summary>
    /// A <c>mermaid</c> value the step does not recognise stops the action rather than falling through to
    /// "no renderer" — that fallthrough is the shipped bug rebuilt out of a typo.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("True")]
    [InlineData("")]
    public void An_unrecognised_mermaid_value_is_a_refusal_rather_than_a_silent_skip(string mermaid)
    {
        var repo = NewConsumerRepo(withManifest: true);
        var run = RunStep(RendererStep, repo, "publish", mermaid);

        run.Code.ShouldNotBe(0, $"mermaid='{mermaid}' was accepted.\n{run.Diagnostics}");
        run.Outputs.ShouldNotContainKey(
            "needed",
            $"The refusal still wrote a decision, which the steps below would act on.\n{run.Diagnostics}");

        var annotation = run.Output
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("::error::", StringComparison.Ordinal));

        annotation.ShouldNotBeNull($"The refusal wrote no ::error:: annotation. Got:\n{run.Output}");
        annotation.ShouldContain("auto", customMessage: "The refusal does not name the values it accepts.");
    }

    /// <summary>
    /// The other way a restore fails, and the one a consumer cannot diagnose alone: the manifest is
    /// there, but nothing configured the feed the pinned tool lives on.
    /// </summary>
    /// <remarks>
    /// <c>DocuMe.Cli</c> goes to GitHub Packages and never to nuget.org
    /// (<c>.github/workflows/release.yml</c>), and GitHub Packages authenticates every read. So a runner
    /// that adds no feed restores against nuget.org alone and gets "is not found in NuGet feeds
    /// https://api.nuget.org/v3/index.json" — a message naming the feed it did look in, never the one it
    /// should have, and never the token that would have opened it. Passing that through unannotated is
    /// how a consumer's first docs job becomes an unanswerable red check.
    /// </remarks>
    [Fact]
    public void The_restore_step_names_the_feed_when_the_pinned_tool_cannot_be_restored()
    {
        var repo = NewConsumerRepo(withManifest: true);
        var run = RunStep(RestoreStep, repo, dotnetExit: 1);

        run.Code.ShouldNotBe(0, $"A failed restore was allowed to continue.\n{run.Diagnostics}");
        run.Argv.ShouldBe(["tool", "restore"], "The step no longer reaches `dotnet tool restore`.");

        var annotation = run.Output
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("::error::", StringComparison.Ordinal));

        annotation.ShouldNotBeNull($"The failed restore wrote no ::error:: annotation. Got:\n{run.Output}");
        annotation.ShouldContain(
            "packages-token",
            customMessage: "The refusal does not name the input that fixes it.");
        annotation.ShouldContain(
            "read:packages",
            customMessage: "The refusal does not name the scope a cross-org token needs.");
    }

    /// <summary>
    /// The drift guard for the executed steps: they are found by name, so a rename fails here rather
    /// than turning every execution test above into a vacuous pass on an empty script.
    /// </summary>
    [Fact]
    public void The_executed_steps_are_the_ones_the_action_still_ships()
    {
        var scripts = new[] { RestoreStep, DocumeStep, RendererStep }.Select(ScriptAt).ToList();

        scripts.ShouldAllBe(script => script.Length != 0, "A step this file executes no longer has a shell.");
    }

    // ---- fixtures and process plumbing -------------------------------------------------------------

    /// <summary>Position of the executed steps among <see cref="Steps"/>.</summary>
    private static int RestoreStep => IndexOfRun("dotnet tool restore");

    private static int DocumeStep => IndexOfRun("dotnet tool run docume");

    private static int RendererStep => IndexOfRun("needed=$needed");

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
    private static StepRun RunStep(
        int step,
        string repo,
        string? args = null,
        string? mermaid = null,
        int dotnetExit = 0)
    {
        var argv = Path.Combine(repo, "dotnet-argv.txt");
        var outputs = Path.Combine(repo, "github-output.txt");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Cleared down to what a runner guarantees, so a variable this repository happens to export
            // cannot stand in for one the action must set itself.
            ["PATH"] = $"{StubDotnet(repo, argv, dotnetExit)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
            ["HOME"] = repo,

            // The runner creates this file before the step; a step appending to an unset path is the
            // failure being tested for elsewhere, not one to introduce here.
            ["GITHUB_OUTPUT"] = outputs,
        };

        File.WriteAllText(outputs, string.Empty);

        if (args is not null)
        {
            environment["ARGS"] = args;
        }

        if (mermaid is not null)
        {
            environment["MERMAID"] = mermaid;
        }

        var result = Shell(ScriptAt(step), repo, environment);

        return new StepRun(
            result.Code,
            result.Output,
            result.Error,
            File.Exists(argv) ? File.ReadAllLines(argv).ToList() : [],
            StepOutputs(outputs));
    }

    /// <summary>
    /// The <c>key=value</c> lines a step appended to <c>$GITHUB_OUTPUT</c>, which is how the runner
    /// carries a decision from one step to the next step's <c>if:</c>.
    /// </summary>
    private static Dictionary<string, string> StepOutputs(string path)
    {
        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.Exists(path) ? File.ReadAllLines(path) : [])
        {
            var split = line.IndexOf('=', StringComparison.Ordinal);

            if (split > 0)
            {
                written[line[..split]] = line[(split + 1)..];
            }
        }

        return written;
    }

    /// <summary>
    /// A <c>dotnet</c> that records its arguments one per line and exits
    /// <paramref name="exitCode"/>. One per line so the word splitting of <c>$ARGS</c> is visible: an
    /// argument carrying a space would show up as one line. A non-zero code stands in for the failures
    /// the step has to recognise rather than pass on — a restore that found no feed, above all.
    /// </summary>
    private static string StubDotnet(string root, string argv, int exitCode)
    {
        var bin = Path.Combine(root, "stub-bin");
        var script = $"""
            #!/bin/bash
            printf '%s\n' "$@" > '{argv}'
            exit {exitCode}
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

    private sealed record StepRun(
        int Code,
        string Output,
        string Error,
        List<string> Argv,
        Dictionary<string, string> Outputs)
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
