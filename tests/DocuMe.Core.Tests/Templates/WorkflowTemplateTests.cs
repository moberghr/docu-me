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

    /// <summary>
    /// The templates that read <c>docume.json</c> to find the wiki root, and the job each does it in.
    /// Five of the six: <c>docs-drift-pr.yml</c> takes its base ref from the pull-request event instead.
    /// </summary>
    private static readonly (string Template, string Job)[] ConfigReaders =
    [
        ("docs-publish.yml", "publish"),
        ("docs-sync.yml", "sync"),
        ("docs-drift.yml", "mark"),
        ("docs-refresh.yml", "refresh"),
        ("docs-feedback.yml", "feedback"),
    ];

    private const string CredentialPrefix = "DOCUME_CONFLUENCE_";
    private const string ToolRestore = "dotnet tool restore";
    private const string ToolRun = "dotnet tool run docume";
    private const string ConfigRead = "jq -r '.wiki.root // \"docs/wiki\"' docume.json";
    private const string ConfigGuard = "if [ ! -f docume.json ]; then";
    private const string Annotation = "::error title=DocuMe::";

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

        text.ShouldContain("git add \"$state\" \"$inbox\" \"$archive\"", customMessage: staged);

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
        foreach (var (name, job) in new[] { ("docs-drift.yml", "mark"), ("docs-refresh.yml", "refresh") })
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
            var skill = Path.GetFileNameWithoutExtension(name);
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

    private static string Directory { get; } = Locate();

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
}
