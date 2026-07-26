using System.Text.RegularExpressions;
using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// PLAN.md §10's drift-and-refresh bullets, each held against the workflow step that <em>performs</em> it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap this closes.</strong> <c>WorkflowTemplateTests</c> checks the six templates against a
/// hard-coded list of filenames and a set of mistakes worth catching — a flag the CLI lacks, a missing
/// <c>dotnet tool restore</c>, an inlined credential. What no test reads is §10 itself. Its six bullets
/// each promise a behaviour, and a promise whose step was never written looks exactly like a promise whose
/// step is there: the plan is prose, and prose asserts nothing. <see cref="PlanCommandSpecTests"/> found
/// <c>--notify-reviewers</c> that way in §6 and <see cref="PlanDataContractTests"/> found two dead knobs
/// that way in §5.
/// </para>
/// <para>
/// <strong>A site is the step that does it.</strong> Every claim names a file <em>and</em> a pattern that
/// has to match inside it — a trigger block, a command line, a step condition, a step ordering. The
/// presence of a template is never the evidence; <c>WorkflowTemplateTests</c> already owns that, and a
/// template can exist while the step a bullet promises does not. <see cref="Site.Absent"/> inverts the
/// ones §10 states negatively: <c>docs-drift.yml</c> is "seconds, no LLM", the PR comment is
/// "non-blocking", and <c>baselineSha</c> has no CLI writer at all.
/// </para>
/// <para>
/// <strong>Keyed on the plan's own words.</strong> A claim matches its bullet by a distinctive phrase
/// lifted from §10, not by ordinal, so reordering the section costs nothing while <em>adding</em> a bullet
/// fails — the new promise matches no claim, which is the case that matters.
/// </para>
/// <para>
/// <strong>What this cannot see.</strong> A pattern proves the step is written, not that it runs green in
/// a consumer's repository. <c>WorkflowShellTests</c> executes the shell, and the M5 round trip behind
/// <c>gate-m5-refresh-roundtrip</c> is what closes the rest.
/// </para>
/// </remarks>
public sealed partial class PlanWorkflowTraceTests
{
    /// <summary>One file, and the construct in it that carries out a promise.</summary>
    private sealed record Site
    {
        /// <summary>Repo-relative path.</summary>
        public required string File { get; init; }

        /// <summary>A regex that must match the file's text — the step doing the work.</summary>
        public required string Performs { get; init; }

        /// <summary>
        /// When <c>true</c>, the pattern must <em>not</em> match: §10 states the promise negatively, and
        /// its absence is the only evidence there is.
        /// </summary>
        public bool Absent { get; init; }
    }

    /// <summary>One §10 bullet or numbered step, bound to the workflow that performs it.</summary>
    private sealed record Claim
    {
        /// <summary>Label used in failure messages only; nothing is keyed on it.</summary>
        public required string Id { get; init; }

        /// <summary>A phrase from §10's own text, distinctive enough to name one unit.</summary>
        public required string Lead { get; init; }

        /// <summary>Every file that performs a part of this claim.</summary>
        public required Site[] Sites { get; init; }
    }

    /// <summary>
    /// A bullet whose prose states more than the shipped templates deliver. Recorded rather than
    /// tolerated: the sites still have to match, so the gap cannot drift without failing.
    /// </summary>
    private sealed record PlanDeviation
    {
        /// <summary>The <see cref="Claim.Id"/> it belongs to.</summary>
        public required string Id { get; init; }

        /// <summary>What the plan overstates, and what correcting it would take.</summary>
        public required string Why { get; init; }
    }

    private const string DriftYml = "templates/workflows/docs-drift.yml";
    private const string DriftPrYml = "templates/workflows/docs-drift-pr.yml";
    private const string PublishYml = "templates/workflows/docs-publish.yml";
    private const string SyncYml = "templates/workflows/docs-sync.yml";
    private const string RefreshYml = "templates/workflows/docs-refresh.yml";
    private const string FeedbackYml = "templates/workflows/docs-feedback.yml";
    private const string RefreshSkill = "plugin/skills/docs-refresh/SKILL.md";

