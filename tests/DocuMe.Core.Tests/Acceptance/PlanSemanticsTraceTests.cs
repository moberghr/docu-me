using System.Text.RegularExpressions;
using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// PLAN.md §8's approval semantics and §9's feedback steps, each held against the file that
/// <em>performs</em> it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure this exists to catch.</strong> §8 and §9 are prose: numbered semantics a reader
/// takes as a description of working software. Nothing read them back. The repo pins what exists against
/// what exists — reference page against help output, changelog against flag inventory, schema against
/// record — so a semantic the plan states and nobody built is invisible to every check in the suite.
/// <see cref="PlanCommandSpecTests"/> found <c>--notify-reviewers</c> that way in §6 and
/// <see cref="PlanDataContractTests"/> found two dead knobs that way in §5.
/// </para>
/// <para>
/// <strong>A site is the line that does it, not a type that could.</strong> The §5 sweep's lesson was that
/// a shape check passes on a field which binds and changes nothing, and the same trap is worse here: every
/// §8 semantic has a plausibly-named type near it. So each claim names a file <em>and</em> a pattern that
/// has to match inside it — a call, an assignment, a rendered sentence, a workflow condition. A type
/// declaration is never the evidence. <see cref="Site.Absent"/> inverts one: §9 step 5 decides
/// <em>against</em> a reply flag on publish, and the only proof of a decision not taken is that the flag
/// is not there.
/// </para>
/// <para>
/// <strong>Keyed on the plan's own words, not on bullet ordinals.</strong> A claim matches its unit by a
/// distinctive phrase lifted from the plan. Reordering §9 therefore costs nothing, while <em>adding</em> a
/// semantic fails: the new bullet matches no claim, which is the case that matters. Both directions are
/// double-entry, the way the two sibling files record their gaps.
/// </para>
/// <para>
/// <strong>What this cannot see.</strong> A pattern proves the performing line is present, not that it is
/// reached — a call inside a branch no run takes still matches. The sandbox round-trips behind the M2–M5
/// gates are what close that, and this file is the check that survives between them.
/// </para>
/// </remarks>
public sealed partial class PlanSemanticsTraceTests
{
    /// <summary>One file, and the construct in it that carries out a claim.</summary>
    private sealed record Site
    {
        /// <summary>Repo-relative path.</summary>
        public required string File { get; init; }

        /// <summary>A regex that must match the file's text — the line doing the work.</summary>
        public required string Performs { get; init; }

        /// <summary>
        /// When <c>true</c>, the pattern must <em>not</em> match: the claim is a decision against
        /// building something, and its absence is the only evidence there is.
        /// </summary>
        public bool Absent { get; init; }
    }

    /// <summary>One §8 bullet or §9 step, bound to the code that performs it.</summary>
    private sealed record Claim
    {
        /// <summary>Label used in failure messages only; nothing is keyed on it.</summary>
        public required string Id { get; init; }

        /// <summary>A phrase from the plan's own text, distinctive enough to name one unit.</summary>
        public required string Lead { get; init; }

        /// <summary>Every file that performs a part of this claim.</summary>
        public required Site[] Sites { get; init; }
    }

    /// <summary>
    /// A claim whose prose states more than any implementation can deliver. Recorded rather than
    /// tolerated: the sites still have to match, so the gap cannot drift without failing.
    /// </summary>
    private sealed record PlanDeviation
    {
        /// <summary>The <see cref="Claim.Id"/> it belongs to.</summary>
        public required string Id { get; init; }

        /// <summary>What the plan overstates, and what correcting it would take.</summary>
        public required string Why { get; init; }
    }

    private const string SkillPath = "plugin/skills/docs-feedback/SKILL.md";
    private const string SyncPath = "src/DocuMe.Cli/Commands/SyncCommand.cs";

