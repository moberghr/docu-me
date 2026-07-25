using DocuMe.Core.Drift;
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
    ];

    /// <summary>The templates that talk to Confluence, and so need both credential variables.</summary>
    private static readonly string[] ConfluenceFacing =
    [
        "docs-drift.yml",
        "docs-publish.yml",
        "docs-sync.yml",
    ];

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

            run.ShouldBeGreaterThan(-1, $"{name} never invokes the tool.");
            restore.ShouldBeGreaterThan(-1, $"{name} runs docume without `{ToolRestore}` (§12).");
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
    public void The_refresh_template_keeps_the_permission_gate_on_its_model_run()
    {
        const string name = "docs-refresh.yml";
        var runnable = Runnable(name).ToList();

        // §11's headless invocation is `claude -p "/docs-refresh" --permission-mode acceptEdits`. The
        // flag is the assertion, not the mode: this is the one template that hands an unattended model a
        // token that can push branches and open PRs, and `--dangerously-skip-permissions` would remove
        // the only thing standing between the two. A template is the worst place to lose that, because
        // whoever copies it will not re-derive why it was there.
        var text = string.Join('\n', runnable);

        text.ShouldContain("--permission-mode", customMessage: $"{name} runs a model with no permission mode (§11).");
        text.ShouldNotContain(
            "--dangerously-skip-permissions",
            customMessage: $"{name}: never in a template — this job can push branches and open PRs.");

        // Rule §1.1 again, for the key this template alone carries.
        var apiKey = runnable
            .Where(line => line.Contains("ANTHROPIC_API_KEY", StringComparison.Ordinal))
            .Where(line => line.Contains(':', StringComparison.Ordinal))
            .ToList();

        apiKey.ShouldNotBeEmpty($"{name} runs a model without an API key.");
        apiKey.ShouldAllBe(
            line => line.Contains("secrets.", StringComparison.Ordinal),
            $"{name}: the API key must come from `${{{{ secrets.… }}}}`.");
    }

    [Fact]
    public void The_refresh_template_neither_publishes_nor_holds_a_Confluence_credential()
    {
        // Rule §0.4 as a file assertion. The refresh skill's output is a PR; publishing happens later,
        // behind a human's merge, in docs-publish.yml. So the nightly unattended model run has no reason
        // to hold the publishing token — and once it holds none, a run that tried to publish anyway
        // fails on credentials rather than writing to Confluence.
        var text = Text("docs-refresh.yml");

        text.ShouldNotContain(
            CredentialPrefix,
            customMessage: "The refresh job publishes nothing — keep the Confluence token out of it.");

        var publishes = Runnable("docs-refresh.yml")
            .Where(line => line.Contains("docume publish", StringComparison.Ordinal))
            .ToList();

        publishes.ShouldBeEmpty("A refresh opens a PR; docs-publish.yml publishes it after merge (§10).");
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