    private static readonly Claim[] Claims =
    [
        new()
        {
            Id = "§10 deploy-triggered marking",
            Lead = "stale labels + dashboard update",
            Sites =
            [

                // The trigger the bullet names, not merely a workflow called docs-drift.
                new() { File = DriftYml, Performs = @"^on:\n  workflow_run:" },
                new() { File = DriftYml, Performs = @"docume drift --baseline [^\n]*--mark" },

                // "+ dashboard update" is the CLI's half: --mark refreshes §6.5 in the same run, which is
                // why the workflow has no second step for it.
                new()
                {
                    File = "src/DocuMe.Cli/Commands/DriftCommand.cs",
                    Performs = @"await RefreshDashboardAsync\(",
                },

                // "Seconds, no LLM" — the one template on the deploy path that must never pay for a model.
                new() { File = DriftYml, Performs = @"claude", Absent = true },
            ],
        },
        new()
        {
            Id = "§10 pull-request advisory comment",
            Lead = "sticky PR comment listing affected pages",
            Sites =
            [
                new() { File = DriftPrYml, Performs = @"^on:\n  pull_request:" },
                new() { File = DriftPrYml, Performs = @"docume drift --baseline [^\n]*--format github-comment" },

                // "Sticky": the previous comment is edited in place. A `gh pr comment` with no lookup
                // would post one comment per push, which is the failure the word was written to exclude.
                new() { File = DriftPrYml, Performs = @"gh api -X PATCH" },

                // "Non-blocking", and the CLI does have the flag that would break it (DriftCommand.cs:78).
                // Anchored to an invocation rather than to the bare flag name: the template's own header
                // comment says "no `--fail-on-drift` here", and a pattern that a comment satisfies proves
                // nothing about what the job runs.
                new()
                {
                    File = DriftPrYml,
                    Performs = @"docume drift[^\n]*--fail-on-drift",
                    Absent = true,
                },

                // The deviation's evidence: the shipped baseline is the merge base, not the bullet's
                // `origin/<defaultBranch>`. Pinned so the gap cannot close or widen unnoticed — and
                // anchored to the assignment, because `git merge-base` is named in a comment too.
                new() { File = DriftPrYml, Performs = @"base=\$\(git merge-base" },
            ],
        },
        new()
        {
            Id = "§10 /docs-refresh runs nightly in CI and on demand",
            Lead = "nightly cron in CI via headless Claude",
            Sites =
            [
                new() { File = RefreshYml, Performs = @"cron: '0 3 \* \* \*'" },

                // "and locally on demand" has two halves: a manual CI trigger, and a skill a human can
                // invoke with no workflow at all.
                new() { File = RefreshYml, Performs = @"workflow_dispatch:" },
                new() { File = RefreshSkill, Performs = @"^name: docs-refresh$" },
                new() { File = RefreshYml, Performs = @"claude -p '/docs-refresh'" },
            ],
        },
        new()
        {
            Id = "§10 step 1 — the drift report is the input",
            Lead = "Input: `docume drift --format json`",
            Sites =
            [

                // The workflow asks before it pays for a model, and the skill asks again for the detail.
                new() { File = RefreshYml, Performs = @"docume drift --baseline [^\n]*--format json" },
                new() { File = RefreshSkill, Performs = @"docume drift --format json" },
                new() { File = RefreshSkill, Performs = @"which pages, and which of their globs matched" },
            ],
        },
        new()
        {
            Id = "§10 step 2 — only stale pages, sources followed, baseline bumped",
            Lead = "Regenerate ONLY stale pages",
            Sites =
            [
                new() { File = RefreshSkill, Performs = @"Rewrite only the affected sections" },
                new() { File = RefreshSkill, Performs = @"Update `sources` when the code moved" },

                // "bump `baselineSha`" — and the skill is the only thing entitled to, per this section's
                // own closing paragraph.
                new() { File = RefreshSkill, Performs = @"set `baselineSha` to the" },
            ],
        },
        new()
        {
            Id = "§10 step 3 — a dated PR carrying a summary table",
            Lead = "Output: PR `docs/refresh-<date>`",
            Sites =
            [
                new() { File = RefreshSkill, Performs = @"docs/refresh-\$date" },

                // "(page → what changed → why)", as three columns rather than as prose.
                new()
                {
                    File = RefreshSkill,
                    Performs = @"\| Page \| What changed in the code \| Why the page changed \|",
                },

                // And the workflow notices when the model ran and no such branch appeared.
                new() { File = RefreshYml, Performs = @"refs/heads/docs/refresh-\*" },
            ],
        },
        new()
        {
            Id = "§10 merge to default branch — publish, dashboard, reply, carry",
            Lead = "changed pages republished",
            Sites =
            [
                new() { File = PublishYml, Performs = @"^on:\n  push:" },
                new() { File = PublishYml, Performs = @"paths:\n      - 'docs/wiki/\*\*'" },

                // "--changed-since <state.lastPublishedSha>": the value comes out of the state file, so
                // both halves are the claim — reading it, and passing it.
                new() { File = PublishYml, Performs = @"\.lastPublishedSha // empty" },
                new() { File = PublishYml, Performs = @"docume publish --changed-since [^\n]*\|\| code=\$\?" },
                new() { File = PublishYml, Performs = @"docume dashboard \|\| code=\$\?" },
                new() { File = PublishYml, Performs = @"docume sync --reply \|\| code=\$\?" },

                // "the reply is skipped outright when the publish failed".
                new() { File = PublishYml, Performs = @"if: steps\.publish\.outputs\.code == '0'" },

                // "one step at the end turns any held failure into a red check AFTER the state file is
                // safe". The ordering IS the contract: swap these two steps and a publish that died on
                // page seven throws away the page ids it earned, and the next run creates them again.
                new()
                {
                    File = PublishYml,
                    Performs = @"name: Carry the state file[\s\S]*name: Fail if DocuMe reported a failure",
                },

                // "Toolchain": the renderer's dependency, installed here and nowhere else.
                new() { File = PublishYml, Performs = @"npm install --no-save beautiful-mermaid" },
                new() { File = RefreshYml, Performs = @"beautiful-mermaid", Absent = true },
                new() { File = FeedbackYml, Performs = @"beautiful-mermaid", Absent = true },

                // "`init` gitignores `node_modules/` rather than populating it".
                new()
                {
                    File = "src/DocuMe.Core/Scaffolding/ProjectScaffolder.cs",
                    Performs = @"GitignoreEntry = ""node_modules/""",
                },
            ],
        },
        new()
        {
            Id = "§10 six-hourly sync cron",
            Lead = "opens/updates a `docs/sync` PR",
            Sites =
            [
                new() { File = SyncYml, Performs = @"cron: '0 \*/6 \* \* \*'" },
                new() { File = SyncYml, Performs = @"docume sync \|\| code=\$\?" },
                new() { File = SyncYml, Performs = @"docume dashboard \|\| code=\$\?" },

                // "when state/inbox changed" — the staged diff is the gate, so an idle cron run commits
                // nothing and opens nothing.
                new() { File = SyncYml, Performs = @"git diff --cached --quiet" },

                // "opens/updates": create only when no PR is open on the branch already.
                new() { File = SyncYml, Performs = @"gh pr view [^\n]*\|\| gh pr create" },
            ],
        },
        new()
        {
            Id = "§10 comment triage runs only when there is work",
            Lead = "counts untriaged inbox items",
            Sites =
            [
                new() { File = FeedbackYml, Performs = @"name: Count untriaged feedback" },

                // "when there is work" is a gate in both directions: the model step is conditional, and
                // the idle path says so rather than passing silently. The condition is bound to the step
                // it guards — six steps in this file carry the same `if:`, so an unanchored pattern stays
                // green while the one step that costs money loses its gate.
                new()
                {
                    File = FeedbackYml,
                    Performs = @"name: Triage the feedback\n        if: steps\.inbox\.outputs\.work == 'true'",
                },
                new() { File = FeedbackYml, Performs = @"name: Nothing to triage" },
                new() { File = FeedbackYml, Performs = @"claude -p '/docs-feedback'" },
            ],
        },
        new()
        {
            Id = "§10 baselineSha has no CLI writer",
            Lead = "has no CLI writer",
            Sites =
            [

                // Every state mutation in the tool is a method on this one type, so a command that wanted
                // to stamp the baseline would have to add one here. Its absence is the whole evidence.
                new()
                {
                    File = "src/DocuMe.Core/State/StateUpdates.cs",
                    Performs = @"BaselineSha",
                    Absent = true,
                },

                // Named separately because it is the defect the bullet describes: "a publish that bumped
                // it would claim the pages were regenerated when they were only re-uploaded".
                new()
                {
                    File = "src/DocuMe.Cli/Commands/PublishCommand.cs",
                    Performs = @"BaselineSha",
                    Absent = true,
                },

                // The two readers the bullet names, so the field is not merely unwritten but unused.
                new()
                {
                    File = "src/DocuMe.Core/Status/StatusModel.cs",
                    Performs = @"BaselineSha = state\.BaselineSha",
                },
                new()
                {
                    File = "src/DocuMe.Core/Publishing/PublishPipeline.cs",
                    Performs = @"BaselineSha = state\.BaselineSha",
                },

                // And the generation pass that IS entitled to move it says so to the model.
                new() { File = RefreshSkill, Performs = @"The only field you may write is" },
            ],
        },
    ];