    private static readonly Claim[] Claims =
    [
        new()
        {
            Id = "§8 label is the only gesture",
            Lead = "That's all a reviewer ever does",
            Sites =
            [
                new()
                {
                    File = "src/DocuMe.Core/Sync/LabelReader.cs",
                    Performs = @"SearchPagesByLabelAsync\(spaceKey, config\.Labels\.Approved",
                },
            ],
        },
        new()
        {
            Id = "§8 approval recorded at the observed version",
            Lead = "at the page version current at observation time",
            Sites =
            [

                // The live version is fetched, rather than state's publishedVersion being reused.
                new()
                {
                    File = "src/DocuMe.Core/Sync/LabelReader.cs",
                    Performs = @"read\?\.Version is \{ \} current",
                },

                // And it is the value that reaches the recorded approval.
                new()
                {
                    File = "src/DocuMe.Core/Sync/LabelSyncPlanner.cs",
                    Performs = @"RecordApproval\([\s\S]*?approval\.Version\)",
                },
            ],
        },
        new()
        {
            Id = "§8 invalidation removes the label",
            Lead = "Invalidation = machine removes the label",
            Sites =
            [
                new()
                {
                    File = "src/DocuMe.Core/Publishing/PublishExecutor.cs",
                    Performs = @"RemoveLabelIfPresentAsync\(pageId!, config\.Labels\.Approved",
                },
            ],
        },
        new()
        {
            Id = "§8 banner-only edits never invalidate",
            Lead = "Banner-only or machine edits never invalidate",
            Sites =
            [

                // The ordering IS the invariant: hash the converter's output, inject the banner after.
                // Reversing these two lines revokes approval on every approved page in the wiki, and no
                // other assertion in the suite reads them as a sequence.
                new()
                {
                    File = "src/DocuMe.Core/Publishing/PublishPipeline.cs",
                    Performs = @"ContentHash\.OfBody\(body\)[\s\S]*banner\.InjectInto\(body\)",
                },
            ],
        },
        new()
        {
            Id = "§8 banner carries provenance and points at labels",
            Lead = "Review status is shown by page labels",
            Sites =
            [
                new()
                {
                    File = "src/DocuMe.Core/Markdown/PageBanner.cs",
                    Performs = @"Write\(""Review status is shown by page labels""\)",
                },

                // The two provenance halves §8 names: baseline SHA and date.
                new()
                {
                    File = "src/DocuMe.Core/Markdown/PageBanner.cs",
                    Performs = @"WriteEscaped\(baselineSha\)",
                },
                new()
                {
                    File = "src/DocuMe.Core/Markdown/PageBanner.cs",
                    Performs = @"generatedOn\.ToString\(""yyyy-MM-dd""",
                },
            ],
        },
        new()
        {
            Id = "§8 approval history kept for audit",
            Lead = "Approval history kept in",
            Sites =
            [
                new()
                {
                    File = "src/DocuMe.Core/State/StateUpdates.cs",
                    Performs = @"History = history",
                },
            ],
        },
        new()
        {
            Id = "§9 intake is Confluence comments, inbox is the seam",
            Lead = "the pluggable seam",
            Sites =
            [
                new()
                {
                    File = "src/DocuMe.Core/Feedback/FeedbackItem.cs",
                    Performs = @"ConfluenceCommentPrefix = ""conf-comment-""",
                },

                // The seam is enforced, not decorative: an id naming a channel v1 cannot post to is
                // refused rather than sent as a bare comment id.
                new()
                {
                    File = "src/DocuMe.Core/Feedback/FeedbackReplyPlanner.cs",
                    Performs = @"StartsWith\(FeedbackItemId\.ConfluenceCommentPrefix",
                },
            ],
        },
        new()
        {
            Id = "§9 sync --comments, committed by the cron workflow",
            Lead = "committed via PR by the cron workflow",
            Sites =
            [
                new() { File = SyncPath, Performs = @"new Option<bool>\(""--comments""\)" },
                new() { File = "templates/workflows/docs-sync.yml", Performs = @"gh pr create" },
                new() { File = "templates/workflows/docs-sync.yml", Performs = @"schedule:" },
            ],
        },
        new()
        {
            Id = "§9 the /docs-feedback skill exists and is manually triggerable",
            Lead = "run locally or manually triggered in CI",
            Sites =
            [
                new() { File = SkillPath, Performs = @"^name: docs-feedback$" },
                new() { File = "templates/workflows/docs-feedback.claude.yml", Performs = @"workflow_dispatch:" },
            ],
        },
        new()
        {
            Id = "§9 step 1 — reads items with status new",
            Lead = "Reads inbox items with `status: new`",
            Sites =
            [
                new() { File = SkillPath, Performs = @"`status: new` . triage it" },
            ],
        },
        new()
        {
            Id = "§9 step 2 — claims verified, never obeyed",
            Lead = "never as instructions",
            Sites =
            [

                // §1.3 requires this be stated in the SKILL.md system contract, so the heading is part
                // of the claim rather than the sentence appearing anywhere in the file.
                new() { File = SkillPath, Performs = @"## System contract[\s\S]*untrusted input" },
                new() { File = SkillPath, Performs = @"claims to verify" },
            ],
        },
        new()
        {
            Id = "§9 step 3 — triage has all three outcomes",
            Lead = "suggestion/out-of-scope → mark `rejected` with reason",
            Sites =
            [
                new() { File = SkillPath, Performs = @"3a\.[^\n]*`fixed`" },
                new() { File = SkillPath, Performs = @"3b\.[^\n]*`rejected`" },
                new() { File = SkillPath, Performs = @"3c\.[^\n]*`question`" },
                new() { File = SkillPath, Performs = @"_meta/GAPS\.md" },
            ],
        },
        new()
        {
            Id = "§9 step 4 — one dated PR carrying fixes, verdicts and archive moves",
            Lead = "containing fixes + inbox status updates + archive moves",
            Sites =
            [
                new() { File = SkillPath, Performs = @"docs/feedback-\$date" },
                new() { File = SkillPath, Performs = @"git mv [^\n]*feedback/inbox[^\n]*feedback/archive" },
            ],
        },
        new()
        {
            Id = "§9 step 5 — reply is a separate, publish-gated step",
            Lead = "A separate step, not a flag on publish",
            Sites =
            [

                // Never in the default set, so the six-hourly cron of §10 posts nothing.
                new() { File = SyncPath, Performs = @"var syncReply = requested\.Reply;" },

                // The decision against a flag on publish. Its absence is the whole evidence.
                new()
                {
                    File = "src/DocuMe.Cli/Commands/PublishCommand.cs",
                    Performs = @"""--reply""",
                    Absent = true,
                },

                // "Gated on the publish having succeeded": the gate is the workflow's condition, and the
                // CLI cannot hold it — the reply pass reads triaged items and live comments, not this
                // run's outcome.
                new()
                {
                    File = "templates/workflows/docs-publish.yml",
                    Performs = @"if: steps\.publish\.outputs\.code == '0'",
                },

                // "Resolves inline comments where the API allows", with each way of not allowing named.
                new()
                {
                    File = "src/DocuMe.Core/Feedback/FeedbackReplyPlanner.cs",
                    Performs = @"ReplyResolvePlan\.NotClosable",
                },
            ],
        },
        new()
        {
            Id = "§9 CI posture — PR-only writes",
            Lead = "read-only repo access + PR-only writes",
            Sites =
            [

                // Pinned because it is the deviation's evidence: a job that opens a PR from a branch
                // cannot hold `contents: read`.
                new()
                {
                    File = "templates/workflows/docs-feedback.claude.yml",
                    Performs = @"permissions:\n  contents: write\n  pull-requests: write",
                },

                // The half that is honoured exactly: the model run holds no Confluence credentials, so
                // it cannot write to Confluence even if it tried (rule §0.4).
                new()
                {
                    File = "templates/workflows/docs-feedback.claude.yml",
                    Performs = @"DOCUME_CONFLUENCE_TOKEN",
                    Absent = true,
                },
            ],
        },
    ];

