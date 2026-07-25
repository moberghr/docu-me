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
    /// <summary>The templates §14 names, by the filenames M6's <c>init</c> will scaffold.</summary>
    private static readonly string[] Templates =
    [
        "docs-drift.yml",
        "docs-drift-pr.yml",
        "docs-publish.yml",
        "docs-sync.yml",
        "docs-refresh.yml",
        "docs-feedback.yml",
    ];

    /// <summary>The templates that talk to Confluence, and so need both credential variables.</summary>
    private static readonly string[] ConfluenceFacing =
    [
        "docs-drift.yml",
        "docs-publish.yml",
        "docs-sync.yml",
    ];

    /// <summary>
    /// The templates that run a model (§11's headless <c>claude -p</c>). Their output is a PR, so none of
    /// them writes to Confluence and none of them holds a credential.
    /// </summary>
    private static readonly string[] ModelDriven = ["docs-refresh.yml", "docs-feedback.yml"];

    /// <summary>
    /// The templates whose only <c>docume</c> invocation happens inside the skill they run, so the literal
    /// command line is not in the yaml.
    /// </summary>
    private static readonly string[] SkillDriven = ["docs-feedback.yml"];

    private const string CredentialPrefix = "DOCUME_CONFLUENCE_";
    private const string ToolRestore = "dotnet tool restore";
    private const string ToolRun = "dotnet tool run docume";

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

            text.ShouldContain("--permission-mode", customMessage: $"{name} runs a model with no permission mode (§11).");
            text.ShouldNotContain(
                "--dangerously-skip-permissions",
                customMessage: $"{name}: never in a template — this job can push branches and open PRs.");

            // Rule §1.1 again, for the key only these templates carry.
            var apiKey = runnable
                .Where(line => line.Contains("ANTHROPIC_API_KEY", StringComparison.Ordinal))
                .Where(line => line.Contains(':', StringComparison.Ordinal))
                .ToList();

            apiKey.ShouldNotBeEmpty($"{name} runs a model without an API key.");
            apiKey.ShouldAllBe(
                line => line.Contains("secrets.", StringComparison.Ordinal),
                $"{name}: the API key must come from `${{{{ secrets.… }}}}`.");
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
    public void The_feedback_template_triggers_on_the_inbox_it_triages()
    {
        const string name = "docs-feedback.yml";
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
        text.ShouldContain(
            "git add \"$state\" \"$inbox\"",
            customMessage: $"{name} must stage the inbox items alongside the state file (§9).");
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

    private static string Directory { get; } = Locate();

    private static string Text(string name) => File.ReadAllText(Path.Combine(Directory, name));

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
}