    /// <summary>
    /// Bullets whose prose overstates what the shipped templates do. Both are plan edits nobody has made
    /// rather than a licence to ignore the bullet.
    /// </summary>
    private static readonly PlanDeviation[] PlanDeviations =
    [
        new()
        {
            Id = "§10 pull-request advisory comment",
            Why = "PLAN.md §10's second bullet spells the command `docume drift --baseline "
                + "origin/<defaultBranch> --format github-comment`. docs-drift-pr.yml passes `git "
                + "merge-base origin/<the PR's own base ref> HEAD` instead, and its comment says why: "
                + "diffing against the base branch TIP attributes every commit merged into the base since "
                + "the branch started to this PR, so the comment lists pages the author never touched. "
                + "The template is right and the bullet is wrong. This is the same knob iter123 found "
                + "dead: `drift.defaultBranch` is declared in §5.1 and read by nothing, and this bullet "
                + "is the only place in the plan that gives it a job. Correcting §10 to the merge base is "
                + "half of settling decisions.planSection5Deviations — the other half is deciding whether "
                + "`drift.defaultBranch` has any consumer left once this bullet stops naming it.",
        },
        new()
        {
            Id = "§10 merge to default branch — publish, dashboard, reply, carry",
            Why = "PLAN.md §10's fourth bullet says docs-publish.yml is \"the only scaffolded workflow "
                + "that installs Node and `beautiful-mermaid`\". Only the second half is true: "
                + "docs-refresh.yml and docs-feedback.yml both run actions/setup-node, because "
                + "`claude -p` is an npm package. Read literally the sentence sends an auditor looking "
                + "for a drifted template that is not there. The justification clause that follows it "
                + "(\"because publish is the only command that renders a diagram\") is about the renderer "
                + "alone, so the fix is to say the renderer's toolchain rather than Node — a wording "
                + "change in PLAN.md and nothing else. The Absent sites on the two model templates pin "
                + "the half that is real.",
        },
    ];