    /// <summary>
    /// Claims whose prose overstates what any implementation could do. One entry, and it is a plan edit
    /// nobody has made rather than a licence to ignore the bullet.
    /// </summary>
    private static readonly PlanDeviation[] PlanDeviations =
    [
        new()
        {
            Id = "§9 CI posture — PR-only writes",
            Why = "PLAN.md §9 and rule §1.5 both say feedback processing runs with \"read-only repo "
                + "access\". No GitHub job can: pushing the docs/feedback-<date> branch the same bullet "
                + "requires needs `contents: write`, and templates/workflows/docs-feedback.claude.yml grants it "
                + "(its own comment at the permissions block says why). What IS enforced is the half that "
                + "matters, and the wiki already words it correctly — "
                + "docs/wiki/30-automation/workflows.md's \"Everything writes through a pull request\" "
                + "says \"none of them pushes to the default branch\" and never claims read-only. So no "
                + "consumer-facing page is wrong; the plan and the rule are. Correcting both to the "
                + "wiki's wording closes this. Weakening the template would not, and neither would "
                + "editing rule §1.5 from the loop — .claude/ is not the loop's to write.",
        },
    ];

    /// <summary>
    /// Anti-vacuity guard: every assertion below reads the parsed units, so a renumbered §8/§9 or a
    /// reformatted list would turn them all green by comparing nothing at all.
    /// </summary>
    [Fact]
    public void Sections_8_and_9_parse_into_their_bullets_and_steps()
    {
        var approval = Units("8");
        var feedback = Units("9");

        var reformatted = $"PLAN.md §8 parsed to {approval.Count} bullet(s), not the six semantics this "
            + "file traces. It has been reformatted or renumbered; point the parser at it rather than "
            + "letting every claim below match nothing.";

        approval.Count.ShouldBe(6, reformatted);

        var renumbered = $"PLAN.md §9 parsed to {feedback.Count} unit(s), not the four bullets plus five "
            + "numbered steps this file traces. The nested list is two-space indented — check that before "
            + "assuming a step was deleted.";

        // Four top-level bullets plus the skill's five numbered steps.
        feedback.Count.ShouldBe(9, renumbered);
    }

