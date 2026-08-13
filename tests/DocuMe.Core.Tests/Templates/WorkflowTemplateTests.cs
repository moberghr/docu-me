using System.Globalization;
using System.Text.Json.Nodes;
using DocuMe.Core.Drift;
using DocuMe.Core.Feedback;
using Shouldly;
using YamlDotNet.RepresentationModel;

namespace DocuMe.Core.Tests.Templates;

/// <summary>
/// The consumer workflow templates in <c>templates/workflows/</c> (PLAN.md §10), asserted as a contract
/// rather than left to a reviewer's eye.
/// </summary>
/// <remarks>
/// <para>
/// A workflow template is code that only ever runs in somebody else's repository, months after it was
/// written, where its first failure is a red check on a contributor's PR. Nothing else in this suite
/// covers it: <c>dotnet build</c> does not read yaml, and the CLI's own tests know nothing about the
/// command lines these files spell.
/// </para>
/// <para>
/// So the assertions are the mistakes that are cheap to make and expensive to find. A flag the CLI does
/// not have (§10's own text writes <c>--base</c>, which never existed — the option is
/// <c>--baseline</c>). A <c>docume</c> step before <c>dotnet tool restore</c>, which runs whatever
/// version the runner image happens to hold, or nothing. A credential inlined instead of read from
/// secrets (rule §1.1). <c>--prune</c> in CI (rule §9.6). And the sticky-comment marker drifting away
/// from <see cref="DriftComment.Marker"/>, which would not fail anything — it would just quietly post a
/// second comment per push forever.
/// </para>
/// </remarks>
public sealed class WorkflowTemplateTests
{
    /// <summary>
    /// Every template file in <c>templates/workflows/</c>, by its own name rather than by the name a
    /// consumer receives.
    /// </summary>
    /// <remarks>
    /// The two are no longer the same thing. A model-running workflow ships one per-rail spelling —
    /// <c>docs-refresh.claude.yml</c>, <c>docs-refresh.copilot.yml</c> — and whichever the rail selects
    /// lands in a consumer repo as <c>docs-refresh.yml</c>. This list names the files on disk so that
    /// every assertion below runs against BOTH rails: a Copilot variant that lost its timeout, leaked a
    /// credential or dropped its permission gate is exactly as broken as a Claude one that did, and a
    /// list of consumer-facing names would have checked only whichever rail happened to be the default.
    /// </remarks>
    private static readonly string[] Templates =
    [
        "docs-drift.yml",
        "docs-drift-pr.yml",
        "docs-publish.yml",
        "docs-sync.yml",
        "docs-refresh.claude.yml",
        "docs-refresh.copilot.yml",
        "docs-feedback.claude.yml",
        "docs-feedback.copilot.yml",
    ];

    /// <summary>The templates that talk to Confluence, and so need both credential variables.</summary>
    private static readonly string[] ConfluenceFacing =
    [
        "docs-drift.yml",
        "docs-publish.yml",
        "docs-sync.yml",
    ];

    /// <summary>
    /// The templates that run a model (§11's headless invocation, <c>claude -p</c> or <c>copilot -p</c>).
    /// Their output is a PR, so none of them writes to Confluence and none holds a Confluence credential.
    /// </summary>
    private static readonly string[] ModelDriven =
    [
        "docs-refresh.claude.yml",
        "docs-refresh.copilot.yml",
        "docs-feedback.claude.yml",
        "docs-feedback.copilot.yml",
    ];

    /// <summary>
    /// GitHub's default job timeout in minutes — what a step with no <c>timeout-minutes</c> of its own
    /// effectively inherits.
    /// </summary>
    private const int JobDefaultTimeoutMinutes = 360;

    /// <summary>
    /// The templates whose only <c>docume</c> invocation happens inside the skill they run, so the literal
    /// command line is not in the yaml.
    /// </summary>
    private static readonly string[] SkillDriven =
    [
        "docs-feedback.claude.yml",
        "docs-feedback.copilot.yml",
    ];

    /// <summary>
    /// The templates that read <c>docume.json</c> to find the wiki root, and the job each does it in.
    /// Five of the six: <c>docs-drift-pr.yml</c> takes its base ref from the pull-request event instead.
    /// </summary>
    private static readonly (string Template, string Job)[] ConfigReaders =
    [
        ("docs-publish.yml", "publish"),
        ("docs-sync.yml", "sync"),
        ("docs-drift.yml", "mark"),
        ("docs-refresh.claude.yml", "refresh"),
        ("docs-refresh.copilot.yml", "refresh"),
        ("docs-feedback.claude.yml", "feedback"),
        ("docs-feedback.copilot.yml", "feedback"),
    ];

    /// <summary>
    /// The Copilot rail's half of the tool-grant contract: the grant is spelled out, and the blanket
    /// is absent.
    /// </summary>
    /// <remarks>
    /// <c>--allow-all-tools</c> / <c>--allow-all-paths</c> are Copilot's equivalent of
    /// <c>--dangerously-skip-permissions</c>, and they are refused for the identical reason: these jobs
    /// hand an unattended model a token that can push branches and open PRs, and on
    /// <c>docs-feedback</c> the input is text a reviewer wrote in Confluence (rule §1.3). The
    /// executable yaml is what is searched, not the file — both templates discuss the blanket at length
    /// in a comment explaining how to use it while diagnosing a hang, and a naive text search would
    /// read that advice as the violation it warns against.
    /// </remarks>
    private static void AssertCopilotGrantIsEnumerated(string name, string runnable)
    {
        var blanket = $"{name} passes a blanket tool grant. That is Copilot's "
            + "--dangerously-skip-permissions, and it is refused for the same reason: this job can push "
            + "branches and open PRs unattended. Enumerate what the skill actually needs.";

        runnable.ShouldNotContain("--allow-all-tools", customMessage: blanket);
        runnable.ShouldNotContain("--allow-all-paths", customMessage: blanket);

        var ungranted = $"{name} passes no --allow-tool, so the model run gets Copilot's default grant "
            + "instead of one this repo decided on.";

        var grants = runnable
            .Split('\n')
            .Count(line => line.Contains("--allow-tool", StringComparison.Ordinal));

        grants.ShouldBeGreaterThan(0, ungranted);

        // The three binaries every skill in this plugin reaches for: `docume` through `dotnet tool
        // run`, the PR through `gh`, the branch and commit through `git`. A grant that dropped one
        // fails at the step that needed it, mid-run, after the model has already been paid for.
        foreach (var binary in new[] { "git", "gh", "dotnet" })
        {
            var unreachable = $"{name} never grants shell({binary}…), so the skill cannot run "
                + $"`{binary}` — the run dies at the first step that needs it, having already spent.";

            runnable.ShouldContain($"shell({binary}", customMessage: unreachable);
        }
    }

    /// <summary>
    /// The skill a template invokes, which is its file name with the rail infix taken back off:
    /// <c>docs-refresh.copilot.yml</c> runs <c>/docs-refresh</c>, out of
    /// <c>plugin/skills/docs-refresh/</c>. Both rails run the same skill — that is the point of having
    /// rails at all, and the reason the skill bodies are untouched by them.
    /// </summary>
    private static string SkillName(string template)
    {
        var stem = Path.GetFileNameWithoutExtension(template);
        var infix = stem.LastIndexOf('.');

        return infix < 0 ? stem : stem[..infix];
    }

    /// <summary>Whether a template is the Copilot spelling, by the same infix.</summary>
    private static bool IsCopilot(string template) =>
        Path.GetFileNameWithoutExtension(template).EndsWith(".copilot", StringComparison.Ordinal);

    private const string CredentialPrefix = "DOCUME_CONFLUENCE_";
    private const string ToolRestore = "dotnet tool restore";
    private const string ToolRun = "dotnet tool run docume";
    private const string ConfigRead = "jq -r '.wiki.root // \"docs/wiki\"' docume.json";
    private const string ConfigGuard = "if [ ! -f docume.json ]; then";
    private const string Annotation = "::error title=DocuMe::";
    private const string MermaidPackage = "beautiful-mermaid";

    [Fact]
    public void Every_template_PLAN_10_names_is_present()
    {
        var missing = Templates
            .Where(name => !File.Exists(Path.Combine(Directory, name)))
            .ToList();

        // Names, not a count: the rest of this class iterates the same list, so a template that
        // silently vanished would turn every other test here into a vacuous pass.
        missing.ShouldBeEmpty($"Missing workflow template(s) under {Directory}.");
    }

    /// <summary>
    /// And nothing beyond them. <c>docume init</c> ships this directory by glob, so a template
    /// dropped in here without being added to <see cref="Templates"/> would reach consumer repos
    /// with none of the assertions below ever having read it.
    /// </summary>
    [Fact]
    public void No_template_ships_without_being_covered_here()
    {
        var uncovered = System.IO.Directory
            .GetFiles(Directory, "*.yml")
            .Select(Path.GetFileName)
            .Where(name => !Templates.Contains(name, StringComparer.Ordinal))
            .ToList();

        uncovered.ShouldBeEmpty(
            $"Workflow template(s) under {Directory} that `docume init` ships but this class does not "
            + "check. Add them to Templates (and to ConfluenceFacing / ModelDriven / SkillDriven as "
            + "they apply).");
    }