    /// <summary>
    /// Anti-vacuity guard: every assertion below reads the parsed units, so a reformatted §10 would turn
    /// them all green by comparing nothing at all.
    /// </summary>
    [Fact]
    public void Section_10_parses_into_its_bullets_steps_and_closing_rule()
    {
        var units = Units("10");

        var reformatted = $"PLAN.md §10 parsed to {units.Count} unit(s), not the six bullets plus the "
            + "refresh skill's three numbered steps plus the closing `baselineSha` rule this file traces. "
            + "The numbered steps are two-space indented and the closing rule is a bold-lead paragraph — "
            + "check both before assuming a bullet was deleted.";

        units.Count.ShouldBe(10, reformatted);
    }

    /// <summary>
    /// The map check, in both directions: a promise §10 makes that no claim names fails, and so does a
    /// claim whose phrase §10 no longer contains.
    /// </summary>
    [Fact]
    public void Every_promise_the_plan_makes_is_traced_by_exactly_one_claim()
    {
        var units = Units("10");

        var untraced = new List<string>();
        var ambiguous = new List<string>();

        foreach (var unit in units)
        {
            var matches = Claims
                .Where(claim => unit.Contains(claim.Lead, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
            {
                untraced.Add(Excerpt(unit));
                continue;
            }

            if (matches.Count > 1)
            {
                ambiguous.Add($"{Excerpt(unit)} → {string.Join(", ", matches.Select(claim => claim.Id))}");
            }
        }

        var orphaned = Claims
            .Where(claim => !units.Any(unit => unit.Contains(claim.Lead, StringComparison.Ordinal)))
            .Select(claim => $"{claim.Id} (looked for \"{claim.Lead}\")")
            .ToList();

        untraced.ShouldBeEmpty(
            customMessage: "PLAN.md §10 promises a workflow behaviour no claim in this file traces, so "
                + "nothing checks that the step was written — the defect that let --notify-reviewers sit "
                + "unbuilt in §6. Add a claim naming the step that performs it: "
                + string.Join(" // ", untraced));

        ambiguous.ShouldBeEmpty(
            customMessage: "A claim's Lead phrase matches more than one bullet, so the two are no longer "
                + $"distinguishable and one of them is going unchecked: {string.Join(" // ", ambiguous)}");

        orphaned.ShouldBeEmpty(
            customMessage: "A claim looks for a phrase PLAN.md §10 no longer contains. Either the promise "
                + "was dropped — delete the claim — or its wording changed and this file is now tracing a "
                + $"bullet that says something else: {string.Join(" // ", orphaned)}");
    }

    /// <summary>
    /// The trace itself: every site must exist and must contain the step that carries out the promise
    /// (or, for <see cref="Site.Absent"/>, must not).
    /// </summary>
    [Fact]
    public void Every_claim_names_a_file_that_performs_it()
    {
        var unperformed = new List<string>();

        foreach (var claim in Claims)
        {
            foreach (var site in claim.Sites)
            {
                var path = Path.Combine(DocumeCli.RepoRoot, site.File.Replace('/', Path.DirectorySeparatorChar));

                File.Exists(path).ShouldBeTrue(
                    $"{claim.Id} names {site.File}, which does not exist. A moved file makes this claim "
                    + "untraceable; point it at the new one.");

                var matched = Pattern(site.Performs).IsMatch(File.ReadAllText(path));

                if (matched == site.Absent)
                {
                    var wrong = site.Absent
                        ? $"{claim.Id}: {site.File} MATCHES /{site.Performs}/ and §10 states the opposite. "
                            + "Something was added that the plan says should not be there."
                        : $"{claim.Id}: {site.File} does not match /{site.Performs}/, so no step in it "
                            + "performs the behaviour §10 promises.";

                    unperformed.Add(wrong);
                }
            }
        }

        unperformed.ShouldBeEmpty(customMessage: string.Join(" // ", unperformed));
    }

    /// <summary>
    /// Deviations are double-entry too: an entry naming a claim that does not exist fails, and so does an
    /// empty list — the two recorded gaps are plan edits owed, not closed items.
    /// </summary>
    [Fact]
    public void Recorded_plan_deviations_name_real_claims()
    {
        var ids = Claims.Select(claim => claim.Id).ToHashSet(StringComparer.Ordinal);

        PlanDeviations.ShouldNotBeEmpty(
            "§10's second bullet names a baseline the template deliberately does not use, and its fourth "
            + "claims docs-publish.yml is the only template installing Node when three do. If PLAN.md §10 "
            + "was corrected on both counts, delete this list and this assertion together rather than "
            + "emptying the list and leaving the check.");

        foreach (var deviation in PlanDeviations)
        {
            var orphaned = $"A deviation names \"{deviation.Id}\", which is not a claim in this file. "
                + "Either the claim was renamed or the deviation outlived it.";

            ids.ShouldContain(deviation.Id, customMessage: orphaned);

            var thin = $"{deviation.Id}'s Why has to say what the plan overstates and what correcting it "
                + "would take; a one-liner turns this list into a suppression.";

            deviation.Why.Length.ShouldBeGreaterThan(80, thin);
        }
    }

    /// <summary>
    /// §10's bullets, the refresh skill's nested numbered steps, and its closing bold-lead rule, each as
    /// one line of plan text.
    /// </summary>
    private static List<string> Units(string section)
    {
        var body = Section(section);
        var units = new List<string>();

        foreach (var line in body.Split('\n'))
        {
            var text = line.TrimEnd('\r');

            // Top-level bullets, two-space-indented numbered steps, and a bold-lead paragraph. The last
            // is why this parser differs from the §8/§9 one: §10 states its `baselineSha` rule as a
            // paragraph rather than as a bullet, and a parser that skipped it would leave the section's
            // sharpest invariant untraced.
            if (text.StartsWith("- ", StringComparison.Ordinal))
            {
                units.Add(text[2..]);
                continue;
            }

            if (text.StartsWith("**", StringComparison.Ordinal))
            {
                units.Add(text);
                continue;
            }

            var step = NumberedStep().Match(text);

            if (step.Success)
            {
                units.Add(step.Groups["text"].Value);
            }
        }

        return units;
    }

    /// <summary>§N's body, up to the next heading of any level.</summary>
    private static string Section(string section)
    {
        var match = Regex.Match(
            File.ReadAllText(Path.Combine(DocumeCli.RepoRoot, "PLAN.md")),
            $@"\n## {Regex.Escape(section)}\. (?<body>[\s\S]*?)(?=\n#{{2,3}} )",
            RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));

        match.Success.ShouldBeTrue(
            $"PLAN.md has no \"## {section}.\" section, so this whole file is reading nothing. Point it at "
            + "the new numbering rather than deleting it.");

        return match.Groups["body"].Value;
    }

    /// <summary>The first 90 characters of a bullet, for a failure message that fits on a line.</summary>
    private static string Excerpt(string unit) =>
        unit.Length <= 90 ? unit : string.Concat(unit.AsSpan(0, 90), "…");

    private static Regex Pattern(string performs) =>
        new(performs, RegexOptions.Multiline, TimeSpan.FromSeconds(2));

    [GeneratedRegex(@"^  \d+\. (?<text>.+)$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberedStep();
}