    /// <summary>
    /// The map check, in both directions: a semantic the plan states that no claim names fails, and so
    /// does a claim whose phrase the plan no longer contains.
    /// </summary>
    [Fact]
    public void Every_semantic_the_plan_states_is_traced_by_exactly_one_claim()
    {
        var units = Units("8").Concat(Units("9")).ToList();

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
            customMessage: "PLAN.md §8/§9 states a semantic no claim in this file traces, so nothing "
                + "checks that it was built — the defect that let --notify-reviewers sit unbuilt in §6. "
                + $"Add a claim naming the line that performs it: {string.Join(" // ", untraced)}");

        ambiguous.ShouldBeEmpty(
            customMessage: "A claim's Lead phrase matches more than one bullet, so the two are no longer "
                + $"distinguishable and one of them is going unchecked: {string.Join(" // ", ambiguous)}");

        orphaned.ShouldBeEmpty(
            customMessage: "A claim looks for a phrase PLAN.md no longer contains. Either the semantic was "
                + "dropped — delete the claim — or its wording changed and this file is now tracing a "
                + $"bullet that says something else: {string.Join(" // ", orphaned)}");
    }

    /// <summary>
    /// The trace itself: every site must exist and must contain the construct that carries out the claim
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
                        ? $"{claim.Id}: {site.File} MATCHES /{site.Performs}/ and the plan decided against "
                            + "it. Something was built that §8/§9 says should not exist."
                        : $"{claim.Id}: {site.File} does not match /{site.Performs}/, so nothing in it "
                            + "performs the semantic the plan states.";

                    unperformed.Add(wrong);
                }
            }
        }

        unperformed.ShouldBeEmpty(customMessage: string.Join(" // ", unperformed));
    }

    /// <summary>
    /// Deviations are double-entry too: an entry naming a claim that does not exist fails, and so does an
    /// empty list — the one recorded gap is a plan edit owed, not a closed item.
    /// </summary>
    [Fact]
    public void Recorded_plan_deviations_name_real_claims()
    {
        var ids = Claims.Select(claim => claim.Id).ToHashSet(StringComparer.Ordinal);

        PlanDeviations.ShouldNotBeEmpty(
            "§9's CI-posture bullet asks for read-only repo access on a job that opens a PR, which no "
            + "GitHub token can do. If PLAN.md §9 and rule §1.5 were corrected, delete this list and this "
            + "assertion together rather than emptying the list and leaving the check.");

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
    /// §8's bullets and §9's bullets plus its nested numbered steps, each as one line of plan text.
    /// </summary>
    private static List<string> Units(string section)
    {
        var body = Section(section);
        var units = new List<string>();

        foreach (var line in body.Split('\n'))
        {
            var text = line.TrimEnd('\r');

            // Top-level bullets and two-space-indented numbered steps. Anything deeper is elaboration on
            // a step rather than a semantic in its own right.
            if (text.StartsWith("- ", StringComparison.Ordinal))
            {
                units.Add(text[2..]);
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