    /// <summary>
    /// <see cref="ConfluenceFacing"/> names every template that holds a credential, and no other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two facts above pin <see cref="Templates"/> to the directory in both directions. Three
    /// subsets of it then bound sweeps of their own, and nothing paired any of the three with
    /// anything — so an entry dropped from one narrowed that sweep in silence, the remaining entries
    /// still passing and no complement claiming the dropped one. Measured at iteration 193: removing
    /// <c>docs-publish.yml</c> from this list left the whole suite green, and with it nothing
    /// asserting that the template which writes every page exports either credential.
    /// </para>
    /// <para>
    /// Only omission was silent. A name <em>added</em> to one of these lists is read against a file
    /// that does not satisfy the sweep, so it fails that sweep's own assertion — measured too, by
    /// putting <c>docs-drift-pr.yml</c> here, which
    /// <see cref="Confluence_facing_templates_export_both_credentials"/> caught. That is why each of
    /// the three facts is a set equality against the templates themselves rather than a floor: the
    /// direction that needed covering is the one a floor cannot see.
    /// </para>
    /// <para>
    /// <see cref="ModelDriven"/> is deliberately not given a fourth fact.
    /// <see cref="Every_repository_a_template_checks_out_is_the_one_this_repo_is"/> already compares
    /// it as a set against the checkouts it finds, which fails on an entry dropped or added — also
    /// measured. A mechanism that removes nothing once another has landed must not ship.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_credential_facing_list_names_every_template_that_holds_a_credential()
    {
        var holding = Templates
            .Where(name => Text(name).Contains(CredentialPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        const string drifted =
            "ConfluenceFacing bounds the sweep asserting both credential variables are exported, and "
            + "it no longer names the same templates the files do. A name dropped from it takes that "
            + "template out of the sweep with nothing else noticing — the credentials could then "
            + "vanish from a publishing job and this suite would stay green (rule §1.1).";

        holding.ShouldBe(ConfluenceFacing.Order(StringComparer.Ordinal), drifted);
    }

    /// <summary>
    /// <see cref="SkillDriven"/> names every template that never invokes the tool by name, and no
    /// other.
    /// </summary>
    /// <remarks>
    /// The one of the three that is an <em>exemption</em> rather than an inventory, so it fails in
    /// both directions and neither is theoretical. A template added to it is excused from
    /// <see cref="Every_template_restores_the_pinned_tool_before_running_it"/>'s ordering half
    /// silently — measured with <c>docs-publish.yml</c>, whole suite green. A template left in it
    /// after it gains a literal <c>dotnet tool run docume</c> excuses nothing while reading as
    /// though it does, which is the shape an exemption must never be allowed to rot into.
    /// </remarks>
    [Fact]
    public void The_skill_driven_exemption_names_every_template_that_never_invokes_the_tool()
    {
        var silent = Templates
            .Where(name => !Runnable(name).Any(line => line.Contains(ToolRun, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);

        const string drifted =
            "SkillDriven excuses its members from having to restore the pinned manifest BEFORE "
            + "invoking the tool, on the grounds that their only invocation happens inside the skill "
            + "they run. It no longer names the templates that is true of, so it is either excusing a "
            + "template that does name the CLI or claiming an exemption that removes nothing (§12).";

        silent.ShouldBe(SkillDriven.Order(StringComparer.Ordinal), drifted);
    }

    /// <summary>
    /// <see cref="ConfigReaders"/> names every template that reads <c>docume.json</c>, and no other.
    /// </summary>
    /// <remarks>
    /// Its <c>Job</c> half was already held — <see cref="Steps"/> throws on a job that does not
    /// exist, and the lookup for the read step throws when that job holds none. Its <c>Template</c>
    /// half was held by nothing, so dropping an entry left that template reading the config with
    /// nothing holding it to the guard: measured with <c>docs-sync.yml</c> and again with
    /// <c>docs-refresh.yml</c>, whole suite green both times.
    /// </remarks>
    [Fact]
    public void The_config_reader_list_names_every_template_that_reads_the_config()
    {
        var reading = Templates
            .Where(name => Runnable(name).Any(line => line.Contains(ConfigRead, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);

        var declared = ConfigReaders
            .Select(entry => entry.Template)
            .Order(StringComparer.Ordinal);

        const string drifted =
            "ConfigReaders bounds the sweep asserting a template checks docume.json is there before "
            + "jq reads it, and it no longer names the templates that read it. A template dropped "
            + "from the list ships an unguarded read, whose failure is a bare jq error as the whole "
            + "step log of a consumer's cron job.";

        reading.ShouldBe(declared, drifted);
    }

    [Fact]
    public void Every_template_is_a_yaml_workflow()
    {
        foreach (var name in Templates)
        {
            var root = Root(name);

            // `on` and `jobs` are what makes a file a workflow rather than a yaml document GitHub
            // ignores without complaint. Read as scalar keys on purpose: a typed deserializer would
            // resolve `on` to a boolean under YAML 1.1 rules and the assertion would be about the
            // wrong key.
            Keys(root).ShouldContain("on", $"{name} has no trigger.");
            Keys(root).ShouldContain("jobs", $"{name} has no jobs.");

            foreach (var job in Mapping(root, "jobs").Children)
            {
                var steps = Mapping(job.Value).Children
                    .Any(child => IsKey(child.Key, "steps"));

                steps.ShouldBeTrue($"{name}: job '{Scalar(job.Key)}' has no steps.");
            }
        }
    }

    /// <summary>
    /// No template reads the <c>runner</c> context, which does not exist in the one place three of them
    /// used it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The context is available from step level down and NOT in <c>jobs.&lt;id&gt;.env</c>. A
    /// <c>${{ runner.temp }}</c> there does not resolve to an empty string, which would at least fail
    /// somewhere near the mistake: GitHub rejects the entire file. docs-drift-pr, docs-feedback and
    /// docs-refresh all shipped that way in v0.1.0, so half the scaffolded CI was dead on arrival in
    /// every consumer repo.
    /// </para>
    /// <para>
    /// THE SYMPTOM IS WHY THIS IS A TEST. The run fails in 0 s, attributed to the <c>push</c> event — on
    /// workflows whose triggers are <c>pull_request</c> and <c>schedule</c> and do not include push at all
    /// — carrying "This run likely failed because of a workflow file issue", no annotation, and no line
    /// number. Nothing in it names a context, a key, or a file position.
    /// <see cref="Every_template_is_a_yaml_workflow"/> cannot catch it either, and that is not a gap in
    /// that test: the file is valid YAML. It is invalid GitHub.
    /// </para>
    /// <para>
    /// Banned outright rather than only inside <c>env:</c>, which would be the narrower true rule. The
    /// three templates that never had the bug take their scratch paths from the <c>$RUNNER_TEMP</c>
    /// environment variable, readable in every step and naming the same directory, so the whole family
    /// already has one idiom and this keeps it. A narrower assertion would have to decide which scopes are
    /// safe, and be re-derived by whoever next moves a path between them.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_template_reads_the_runner_context()
    {
        foreach (var name in Templates)
        {
            foreach (var line in Runnable(name))
            {
                line.Contains("runner.", StringComparison.Ordinal).ShouldBeFalse(
                    $"{name} reads the `runner` context. It does not exist in a job-level `env:`, and "
                    + "GitHub rejects the whole file rather than resolving it empty — as a 0s run "
                    + "attributed to `push`, with no annotation naming the line. Use the $RUNNER_TEMP "
                    + $"environment variable in a step instead: {line.Trim()}");
            }
        }
    }

    /// <summary>
    /// Every template grants the <c>packages: read</c> its <c>dotnet tool restore</c> cannot work without.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each of these files declares an explicit <c>permissions:</c> block, and an explicit block sets every
    /// scope it does not name to <c>none</c>. All six named <c>contents</c> and <c>pull-requests</c> and
    /// none named <c>packages</c>, so GITHUB_TOKEN reached the feed with no package access whatever and
    /// <see cref="Every_template_restores_the_pinned_tool_before_running_it"/>'s restore died on
    /// <c>Unhandled exception: Response status code does not indicate success: 403 (Forbidden)</c> —
    /// which names neither the feed, nor the scope, nor DocuMe.
    /// </para>
    /// <para>
    /// Necessary and not sufficient, and the insufficiency is worth recording next to the assertion so the
    /// next reader does not conclude the grant is redundant: a GitHub Packages package is scoped to the
    /// repository that published it, so a consumer repo also has to be granted read on the package itself.
    /// That half cannot be tested from here — it is state in another repository's settings — which is
    /// exactly why the feed step's comment now carries it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_template_grants_the_packages_scope_its_restore_needs()
    {
        foreach (var name in Templates)
        {
            var root = Root(name);

            Keys(root).ShouldContain(
                "permissions",
                $"{name} declares no `permissions:` block, so this assertion is about nothing.");

            var permissions = Mapping(root, "permissions");

            var denied =
                $"{name} restores DocuMe.Cli from GitHub Packages but grants no `packages` scope. An "
                + "explicit `permissions:` block denies every scope it does not name, so the restore "
                + "fails on a bare 403 that names neither the feed nor the scope.";

            Keys(permissions).ShouldContain("packages", denied);

            Scalar(permissions.Children.Single(child => IsKey(child.Key, "packages")).Value).ShouldBe(
                "read",
                $"{name}: the restore reads the feed and never writes to it, so `packages` is `read`.");
        }
    }

    [Fact]
    public void Every_template_restores_the_pinned_tool_before_running_it()
    {
        foreach (var name in Templates)
        {
            var text = Text(name);
            var restore = text.IndexOf(ToolRestore, StringComparison.Ordinal);
            var run = text.IndexOf(ToolRun, StringComparison.Ordinal);

            // Asserted for every template including the skill-driven one, where it matters more rather
            // than less: docs-feedback.yml never names the CLI, and this restore is the only thing making
            // the `docume status --offline` the skill runs resolve to the version §12 pinned.
            restore.ShouldBeGreaterThan(-1, $"{name} never restores the pinned manifest (§12).");

            if (SkillDriven.Contains(name))
            {
                continue;
            }

            run.ShouldBeGreaterThan(-1, $"{name} never invokes the tool.");
            restore.ShouldBeLessThan(run, $"{name} runs docume before restoring the manifest (§12).");
        }
    }

    [Fact]
    public void No_template_passes_prune()
    {
        // Rule §9.6: orphan deletion needs interactive confirmation and never runs in CI. PruneGuard
        // already refuses in a non-interactive session, so a template passing it would be written
        // against a refusal — the template is the wrong place to discover that.
        //
        // Comment lines are exempt because docs-publish.yml says out loud that the flag is absent and
        // must stay absent. That sentence is the reason the next editor does not add it back, and a
        // test that forbade explaining a rule would be trading a real safeguard for a grep.
        var offenders = Templates
            .Where(name => Runnable(name).Any(line => line.Contains("--prune", StringComparison.Ordinal)))
            .ToList();

        offenders.ShouldBeEmpty("--prune must never appear in a CI template (rule §9.6).");
    }

    [Fact]
    public void Credentials_are_only_ever_read_from_secrets()
    {
        var offenders = new List<string>();

        foreach (var name in Templates)
        {
            var inlined = Runnable(name)
                .Where(line => line.Contains(CredentialPrefix, StringComparison.Ordinal))
                .Where(line => line.Contains(':', StringComparison.Ordinal))
                .Where(line => !line.Contains("secrets.", StringComparison.Ordinal))
                .Select(line => $"{name}: {line.Trim()}");

            offenders.AddRange(inlined);
        }

        // Rule §1.1: env vars from secrets only, never a literal in a committed file.
        offenders.ShouldBeEmpty("A credential must come from `${{ secrets.… }}` (rule §1.1).");
    }

    [Fact]
    public void Confluence_facing_templates_export_both_credentials()
    {
        foreach (var name in ConfluenceFacing)
        {
            var text = Text(name);

            text.ShouldContain($"{CredentialPrefix}EMAIL", customMessage: $"{name} publishes without an email.");
            text.ShouldContain($"{CredentialPrefix}TOKEN", customMessage: $"{name} publishes without a token.");
        }
    }

    [Fact]
    public void The_pull_request_advisory_carries_no_credentials()
    {
        // The design decision this locks: the drift report is a git diff plus a glob match, so the one
        // workflow that runs on every contributor's branch never has the publishing token in its
        // environment. Merging it into docs-drift.yml for tidiness would hand that token to a job
        // triggered by anyone who can open a PR.
        var text = Text("docs-drift-pr.yml");

        text.ShouldNotContain(
            CredentialPrefix,
            customMessage: "The PR advisory job needs no Confluence credentials — keep them out of it.");
    }

    [Fact]
    public void Drift_templates_pass_the_option_the_CLI_actually_has()
    {
        foreach (var name in Templates)
        {
            var lines = Runnable(name)
                .Where(line => line.Contains("docume drift", StringComparison.Ordinal))
                .ToList();

            if (lines.Count == 0)
            {
                continue;
            }

            // §10's own prose writes `--base origin/<defaultBranch>`; no such option was ever built.
            // A template copying the plan verbatim fails at parse time in a consumer's repo.
            var invocations = string.Join('\n', lines);

            invocations.ShouldContain(
                "--baseline",
                customMessage: $"{name} must diff from an explicit baseline.");
            invocations.ShouldNotContain(
                "--base ",
                customMessage: $"{name}: the option is --baseline, not --base.");
            invocations.ShouldNotContain(
                "--base=",
                customMessage: $"{name}: the option is --baseline, not --base.");
        }
    }

    [Fact]
    public void Every_model_run_keeps_its_permission_gate()
    {
        foreach (var name in ModelDriven)
        {
            var runnable = Runnable(name).ToList();

            // §11's headless invocation is `claude -p "/docs-refresh" --permission-mode acceptEdits`. The
            // flag is the assertion, not the mode: these are the templates that hand an unattended model a
            // token that can push branches and open PRs, and `--dangerously-skip-permissions` would remove
            // the only thing standing between the two. A template is the worst place to lose that, because
            // whoever copies it will not re-derive why it was there. It matters most in docs-feedback.yml,
            // whose input is text a reviewer wrote in Confluence (rule §1.3).
            var text = string.Join('\n', runnable);

            // Each rail spells the gate with its own flag, and both are asserted rather than one being
            // waved through: Claude bounds the run with --permission-mode, Copilot with the enumerated
            // --allow-tool set checked in Every_model_run_grants_exactly_the_tools_its_skill_declares.
            // What is common is the refusal below — neither rail may hand the run an unbounded grant.
            var gate = IsCopilot(name) ? "--allow-tool" : "--permission-mode";

            text.ShouldContain(gate, customMessage: $"{name} runs a model with no permission gate (§11).");

            foreach (var escape in new[] { "--dangerously-skip-permissions", "--allow-all-tools" })
            {
                text.ShouldNotContain(
                    escape,
                    customMessage: $"{name}: never in a template — this job can push branches and open PRs.");
            }

            // Rule §1.1 again, for the credential only these templates carry. Which one it is depends
            // on the rail; that it is read from `secrets.` does not.
            var secret = IsCopilot(name) ? "COPILOT_GITHUB_TOKEN" : "ANTHROPIC_API_KEY";

            // Where the secret is BOUND, which is the line rule §1.1 is about, rather than every line
            // that mentions it. The Copilot rail names its own token twice more: once in the shell
            // emptiness test its preflight step runs, and once in that step's error message. A looser
            // "contains the name and a colon" filter collects both of those and then fails them for
            // not saying secrets. — the assertion misreading a shell test as a declaration.
            var credential = runnable
                .Where(line => line.TrimStart().StartsWith($"{secret}:", StringComparison.Ordinal))
                .ToList();

            credential.ShouldNotBeEmpty($"{name} runs a model without naming {secret}.");
            credential.ShouldAllBe(
                line => line.Contains("secrets.", StringComparison.Ordinal),
                $"{name}: {secret} must come from `${{{{ secrets.… }}}}`.");
        }
    }

    /// <summary>
    /// Each model-driven template invokes its own skill, and grants it exactly the tools that skill's
    /// frontmatter declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one coupling in §11's invocation that fails silently in both directions, and it was
    /// held by nothing until the invocation was first executed (iteration 116, against a throwaway
    /// consumer repo: <c>claude -p '/docs-refresh' --plugin-dir … --permission-mode acceptEdits
    /// --allowed-tools 'Bash,Read,Write,Edit,Glob,Grep'</c>, exit 0 in 24 turns).
    /// </para>
    /// <para>
    /// <c>--allowed-tools</c> is the outer bound: the frontmatter cannot widen it. So a tool added to a
    /// SKILL.md and not to the template is a tool the skill believes it has and does not, and the run
    /// fails at whichever step needed it — a red nightly job whose log blames a tool call, not the
    /// template that never granted it. The other direction is the one that matters more: a template
    /// granting a tool the skill never declares hands an unattended model holding a branch-push token
    /// more reach than the skill was reviewed for, which is the same boundary
    /// <c>--dangerously-skip-permissions</c> is kept out of these files to protect.
    /// </para>
    /// <para>
    /// The slash command is asserted here too, for the reason
    /// <see cref="Every_model_run_looks_for_the_branch_its_own_skill_pushes"/> gives about the branch
    /// pattern: these two templates are verbatim twins apart from a handful of strings, and a
    /// <c>docs-feedback.yml</c> that ran <c>/docs-refresh</c> would be green here, green in CI, and
    /// wrong every night.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_model_run_grants_exactly_the_tools_its_skill_declares()
    {
        foreach (var name in ModelDriven)
        {
            var skill = SkillName(name);
            var runnable = string.Join('\n', Runnable(name));

            // The slash command, from the yaml the runner acts on rather than from the prose above it.
            var wrongSkill = $"{name} must invoke /{skill} (§11). A template that runs another "
                + "template's skill passes every other assertion in this class.";

            var cli = IsCopilot(name) ? "copilot" : "claude";

            runnable.ShouldContain($"{cli} -p '/{skill}'", customMessage: wrongSkill);

            // The two rails express a tool grant differently enough that one set comparison cannot
            // cover both, and pretending otherwise is how a real grant stops being checked.
            //
            // Claude's --allowed-tools is a list of the SAME tool names the skill declares in its
            // frontmatter, so the two sides compare directly and the assertion below is exact.
            //
            // Copilot's --allow-tool is a repeated flag naming BINARIES and filters — shell(git:*),
            // write — which have no correspondence to Bash/Read/Edit. Set equality against the
            // frontmatter would be comparing two vocabularies. What is checkable, and what actually
            // matters, is that the grant stayed enumerated: the blanket is the thing this whole family
            // of assertions exists to keep out.
            if (IsCopilot(name))
            {
                AssertCopilotGrantIsEnumerated(name, runnable);

                continue;
            }

            var granted = QuotedFlagValue(runnable, "--allowed-tools");
            var declared = FrontmatterValue(SkillText(skill), "allowed-tools");

            // Vacuous-pass guards, both sides. An unparsed flag or a dropped frontmatter key would
            // otherwise make the set comparison below compare two empties and pass.
            granted.ShouldNotBeEmpty(
                $"{name} passes no --allowed-tools, so the model run gets the CLI's default grant "
                + "instead of the skill's declared one.");
            declared.ShouldNotBeEmpty(
                $"plugin/skills/{skill}/SKILL.md declares no allowed-tools, so nothing says what the "
                + $"{name} run may reach for.");

            var missing = declared.Except(granted, StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var extra = granted.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal);

            missing.ShouldBeEmpty(
                $"plugin/skills/{skill}/SKILL.md declares tool(s) that {name} does not grant. The skill "
                + "cannot use them: --allowed-tools is the outer bound, so the run fails at the step "
                + "that needed one.");
            extra.ShouldBeEmpty(
                $"{name} grants tool(s) plugin/skills/{skill}/SKILL.md never declares. An unattended "
                + "model holding a branch-push token gets reach the skill was not reviewed for.");
        }
    }

    /// <summary>
    /// Each headless model run is bounded by its own <c>timeout-minutes</c>, and bounded by less than the
    /// job default it would otherwise inherit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured at iteration 116: <c>claude -p</c> on the default output format writes nothing at all
    /// until it exits — a run killed at 420 s left 0 bytes of stdout. So an unattended run that hangs
    /// produces no partial transcript and no line saying what it was doing, and both templates' next step
    /// tells the reader to "read the step log above". Without a step timeout the run holds the runner for
    /// the job default (6 h) first, and what finally arrives is a cancelled badge over an empty log.
    /// </para>
    /// <para>
    /// The timeout does not make the log any less empty. What it does is put the runner's own
    /// "The action … has timed out" beside it, which is the difference between a blank log that states
    /// its cause and a blank log that reads like the skill did nothing. A real run is minutes — 168 s,
    /// 24 turns, the same iteration-116 measurement.
    /// </para>
    /// <para>
    /// Asserted because it is invisible: no yaml linter wants it, the templates are green without it, and
    /// the failure it prevents only ever happens in somebody else's repository at 03:00. The upper bound
    /// is the point of the assertion as much as the lower one — a <c>timeout-minutes</c> raised past the
    /// job default bounds nothing while looking like it does.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_model_run_is_bounded_by_a_step_timeout()
    {
        foreach (var name in ModelDriven)
        {
            var steps = ModelRunSteps(name);

            // Vacuous-pass guard: a renamed or restructured invocation must fail here rather than leave
            // the assertions below with nothing to read.
            var oneRun = $"{name} should hold exactly one `claude -p` step (§11), and the timeout "
                + "assertion below reads it.";

            steps.Count.ShouldBe(1, oneRun);

            var timeout = Value(steps[0], "timeout-minutes");

            timeout.ShouldNotBeEmpty(
                $"{name}'s model run carries no timeout-minutes, so a hung run holds the runner for the "
                + "job default. Its output is buffered until exit, so the reader gets a cancelled badge "
                + "over an empty log, hours late and with nothing naming the cause.");

            int.TryParse(timeout, CultureInfo.InvariantCulture, out var minutes).ShouldBeTrue(
                $"{name} spells timeout-minutes as '{timeout}', which is not a whole number of minutes.");

            minutes.ShouldBeGreaterThan(
                0,
                $"{name}'s model run is given {minutes} minutes, which cannot complete a model run.");
            var unbounded = $"{name}'s model run is given {minutes} minutes, at or past the "
                + $"{JobDefaultTimeoutMinutes}-minute job default it would inherit anyway. A bound that "
                + "never fires before the runner's own does is not a bound.";

            minutes.ShouldBeLessThan(JobDefaultTimeoutMinutes, unbounded);
        }
    }

    [Fact]
    public void No_model_run_writes_to_Confluence_or_holds_a_credential()
    {
        foreach (var name in ModelDriven)
        {
            // Rule §0.4 as a file assertion. A skill's output is a PR; every Confluence write happens
            // later, behind a human's merge, in docs-publish.yml. So an unattended model run has no reason
            // to hold the publishing token — and once it holds none, a run that tried to write anyway
            // fails on credentials rather than reaching a space.
            Text(name).ShouldNotContain(
                CredentialPrefix,
                customMessage: $"{name} writes nothing to Confluence — keep the token out of it.");

            // `publish` and `sync --reply` are the two writes these jobs could plausibly reach for, and
            // both would be wrong here for the same reason: the fix is still sitting in an unmerged branch.
            // A reply saying "fixed in the latest version" before the merge tells a reviewer something
            // untrue (§9 step 5).
            var writes = Runnable(name)
                .Where(line => line.Contains("docume publish", StringComparison.Ordinal)
                    || line.Contains("docume sync", StringComparison.Ordinal))
                .ToList();

            writes.ShouldBeEmpty(
                $"{name} opens a PR; docs-publish.yml publishes it and answers the reviewers after the "
                    + "merge (§9 step 5, §10).");
        }
    }

    [Fact]
    public void Every_repository_a_template_checks_out_is_the_one_this_repo_is()
    {
        var declared = DeclaredRepository();

        var checkouts = Templates
            .SelectMany(name => CheckedOutRepositories(name).Select(repository => (Template: name, Repository: repository)))
            .ToList();

        // Vacuous-pass guard. The two model-driven templates fetch the plugin out of this repository and
        // no other template checks out anything but its own, so an empty find means the walk stopped
        // reading the yaml rather than that the templates are clean.
        var lost = "The two model-driven templates fetch the DocuMe plugin from this repository. "
            + $"Checkouts found: [{string.Join(", ", checkouts.Select(entry => entry.Template))}], expected "
            + $"[{string.Join(", ", ModelDriven)}].";

        checkouts
            .Select(entry => entry.Template)
            .Order(StringComparer.Ordinal)
            .ShouldBe(ModelDriven.Order(StringComparer.Ordinal), lost);

        // The bug this exists for, found at iter80 and shipped since the templates were written: both of
        // them named `moberg/docu-me` and the org is `moberghr`. `actions/checkout` cannot resolve it, so
        // the nightly refresh and every feedback triage in every consumer repo died at the checkout —
        // and because the step is `if:`-gated on drift, a repo with no drift stayed green and told nobody.
        // The same typo reached the scaffolded `$schema` URL (iter67, ConfigSchemaTests), which is why
        // this reads the slug off plugin.json rather than retyping it a fourth time.
        var wrong = checkouts
            .Where(entry => !string.Equals(entry.Repository, declared, StringComparison.Ordinal))
            .Select(entry => $"{entry.Template} → {entry.Repository}")
            .ToList();

        wrong.ShouldBeEmpty(
            $"A checkout naming a repository that is not this one ({declared}, per plugin.json) fails in "
                + "the consumer's runner, not here. Offenders:");
    }

    [Fact]
    public void The_feedback_template_triggers_on_the_inbox_it_triages()
    {
        // Both rails, because the trigger and the precheck are the half of this job that has nothing to
        // do with which model runs: a Copilot variant that watched the wrong path, or counted something
        // other than `status: new`, would be exactly as silently-never-firing as a Claude one.
        foreach (var name in SkillDriven)
        {
            AssertFeedbackTriggersOnItsInbox(name);
        }
    }

    private static void AssertFeedbackTriggersOnItsInbox(string name)
    {
        var text = Text(name);

        // §9's work list is inbox items with `status: new`, and they arrive on the default branch when the
        // docs/sync PR merges. That push is the trigger, which is what keeps the model run tied to there
        // being feedback rather than to a clock — and a path filter pointing anywhere else would leave the
        // job silently never firing, the failure mode a green check cannot show.
        text.ShouldContain(
            FeedbackInbox.RelativeDirectory,
            customMessage: $"{name} must watch the inbox directory (§5.4).");

        var paths = Mapping(Mapping(Root(name), "on"), "push").Children
            .Any(child => IsKey(child.Key, "paths"));

        paths.ShouldBeTrue($"{name} must filter its push trigger to the inbox path (§9).");

        // The precheck is the reason this template is cheap enough to fire on every wiki push: `status`
        // is the field §5.4 defines and FeedbackStatus.New is the one value ingestion writes, so a
        // template counting anything else would run a model over an inbox with nothing new in it.
        var counts = Runnable(name)
            .Where(line => line.Contains(".status", StringComparison.Ordinal))
            .ToList();

        counts.ShouldNotBeEmpty($"{name} must count untriaged items before running a model (§9 step 1).");

        // The comparison itself, not the word anywhere in the file: the step that says "no inbox item has
        // status 'new'" out loud would otherwise satisfy this while the gate above it counted something
        // else entirely. Coupled to the shell spelling on purpose — it is the only place the template
        // decides what untriaged means.
        Runnable(name).ShouldContain(
            line => line.Contains($"\"$status\" = '{FeedbackStatus.New}'", StringComparison.Ordinal),
            $"{name} must gate on status '{FeedbackStatus.New}' — the one status ingestion writes (§5.4).");
    }

    [Fact]
    public void The_sync_template_commits_the_feedback_inbox_it_ingests()
    {
        const string name = "docs-sync.yml";
        var runnable = Runnable(name).ToList();
        var text = string.Join('\n', runnable);

        // §6.3's default is both halves, and §9 makes this workflow the thing that commits the inbox
        // items: "docume sync --comments → inbox items → committed via PR by the cron workflow". A
        // template that ran the labels half alone would ingest comments nowhere, and one that ingested
        // them without adding the directory would write them into a runner and throw them away — both
        // failures being silence rather than a red check.
        var sync = runnable
            .Where(line => line.Contains("docume sync", StringComparison.Ordinal))
            .ToList();

        sync.ShouldNotBeEmpty($"{name} never runs a sync (§6.3).");
        sync.ShouldNotContain(
            line => line.Contains("--labels", StringComparison.Ordinal),
            $"{name}: a bare `sync` runs both halves — pinning --labels drops comment ingestion (§6.3).");

        text.ShouldContain(
            "_meta/feedback/inbox",
            customMessage: $"{name} must locate the inbox to commit it (§5.4).");

        // The inbox unconditionally, the state only when the run has one in hand — `git add` on a path
        // that exists in no ref is fatal, and an unguarded line there kills the carry and drops the
        // ingested comments (WorkflowShellTests runs that case).
        text.ShouldContain(
            "git add \"$inbox\"",
            customMessage: $"{name} must stage the inbox items it ingested (§9).");
        text.ShouldContain(
            "git add \"$state\"",
            customMessage: $"{name} must stage the state file alongside them (§9).");
    }

    [Fact]
    public void The_publish_template_answers_the_reviewers_after_it_republishes()
    {
        const string name = "docs-publish.yml";
        var steps = Steps(name, "publish");

        var publish = steps.FindIndex(step => step.Run.Contains($"{ToolRun} publish", StringComparison.Ordinal));
        var reply = steps.FindIndex(step => step.Run.Contains($"{ToolRun} sync --reply", StringComparison.Ordinal));

        publish.ShouldBeGreaterThan(-1, $"{name} never publishes (§10).");

        // §9 step 5 has to run from here and nowhere else. This is the one job where the sentence a
        // reply makes — the reviewer's point is fixed in the page they are looking at — is true: the
        // fix PR merged (that push is this trigger) and the page was republished (the step above), in
        // that order, in one run. Nothing else fails if it is missing; reviewers just never hear back.
        const string missing =
            "docs-publish.yml must run `docume sync --reply` — it is the only job where a merge and a "
            + "republish have both happened, so it is the only place a reply is true (§9 step 5).";

        reply.ShouldBeGreaterThan(-1, missing);

        const string ordered =
            "docs-publish.yml answers the reviewers before republishing their page, so the reply "
            + "claims a fix that is not live yet (§9 step 5).";

        reply.ShouldBeGreaterThan(publish, ordered);
    }

    [Fact]
    public void The_publish_template_commits_every_file_the_reply_pass_stamps()
    {
        const string name = "docs-publish.yml";
        var text = Text(name);

        // Both directories, and the archive is the one that matters most: §9 step 4 moves an item to the
        // archive in the same PR that fixes the page, so by the time step 5 runs, the item being
        // answered is usually no longer in the inbox.
        text.ShouldContain(
            FeedbackInbox.RelativeDirectory,
            customMessage: $"{name} must locate the inbox the reply pass stamps (§5.4).");
        text.ShouldContain(
            FeedbackInbox.RelativeArchiveDirectory,
            customMessage: $"{name} must locate the archive the reply pass stamps (§5.4).");

        // `repliedAt` is the whole double-reply guard: a run that posts the replies and throws the
        // stamps away answers every one of them again on the next publish, and each re-run posts
        // another comment under a reviewer's comment. Nothing goes red when that happens.
        const string staged =
            "docs-publish.yml must stage the inbox and the archive alongside the state file — "
            + "`sync --reply` stamps `repliedAt` into those files, and dropping the stamp re-posts "
            + "every reply on the next publish (§5.4, §9 step 5).";

        // Both feedback directories unconditionally; the state file only when the run has one in hand,
        // because `git add` on a path that exists in no ref is fatal and would kill the carry outright
        // (WorkflowShellTests runs that case).
        text.ShouldContain("git add \"$inbox\" \"$archive\"", customMessage: staged);
        text.ShouldContain("git add \"$state\"", customMessage: staged);

        // And the early exit has to watch them too. A push that changes no page body still stamps the
        // items the reply answered, so a guard reading the state alone calls that "nothing to do".
        const string guarded =
            "docs-publish.yml's early exit must watch the feedback directories as well as the state "
            + "file: a publish can stamp an item while leaving state.json untouched.";

        text.ShouldContain(
            "git status --porcelain -- \"$state\" \"$inbox\" \"$archive\"",
            customMessage: guarded);
    }

    [Fact]
    public void A_refused_reply_still_carries_the_state_and_the_stamps()
    {
        const string name = "docs-publish.yml";
        var steps = Steps(name, "publish");

        var reply = steps.Single(step => step.Run.Contains($"{ToolRun} sync --reply", StringComparison.Ordinal));

        reply.Id.ShouldNotBeEmpty($"{name}: the reply step needs an id for its exit code to be read.");

        // The step holds its exit code instead of failing. `--reply` stamps each item the moment that
        // item's reply lands, so a run that posts six of ten and then hits a 403 has six stamps on disk
        // — and the state file above it holds the pageId of every page this run created. Letting the
        // step fail skips the carry and throws away both: the six replies get posted again, and the
        // next publish creates a second copy of every new page.
        const string held =
            "docs-publish.yml's reply step must hold its exit code (`|| code=$?`) rather than fail, so "
            + "the carry step still runs — a failed reply must not cost the state file or the stamps "
            + "the replies that did land wrote.";

        reply.Run.ShouldContain("|| code=$?", customMessage: held);

        var carry = steps.FindIndex(step => step.Run.Contains("git add \"$state\"", StringComparison.Ordinal));
        var fails = steps.FindIndex(step => step.If.Contains($"steps.{reply.Id}.outputs", StringComparison.Ordinal));

        carry.ShouldBeGreaterThan(-1, $"{name} never carries the state file into the PR.");

        // Nothing in between. Every step standing between a posted reply and the commit that records it
        // is a step whose failure turns one answered comment into two on the next publish — the dashboard
        // refresh is the obvious candidate, and it belongs before the reply because losing it costs a
        // stale table and nothing in the repo.
        const string adjacent =
            "docs-publish.yml must carry the stamps in the step right after the replies are posted: "
            + "anything in between is a failure that re-posts a reply a reviewer already received.";

        carry.ShouldBe(steps.IndexOf(reply) + 1, adjacent);

        // Held, not swallowed. Without this the job is green while a reviewer's comment sits unanswered.
        const string reported =
            "docs-publish.yml holds the reply exit code and never checks it, so Confluence refusing a "
            + "reply leaves a green check.";

        fails.ShouldBeGreaterThan(-1, reported);
        fails.ShouldBeGreaterThan(carry, $"{name} must fail the job only after the state is safe.");
    }

    [Fact]
    public void A_failed_publish_still_carries_the_state_it_already_wrote()
    {
        const string name = "docs-publish.yml";
        var steps = Steps(name, "publish");

        // Every step that runs the tool, not the reply alone. `docume publish` saves state.json BEFORE it
        // reports a failure, on purpose (§6.2 step 8): a page id earned by a create cannot be earned again,
        // so the run that failed on page seven is the run whose state file matters most. A step that failed
        // would skip the carry below, and the next run would create a second copy of every page this one
        // created — the exact duplicate-title rejection the state file exists to prevent.
        var held = steps
            .Where(step => step.Run.Contains(ToolRun, StringComparison.Ordinal))
            .ToList();

        held.Count.ShouldBe(3, $"{name} should run publish, the dashboard and the reply pass (§10).");

        foreach (var step in held)
        {
            step.Id.ShouldNotBeEmpty($"{name}: the \"{step.Name}\" step needs an id to publish its code.");

            var unheld =
                $"{name}: the \"{step.Name}\" step must hold its exit code (`|| code=$?`) rather than fail. "
                + "A failure there skips the carry step, which is what commits the state file the publish "
                + "already wrote and the `repliedAt` stamps of the replies that already landed.";

            step.Run.ShouldContain("|| code=$?", customMessage: unheld);

            var unreported =
                $"{name}: the \"{step.Name}\" step holds its exit code without writing it to $GITHUB_OUTPUT, "
                + "so the failure cannot be turned into a red check further down.";

            step.Run.ShouldContain("echo \"code=$code\" >> \"$GITHUB_OUTPUT\"", customMessage: unreported);
        }

        var carry = steps.FindIndex(step => step.Run.Contains("git add \"$state\"", StringComparison.Ordinal));

        carry.ShouldBeGreaterThan(-1, $"{name} never carries the state file into the PR.");

        foreach (var step in held)
        {
            steps
                .IndexOf(step)
                .ShouldBeLessThan(carry, $"{name}: \"{step.Name}\" must run before the state is carried.");
        }

        // One step, after the carry, reading all three codes. Held and never read is the worse bug of the
        // two this test guards: a publish that failed half way leaves a green check, and the next push
        // narrows `--changed-since` against a sha whose pages were never all written.
        var report = steps.FindIndex(step => held.All(
            command => step.If.Contains($"steps.{command.Id}.outputs.code", StringComparison.Ordinal)));

        const string unread =
            "docs-publish.yml must fail the job on any held exit code — publish, dashboard and reply — in "
            + "one step after the carry. A code that nothing reads is a green check over a failed publish.";

        report.ShouldBeGreaterThan(-1, unread);
        report.ShouldBeGreaterThan(carry, $"{name} must fail the job only after the state is safe.");

        // And the reply is the one command that must not run after a failed publish. It claims the
        // reviewer's point is fixed in the page they are looking at; the reply pass reads triaged items and
        // live comments, so it cannot know that this run never reached that page. The dashboard is
        // deliberately not gated this way — it states what the state file says, which is true either way.
        var publish = held.Single(step => step.Run.Contains($"{ToolRun} publish", StringComparison.Ordinal));
        var reply = held.Single(step => step.Run.Contains($"{ToolRun} sync --reply", StringComparison.Ordinal));

        const string ungated =
            "docs-publish.yml must skip the reply pass when the publish failed: a reply that answers a "
            + "comment on a page this run never republished is a false claim, posted where nobody re-reads "
            + "it (§9 step 5).";

        reply.If.ShouldContain($"steps.{publish.Id}.outputs.code == '0'", customMessage: ungated);
    }

    [Fact]
    public void A_failed_dashboard_still_carries_the_feedback_the_sync_ingested()
    {
        const string name = "docs-sync.yml";
        var steps = Steps(name, "sync");

        // Both commands, and the dashboard is the one this test is really about. It writes a Confluence
        // page and nothing in this repo, so its own failure costs a stale table — but a step that failed
        // there would skip the PR step below and discard a SUCCESSFUL sync's state file and inbox items.
        // Nothing is lost forever, unlike a publish's page ids: approvals are reconstructible from the live
        // labels and the comments from a cursor that never advanced. What breaks is the loop. A dashboard
        // that keeps failing means every six-hourly run reads a reviewer's comment and throws it away, and
        // §9 stalls behind a red check that names the dashboard rather than the feedback.
        var held = steps
            .Where(step => step.Run.Contains(ToolRun, StringComparison.Ordinal))
            .ToList();

        held.Count.ShouldBe(2, $"{name} should run the sync and the dashboard (§10).");

        foreach (var step in held)
        {
            step.Id.ShouldNotBeEmpty($"{name}: the \"{step.Name}\" step needs an id to publish its code.");

            var unheld =
                $"{name}: the \"{step.Name}\" step must hold its exit code (`|| code=$?`) rather than fail. "
                + "A failure there skips the step that opens the docs/sync PR, which is the only thing "
                + "that gets an ingested comment out of the runner (§6.3, §9).";

            step.Run.ShouldContain("|| code=$?", customMessage: unheld);

            var unreported =
                $"{name}: the \"{step.Name}\" step holds its exit code without writing it to $GITHUB_OUTPUT, "
                + "so the failure cannot be turned into a red check further down.";

            step.Run.ShouldContain("echo \"code=$code\" >> \"$GITHUB_OUTPUT\"", customMessage: unreported);
        }

        var carry = steps.FindIndex(step => step.Run.Contains("git add \"$state\"", StringComparison.Ordinal));

        carry.ShouldBeGreaterThan(-1, $"{name} never opens the docs/sync PR.");

        foreach (var step in held)
        {
            steps
                .IndexOf(step)
                .ShouldBeLessThan(carry, $"{name}: \"{step.Name}\" must run before the PR is opened.");
        }

        // Held and never read is the worse of the two bugs this guards. A cron job nobody watches is
        // exactly the job where a green check over a failed sync goes unnoticed for weeks, and the symptom
        // is a dashboard that has quietly stopped agreeing with the labels a reviewer is adding.
        var report = steps.FindIndex(step => held.All(
            command => step.If.Contains($"steps.{command.Id}.outputs.code", StringComparison.Ordinal)));

        const string unread =
            "docs-sync.yml must fail the job on either held exit code — the sync and the dashboard — in "
            + "one step after the PR is opened. A code that nothing reads is a green check over a failed "
            + "sync.";

        report.ShouldBeGreaterThan(-1, unread);
        report.ShouldBeGreaterThan(carry, $"{name} must fail the job only after the feedback is safe.");
    }

    [Fact]
    public void A_repository_without_the_config_is_told_which_command_fixes_it()
    {
        foreach (var (name, job) in ConfigReaders)
        {
            var step = Steps(name, job).Single(s => s.Run.Contains(ConfigRead, StringComparison.Ordinal));

            // The guard has to precede the read to be a guard at all. Measured before it existed: jq
            // exited 2 and the entire step log was `jq: error: Could not open file docume.json`, which
            // names neither DocuMe nor the command that fixes it — on a cron job, read hours later by
            // someone with six workflows to choose between.
            var guard = step.Run.IndexOf(ConfigGuard, StringComparison.Ordinal);
            var read = step.Run.IndexOf(ConfigRead, StringComparison.Ordinal);

            var unguarded =
                $"{name}: the \"{step.Name}\" step reads docume.json without first checking it is there. "
                + "A repo that copied these workflows in by hand rather than running `docume init` gets a "
                + "bare jq error as its whole log.";

            guard.ShouldBeGreaterThan(-1, unguarded);
            guard.ShouldBeLessThan(read, unguarded);
            step.Run.ShouldContain(Annotation, customMessage: unguarded);

            // `if ! root=$(...)` rather than `root=$(...)`: under `set -e` a bare assignment whose
            // command substitution fails kills the step before any annotation can be echoed, so a
            // docume.json that exists but does not parse would report itself as a jq parse error and
            // nothing else.
            var swallowed =
                $"{name}: the \"{step.Name}\" step must read docume.json as `if ! root=$(...)`. A bare "
                + "assignment lets `set -e` end the step before it can say what was wrong.";

            step.Run.ShouldContain($"if ! root=$({ConfigRead}); then", customMessage: swallowed);
        }
    }

    [Fact]
    public void A_wiki_nobody_has_generated_reaches_its_own_warning()
    {
        // Both templates carry a "No baseline yet" step gated on an empty sha, which exists to say
        // calmly that the wiki has never been generated and let the job pass. It was unreachable in the
        // clearest case of that: with no state file at all, `sha=$(jq ...)` — a bare assignment, unlike
        // docs-publish.yml's, so `set -e` sees it — exited 2 first. The nightly refresh turned that into
        // a red cron check every morning on a repo whose only problem was that nobody had run /docs-loop.
        foreach (var (name, job) in new[]
        {
            ("docs-drift.yml", "mark"),
            ("docs-refresh.claude.yml", "refresh"),
            ("docs-refresh.copilot.yml", "refresh"),
        })
        {
            var steps = Steps(name, job);

            // By id, not by searching for "baselineSha": the warning step names the field too, in the
            // text it prints, so a text match finds two steps and the assertion below would be reading
            // whichever came first.
            var baseline = steps.Single(s => string.Equals(s.Id, "baseline", StringComparison.Ordinal));

            var unreachable =
                $"{name}: the \"{baseline.Name}\" step must read baselineSha only when the state file "
                + "exists. Without the check, a repo that has never been generated fails here with a jq "
                + "error instead of reaching the \"No baseline yet\" warning written for exactly that.";

            baseline.Run.ShouldContain("if [ -f \"$state\" ]; then", customMessage: unreachable);
            baseline.Run.ShouldContain("sha=''", customMessage: unreachable);

            // A state file that EXISTS and will not parse is a broken file, not an absent one, and must
            // stay loud: reading it as "no baseline" would turn a corrupt state into a quiet skip of the
            // whole drift pass.
            var quiet =
                $"{name}: a state file that exists but will not parse must fail loudly with a DocuMe "
                + "annotation, not fall through to the empty-baseline path.";

            baseline.Run.ShouldContain("if ! sha=$(jq -r '.baselineSha // empty' \"$state\"); then", customMessage: quiet);
            baseline.Run.ShouldContain(Annotation, customMessage: quiet);

            var warning = steps.SingleOrDefault(
                s => s.If.Contains($"steps.{baseline.Id}.outputs.sha == ''", StringComparison.Ordinal));

            warning.ShouldNotBeNull(
                $"{name} reads a baseline sha but has no step saying what an empty one means.");
        }
    }

    [Fact]
    public void A_state_file_nothing_has_committed_yet_still_reaches_the_PR()
    {
        // Both templates carry the state file into the docs/sync PR, and until that PR is merged the path
        // is UNTRACKED on the default branch — which is the window this test is about, because the two
        // templates were not reading it the same way. Measured, not reasoned about
        // (.mtk/paths-64/probe-guard.mjs): `git diff --quiet -- <path>` reports SUCCESS for a file git has
        // never tracked, because an untracked file is in no diff. `git status --porcelain` prints `??`.
        // On a tracked-and-unmodified file both report nothing, so the porcelain spelling is a strict
        // improvement rather than a trade.
        //
        // What the diff spelling cost: a six-hourly cron whose whole job is to make a reviewer's label
        // durable read a brand-new state.json as "No label changes and no new feedback" and exited 0. The
        // approvals it had just read were dropped on the runner, every run, for as long as the PR sat
        // unmerged — and the header of docs-sync.yml names the consequence: an approval state does not
        // know about is re-derived with this run's timestamp, so the dashboard spends a page version per
        // run. Silent, and the annotation actively said the opposite.
        foreach (var (name, job) in new[] { ("docs-publish.yml", "publish"), ("docs-sync.yml", "sync") })
        {
            var carry = Steps(name, job)
                .Single(step => step.Run.Contains("git add \"$state\"", StringComparison.Ordinal));

            var blind =
                $"{name}: the \"{carry.Name}\" step must decide whether there is anything to commit with "
                + "`git status --porcelain`, which sees a file git does not track yet. `git diff --quiet` "
                + "reports success for an untracked state.json, so the first run on a repo whose docs/sync "
                + "PR is still open throws its own state file away and says nothing was there.";

            carry.Run.ShouldContain("git status --porcelain -- \"$state\"", customMessage: blind);
            carry.Run.ShouldNotContain("git diff --quiet -- \"$state\"", customMessage: blind);

            // And the switch has to survive what the guard now lets through. Same probe
            // (.mtk/paths-64/probe-collision.mjs): with the state file untracked here and tracked on
            // origin/docs/sync — one repository, mid-window, which is the ordinary case — a plain
            // `git checkout -B` refuses with "untracked working tree files would be overwritten" and the
            // job goes red. `-f` clobbers the collision, which is safe precisely because the step copied
            // the file aside first and copies it back after. Fixing the guard without this would trade a
            // silent skip for a red cron job.
            var refuses =
                $"{name}: the \"{carry.Name}\" step must force the branch switch (`git checkout -f -B`). "
                + "The state file it carries can be untracked here and tracked on the branch it switches "
                + "to, and an unforced switch refuses rather than overwrite it — after the copy-aside has "
                + "already made the overwrite harmless.";

            carry.Run.ShouldContain("git checkout -f -B \"$STATE_BRANCH\" \"origin/$STATE_BRANCH\"", customMessage: refuses);
            carry.Run.ShouldContain("git checkout -f -B \"$STATE_BRANCH\"", customMessage: refuses);
        }
    }

    [Fact]
    public void The_sticky_comment_marker_matches_the_one_the_CLI_writes()
    {
        var env = Mapping(Mapping(Mapping(Root("docs-drift-pr.yml"), "jobs"), "comment"), "env");
        var marker = env.Children
            .Single(child => IsKey(child.Key, "MARKER"))
            .Value;

        // The coupling this test exists for. The CLI guarantees one thing about the comment body — that
        // DriftComment.Marker is its first line — and the workflow's whole stickiness rests on matching
        // it. Change the constant without this assertion and nothing breaks loudly: the job stops
        // finding its own comment and starts posting a new one on every push.
        Scalar(marker).ShouldBe(DriftComment.Marker);
    }

    [Fact]
    public void Every_model_run_looks_for_the_branch_its_own_skill_pushes()
    {
        foreach (var name in ModelDriven)
        {
            var skill = SkillName(name);
            var families = BranchFamilies(name);

            // Vacuous-pass guard: these two templates confirm their model run did something by listing
            // `refs/heads/<family>*` on origin, so a template with no family left to read has lost the
            // check entirely rather than passed it.
            var lost = $"{name} names no refs/heads/ branch family, so nothing tells a run that opened a "
                + "PR from one that did nothing.";

            families.ShouldNotBeEmpty(lost);

            // One family per template, and the ls-remote pattern, the second ls-remote and the `grep -o`
            // must all be the same one. The two templates' branch blocks are verbatim twins apart from
            // this string, which is exactly the shape a copy-paste swaps.
            var swapped = $"{name} greps for more than one branch family ({string.Join(", ", families)}). "
                + "Its before-list, its after-list and its `grep -o` must all name the same one, or the "
                + "diff compares two different questions.";

            families.Distinct(StringComparer.Ordinal).Count().ShouldBe(1, swapped);

            // The coupling that makes this load-bearing, and the direction SkillContractTests cannot
            // check: `Every_skill_names_the_branch_its_PR_is_opened_on` pins the prefix in the SKILL.md,
            // and nothing pinned the copy in the yaml. Edit the template's pattern (or swap the two
            // templates') and every test stays green while the job warns "no branch was pushed" on every
            // run that pushed one — a false alarm on success, which is worse than a missed one because it
            // teaches the team to ignore the annotation.
            var uncoupled = $"{name} looks for '{families[0]}' branches but plugin/skills/{skill}/SKILL.md "
                + "never names that prefix, so the branch the skill pushes is not the branch the workflow "
                + "looks for (rule §8.4).";

            SkillText(skill).ShouldContain(families[0], customMessage: uncoupled);
        }
    }

    /// <summary>
    /// <c>docs-publish.yml</c> installs both halves of the mermaid toolchain, because publish is the one
    /// scaffolded job that renders a diagram.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression test for a bug that shipped: this template set up neither Node nor
    /// <c>beautiful-mermaid</c>, so a consumer whose wiki held one <c>```mermaid</c> fence had every
    /// publish die on the renderer's exit 3. It failed loudly, which is why nothing here caught it — the
    /// suite reads <c>run:</c> blocks, and what was missing was a step, not a wrong line. It also read as
    /// deliberate from the outside: <c>docs-refresh.yml</c> installs Node and says in a comment that the
    /// renderer "runs in the publish path, not here", and <c>init</c> gitignores the very
    /// <c>node_modules/</c> the publish path needs.
    /// </para>
    /// <para>
    /// Asserted as an ordering against the publish step rather than as presence, because a toolchain
    /// installed after the command that uses it is the same outage with a longer log.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_publish_template_installs_the_toolchain_its_render_path_shells_out_to()
    {
        const string name = "docs-publish.yml";
        var steps = Steps(name, "publish");
        var publish = steps.FindIndex(step => step.Run.Contains($"{ToolRun} publish", StringComparison.Ordinal));

        publish.ShouldBeGreaterThanOrEqualTo(0, $"{name} no longer runs `{ToolRun} publish`.");

        var node = StepInputs(name).ToList();
        var installsNode = node.Any(input => input.Uses.StartsWith("actions/setup-node@", StringComparison.Ordinal));

        installsNode.ShouldBeTrue(
            $"{name} runs the only DocuMe command that shells out to Node (PLAN.md §6.2 step 3) without "
            + "installing one, so the version rendering a consumer's diagrams is whatever the runner "
            + "image happens to ship.");

        var install = steps.FindIndex(step => step.Run.Contains(MermaidPackage, StringComparison.Ordinal));
        const string uninstalled = $"{name} never installs {MermaidPackage}. `docume init` gitignores node_modules/ "
            + "rather than populating it, so on a runner the renderer's `import('beautiful-mermaid')` finds "
            + "nothing and exit 3 fails the whole publish for any wiki holding one ```mermaid fence.";

        install.ShouldBeGreaterThanOrEqualTo(0, uninstalled);

        install.ShouldBeLessThan(
            publish,
            $"{name} installs the renderer's dependency after the publish that needs it.");
    }

    /// <summary>
    /// The dependency <c>docs-publish.yml</c> installs is the one the shipped render script is written
    /// against, and is pinned rather than floating.
    /// </summary>
    /// <remarks>
    /// <c>beautiful-mermaid</c> is a reimplementation of mermaid that accepts a subset of the dialect, and
    /// the subset moves between releases (the script's own header records <c>pie</c> and <c>graph TD;</c>
    /// as rejected at 1.1.3). A template floating the version would change which of a consumer's diagrams
    /// render, on a nightly, with no commit to blame — and a template pinned to a *different* version than
    /// the script was verified against is the same surprise with an audit trail that lies.
    /// </remarks>
    [Fact]
    public void The_renderer_dependency_is_pinned_to_the_version_the_render_script_was_written_against()
    {
        var pinned = PinnedMermaidVersion();
        var installed = Runnable("docs-publish.yml")
            .Where(line => line.Contains(MermaidPackage, StringComparison.Ordinal))
            .ToList();

        installed.ShouldNotBeEmpty($"docs-publish.yml installs no {MermaidPackage}.");

        foreach (var line in installed)
        {
            var at = line.IndexOf($"{MermaidPackage}@", StringComparison.Ordinal);

            at.ShouldBeGreaterThanOrEqualTo(
                0,
                $"docs-publish.yml installs {MermaidPackage} without pinning a version:\n{line.Trim()}");

            var tail = line[(at + MermaidPackage.Length + 1)..];
            var end = tail.IndexOfAny([' ', '\'', '"']);
            var version = end < 0 ? tail.TrimEnd() : tail[..end];
            var drifted = $"docs-publish.yml installs {MermaidPackage}@{version}, but package.json pins "
                + $"{pinned} — the version templates/tools/render-mermaid.mjs is written against.";

            version.ShouldBe(pinned, drifted);
        }
    }

    /// <summary>
    /// Every template installs an SDK that can run the tool it then restores.
    /// </summary>
    /// <remarks>
    /// The band is derived from <c>Directory.Build.props</c> rather than written down a seventh time. A
    /// consumer's runner has whatever SDK its image ships; these templates are the only thing that puts
    /// the right one there, and <c>dotnet tool restore</c> against a manifest targeting a newer framework
    /// fails with the SDK's own wording, in someone else's CI. Nothing coupled the two until now — every
    /// <c>with:</c> value in these templates was unread by any assertion — so a TFM bump would have left
    /// six templates installing an SDK too old to run what they restore.
    /// </remarks>
    [Fact]
    public void Every_template_installs_an_SDK_that_can_run_the_tool_this_repo_builds()
    {
        var expected = SdkBand();
        var installed = Templates
            .SelectMany(name => StepInputs(name)
                .Where(input => input.Uses.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal))
                .Where(input => string.Equals(input.Key, "dotnet-version", StringComparison.Ordinal))
                .Select(input => (Name: name, input.Setting)))
            .ToList();

        // Vacuous-pass guard: all six restore the pinned tool, so one installing no SDK at all has lost
        // the step rather than satisfied this.
        var covered = installed.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).ToList();
        var silent = Templates.Except(covered, StringComparer.Ordinal).ToList();

        silent.ShouldBeEmpty(
            $"Template(s) restoring `{ToolRestore}` without installing an SDK: {string.Join(", ", silent)}.");

        var wrong = installed
            .Where(entry => !string.Equals(entry.Setting, expected, StringComparison.Ordinal))
            .Select(entry => $"{entry.Name} installs {entry.Setting}")
            .ToList();

        wrong.ShouldBeEmpty(
            $"DocuMe targets {TargetFramework()}, so a scaffolded workflow must install {expected}: "
            + string.Join("; ", wrong));
    }

    /// <summary>
    /// The SDK band really is derived from the TFM, not a hardcoded <c>10.0.x</c> that happens to be right
    /// today.
    /// </summary>
    /// <remarks>
    /// The assertion above cannot be mutation-tested the obvious way: bumping
    /// <c>Directory.Build.props</c> to a framework this machine has no targeting pack for stops the suite
    /// from running at all, so "did it go red" has no answer (<c>.mtk/paths-81/mutate-tfm.mjs</c> records
    /// the attempt). This covers the same ground by asserting the mapping directly — replace the
    /// derivation with a constant and the <c>net11.0</c> case fails here.
    /// </remarks>
    [Theory]
    [InlineData("net10.0", "10.0.x")]
    [InlineData("net11.0", "11.0.x")]
    [InlineData("net9.0", "9.0.x")]
    public void The_SDK_band_follows_from_the_TFM(string tfm, string expected)
        => BandOf(tfm).ShouldBe(expected);

    /// <summary>
    /// Any Node a template installs satisfies the renderer's floor.
    /// </summary>
    /// <remarks>
    /// PLAN.md §4 and <c>MermaidRenderer</c>'s own diagnostic both name Node ≥ 20. Asserted over every
    /// template rather than the two that name a version today, so the floor covers the next one too.
    /// </remarks>
    [Fact]
    public void Every_Node_a_template_installs_can_run_the_mermaid_renderer()
    {
        const int floor = 20;
        var stale = new List<string>();

        foreach (var name in Templates)
        {
            var versions = StepInputs(name)
                .Where(input => input.Uses.StartsWith("actions/setup-node@", StringComparison.Ordinal))
                .Where(input => string.Equals(input.Key, "node-version", StringComparison.Ordinal))
                .Select(input => input.Setting);

            foreach (var version in versions)
            {
                var major = version.Split('.')[0];

                if (int.TryParse(major, out var parsed) && parsed >= floor)
                {
                    continue;
                }

                stale.Add($"{name} installs Node {version}");
            }
        }

        stale.ShouldBeEmpty(
            $"PLAN.md §4 requires Node ≥ {floor} for the mermaid renderer: {string.Join("; ", stale)}.");
    }

    /// <summary>
    /// The branch prefixes <paramref name="name"/> asks git about — one entry per <c>refs/heads/</c>
    /// mention in a line a runner acts on, in file order.
    /// </summary>
    /// <remarks>
    /// Deliberately not a regex: the two spellings differ only in their glob (<c>-*</c> for ls-remote,
    /// <c>-.*</c> for <c>grep -o</c>), so truncating at the first glob or quote character reads both and
    /// keeps the assertion free of the <c>[GeneratedRegex]</c> ceremony the analyzers demand.
    /// </remarks>
    private static List<string> BranchFamilies(string name)
    {
        const string marker = "refs/heads/";
        var families = new List<string>();

        foreach (var line in Runnable(name))
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var tail = line[(at + marker.Length)..];
            var end = tail.IndexOfAny(['*', '.', '\'', '"', ' ']);
            families.Add(end < 0 ? tail : tail[..end]);
        }

        return families;
    }

    /// <summary>
    /// The comma-separated items inside the single-quoted value of <paramref name="flag"/> in
    /// <paramref name="text"/>, trimmed. Empty when the flag is absent or unquoted.
    /// </summary>
    /// <remarks>
    /// Single quotes only, deliberately: that is how both templates spell it, and the quoting is not
    /// incidental — an unquoted <c>Bash,Read,…</c> would still work today and would break the first time
    /// a tool name gained a character the shell cares about. Returning empty for the unquoted spelling
    /// makes the caller's vacuous-pass guard fire rather than silently comparing nothing.
    /// </remarks>
    private static string[] QuotedFlagValue(string text, string flag)
    {
        var at = text.IndexOf($"{flag} '", StringComparison.Ordinal);
        if (at < 0)
        {
            return [];
        }

        var open = at + flag.Length + 2;
        var close = text.IndexOf('\'', open);

        return close < 0
            ? []
            : Items(text[open..close]);
    }

    /// <summary>
    /// The comma-separated items of <paramref name="key"/> in the yaml frontmatter of
    /// <paramref name="markdown"/>. Empty when the key is absent.
    /// </summary>
    /// <remarks>
    /// A line match rather than a yaml parse, because <c>allowed-tools</c> is spelled as an inline scalar
    /// (<c>Bash, Read, …</c>) in all three SKILL.md files, and the point of comparing it to a flag value
    /// is that both are the same flat list of names.
    /// </remarks>
    private static string[] FrontmatterValue(string markdown, string key)
    {
        var line = markdown
            .Split('\n')
            .TakeWhile((text, index) => index == 0 || !string.Equals(text.Trim(), "---", StringComparison.Ordinal))
            .FirstOrDefault(text => text.StartsWith($"{key}:", StringComparison.Ordinal));

        return line is null
            ? []
            : Items(line[(key.Length + 1)..]);
    }

    private static string[] Items(string list) => list
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Every repository <paramref name="name"/> hands to a checkout step, read out of the step's
    /// <c>with</c> mapping. Structural rather than a text match on <c>repository:</c>, because these
    /// templates explain themselves at length and a comment naming a repo is not a checkout.
    /// </summary>
    private static IEnumerable<string> CheckedOutRepositories(string name)
    {
        var jobs = Mapping(Root(name), "jobs").Children.Select(child => Mapping(child.Value));

        foreach (var job in jobs)
        {
            if (job.Children.FirstOrDefault(child => IsKey(child.Key, "steps")).Value
                is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (var step in steps.Children.OfType<YamlMappingNode>())
            {
                if (step.Children.FirstOrDefault(child => IsKey(child.Key, "with")).Value
                    is not YamlMappingNode inputs)
                {
                    continue;
                }

                var repository = Value(inputs, "repository");

                if (repository.Length > 0)
                {
                    yield return repository;
                }
            }
        }
    }

    /// <summary>
    /// <c>owner/repo</c> as <c>plugin/.claude-plugin/plugin.json</c> declares it: the single spelling
    /// <see cref="Config.ConfigSchemaTests"/> already pins the scaffolded <c>$schema</c> URL to.
    /// </summary>
    private static string DeclaredRepository()
    {
        var path = Path.GetFullPath(
            Path.Combine(Directory, "..", "..", "plugin", ".claude-plugin", "plugin.json"));
        var url = JsonNode.Parse(File.ReadAllText(path))!["repository"]!.GetValue<string>();

        const string prefix = "https://github.com/";
        url.ShouldStartWith(prefix, Case.Sensitive, "plugin.json's repository is not a github.com URL.");

        return url[prefix.Length..].TrimEnd('/');
    }

    private static string Directory { get; } = Locate();

    /// <summary>The repository root, which both <see cref="Directory"/> and the tree below sit under.</summary>
    private static string RepoRoot { get; } = Path.GetFullPath(Path.Combine(Directory, "..", ".."));

    /// <summary>
    /// Every <c>with:</c> input a template hands to a <c>uses:</c> step, across every job, tagged with the
    /// action it configures.
    /// </summary>
    /// <remarks>
    /// Read structurally, like <see cref="CheckedOutRepositories"/> and for the same reason: these
    /// templates carry a lot of prose, and a comment naming a version is not a step installing one. This
    /// exists because an audit of the shipped templates found every <c>with:</c> mapping in them unread by
    /// any assertion — the tests here checked <c>run:</c> blocks, <c>env:</c>, <c>on:</c> and step order,
    /// and nothing looked at what a <c>uses:</c> step was configured with.
    /// </remarks>
    private static IEnumerable<StepInput> StepInputs(string name)
    {
        var jobs = Mapping(Root(name), "jobs").Children.Select(child => Mapping(child.Value));

        foreach (var job in jobs)
        {
            if (job.Children.FirstOrDefault(child => IsKey(child.Key, "steps")).Value
                is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (var step in steps.Children.OfType<YamlMappingNode>())
            {
                var uses = Value(step, "uses");

                if (uses.Length is 0)
                {
                    continue;
                }

                if (step.Children.FirstOrDefault(child => IsKey(child.Key, "with")).Value
                    is not YamlMappingNode inputs)
                {
                    continue;
                }

                foreach (var input in inputs.Children)
                {
                    yield return new StepInput(uses, Scalar(input.Key), Scalar(input.Value));
                }
            }
        }
    }

    /// <summary>The TFM every project in the tree builds against, from the one place that declares it.</summary>
    private static string TargetFramework()
    {
        var props = Path.Combine(RepoRoot, "Directory.Build.props");
        var declared = PropsValue(props, nameof(TargetFramework));

        declared.ShouldNotBeNullOrEmpty($"No <TargetFramework> in {props}.");

        return declared;
    }

    /// <summary>
    /// The <c>actions/setup-dotnet</c> band that can run <see cref="TargetFramework"/>: <c>net10.0</c>
    /// needs <c>10.0.x</c>. Derived rather than written down, so a TFM bump moves the assertion with it.
    /// </summary>
    private static string SdkBand() => BandOf(TargetFramework());

    /// <summary>
    /// <c>net10.0</c> → <c>10.0.x</c>. Split out from <see cref="SdkBand"/> so the derivation can be
    /// asserted on a TFM this machine has no targeting pack for.
    /// </summary>
    private static string BandOf(string tfm)
    {
        var wrong = $"'{tfm}' is not a .NET 5+ TFM, so no SDK band follows from it.";

        tfm.ShouldStartWith("net", Case.Sensitive, wrong);
        tfm.ShouldContain(".", customMessage: wrong);

        return $"{tfm["net".Length..]}.x";
    }

    /// <summary>
    /// The <c>beautiful-mermaid</c> version <c>package.json</c> pins — which exists, by its own
    /// description, only to record the version <c>templates/tools/render-mermaid.mjs</c> is written
    /// against.
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
    /// The text inside the first <c>&lt;element&gt;</c> of an msbuild props file. A three-line reader
    /// rather than an XML parse: one element is wanted, and the alternative pulls a document model in to
    /// answer a question a substring settles.
    /// </summary>
    private static string PropsValue(string path, string element)
    {
        var xml = File.ReadAllText(path);
        var open = $"<{element}>";
        var at = xml.IndexOf(open, StringComparison.Ordinal);

        if (at < 0)
        {
            return string.Empty;
        }

        var tail = xml[(at + open.Length)..];
        var end = tail.IndexOf('<', StringComparison.Ordinal);

        return end < 0 ? string.Empty : tail[..end].Trim();
    }

    private static string Text(string name) => File.ReadAllText(Path.Combine(Directory, name));

    /// <summary>
    /// The SKILL.md of <paramref name="skill"/>. Derived from <see cref="Directory"/> rather than located
    /// again: both live under the repository root this class already found.
    /// </summary>
    private static string SkillText(string skill) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(Directory, "..", "..", "plugin", "skills", skill, "SKILL.md")));

    /// <summary>
    /// The lines of <paramref name="name"/> that a runner acts on: everything except a whole-line
    /// comment. These templates carry a lot of prose — the reasoning is the point of a file somebody
    /// copies into their own repo — and an assertion that grepped the prose too would push the next
    /// editor to explain less.
    /// </summary>
    private static IEnumerable<string> Runnable(string name) => Text(name)
        .Split('\n')
        .Where(line => !line.TrimStart().StartsWith('#'));

    private static YamlMappingNode Root(string name)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(Text(name));

        // Load, not Deserialize: a syntax or indentation error throws here, which is the failure mode
        // hand-written yaml actually has.
        stream.Load(reader);

        stream.Documents.Count.ShouldBe(1, $"{name} should be one yaml document.");

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static IEnumerable<string> Keys(YamlMappingNode node)
        => node.Children.Select(child => Scalar(child.Key));

    /// <summary>
    /// The steps of <paramref name="job"/>, in the order a runner executes them.
    /// </summary>
    /// <remarks>
    /// Read structurally rather than by searching the file text, because the assertions that use this
    /// are about order and about which step owns which line — and a step's <c>run</c> block is the only
    /// place where "after" means "after". Two commands in one script would look ordered to a text search
    /// while being one step to a runner, and a step's <c>if</c> would look like prose.
    /// </remarks>
    private static List<WorkflowStep> Steps(string template, string job)
    {
        var steps = Mapping(Mapping(Root(template), "jobs"), job).Children
            .Single(child => IsKey(child.Key, "steps"))
            .Value;

        return ((YamlSequenceNode)steps).Children
            .OfType<YamlMappingNode>()
            .Select(step => new WorkflowStep(
                Value(step, "id"),
                Value(step, "name"),
                Value(step, "run"),
                Value(step, "if")))
            .ToList();
    }

    /// <summary>
    /// The steps of <paramref name="name"/> that invoke the model, as their yaml mappings, across every
    /// job in the file.
    /// </summary>
    /// <remarks>
    /// Found by what the step runs rather than by its name, because the two templates name theirs
    /// differently ("Refresh the drifted pages", "Triage the feedback") and a name is the one part of a
    /// step an editor is free to reword.
    /// </remarks>
    private static List<YamlMappingNode> ModelRunSteps(string name) =>
        Mapping(Root(name), "jobs").Children
            .Select(job => Mapping(job.Value).Children.FirstOrDefault(child => IsKey(child.Key, "steps")).Value)
            .OfType<YamlSequenceNode>()
            .SelectMany(steps => steps.Children.OfType<YamlMappingNode>())
            .Where(step => ModelInvocations.Any(
                cli => Value(step, "run").Contains(cli, StringComparison.Ordinal)))
            .ToList();

    /// <summary>
    /// How each rail spells "run the model". Matched against the step's <c>run:</c> so a template that
    /// renamed its step, or moved the invocation into a script, stops being found — which is what the
    /// vacuous-pass guards at each call site are there to report.
    /// </summary>
    private static readonly string[] ModelInvocations = ["claude -p", "copilot -p"];

    /// <summary>The scalar at <paramref name="key"/>, or empty when the step does not carry it.</summary>
    private static string Value(YamlMappingNode node, string key)
        => node.Children.FirstOrDefault(child => IsKey(child.Key, key)).Value is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
        => (YamlMappingNode)parent.Children.Single(child => IsKey(child.Key, key)).Value;

    private static YamlMappingNode Mapping(YamlNode node) => (YamlMappingNode)node;

    private static bool IsKey(YamlNode node, string name)
        => string.Equals(Scalar(node), name, StringComparison.Ordinal);

    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value ?? string.Empty;

    /// <summary>
    /// The templates live in the tree rather than beside the test assembly: M6's <c>init</c> scaffolds
    /// these exact files, so the test has to read the shipped copy and not a build artifact of it.
    /// </summary>
    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return Path.Combine(directory.FullName, "templates", "workflows");
            }
        }

        // Not a skip: the templates are committed, so a run that cannot find them is a broken run and
        // saying "0 templates checked, all green" would be the worse answer.
        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so templates/workflows cannot be found.");
    }

    /// <summary>One step of a workflow job — the four keys the assertions here read.</summary>
    private sealed record WorkflowStep(string Id, string Name, string Run, string If);

    /// <summary>One <c>with:</c> input, and the <c>uses:</c> step it configures.</summary>
    private sealed record StepInput(string Uses, string Key, string Setting);
}
