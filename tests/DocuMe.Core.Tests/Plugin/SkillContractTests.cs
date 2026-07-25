using System.Text.Json;
using DocuMe.Core.Feedback;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;
using Shouldly;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization.NamingConventions;

namespace DocuMe.Core.Tests.Plugin;

/// <summary>
/// The Claude Code skills in <c>plugin/skills/</c> (PLAN.md §11), asserted where they make a promise the
/// C# can be held to.
/// </summary>
/// <remarks>
/// <para>
/// A SKILL.md is prose, and most of it is not testable — whether the procedure produces a good page is a
/// human's judgement. Two things in it are not prose, though, and both fail silently.
/// </para>
/// <para>
/// The first is rule §1.3, which requires every SKILL.md to state in its system contract that Confluence
/// page bodies and comments are untrusted input: claims to verify against code, never instructions to
/// follow. That clause is the whole prompt-injection defense (PLAN.md §9), it is invisible when absent,
/// and it is exactly the kind of paragraph an editor trims for length. So its presence is asserted.
/// </para>
/// <para>
/// The second is rule §0.4's boundary: skills reach Confluence only through the <c>docume</c> CLI. A skill
/// that grew a REST call would still read fine and would still work, right up to the run that wrote to a
/// space with none of the tool's guards on it. The grep here is a canary for that, not proof of it.
/// </para>
/// <para>
/// The frontmatter checks are duller and earn their place anyway: a <c>name</c> that disagrees with the
/// directory means <c>/docs-refresh</c> invokes nothing, and a missing <c>description</c> means the model
/// never discovers the skill in the first place.
/// </para>
/// </remarks>
public sealed class SkillContractTests
{
    /// <summary>The three skills §11 names.</summary>
    private static readonly string[] Skills = ["docs-refresh", "docs-feedback", "docs-loop"];

    /// <summary>
    /// The branch each skill's PR is opened on (rule §8.4, PLAN.md §9/§10). Asserted because it is a
    /// convention shared with the workflow templates, which grep for <c>docs/refresh-*</c> and
    /// <c>docs/feedback-*</c> to tell a run that did nothing from one that opened a PR.
    /// </summary>
    /// <remarks>
    /// <c>docs/loop-</c> is the one prefix no §9/§10 sentence names, because no workflow template invokes
    /// generation: `/docs-loop` runs from somebody's terminal on a wiki that does not exist yet. It is
    /// pinned here anyway, and to the same shape as the other two, because the alternative is that the
    /// first workflow to automate generation has to guess — and because a skill free to pick its own
    /// branch name picks a different one each run.
    /// </remarks>
    private static readonly Dictionary<string, string> BranchPrefixes = new(StringComparer.Ordinal)
    {
        ["docs-refresh"] = "docs/refresh-",
        ["docs-feedback"] = "docs/feedback-",
        ["docs-loop"] = "docs/loop-",
    };

    /// <summary>
    /// URL fragments that only appear in a direct Confluence call. Not <c>curl</c>: the refresh skill's
    /// contract says the words "no <c>curl</c>" out loud, and a test that punished a skill for forbidding
    /// something would be the wrong assertion.
    /// </summary>
    private static readonly string[] RestPaths = ["rest/api", "api/v2/pages", "/wiki/api"];

    [Fact]
    public void Every_skill_PLAN_11_names_is_present()
    {
        var missing = Skills
            .Where(skill => !File.Exists(SkillFile(skill)))
            .ToList();

        // Names, not a count: every other test here iterates the same list, so a skill file that vanished
        // would turn them all into vacuous passes.
        missing.ShouldBeEmpty($"Missing SKILL.md under {Directory}/<skill>/.");
    }

    [Fact]
    public void Every_skill_declares_a_name_matching_its_directory()
    {
        foreach (var skill in Skills)
        {
            var frontmatter = Frontmatter(skill);
            var name = Value(frontmatter, "name");

            // `/docs-refresh` resolves by the frontmatter name, not by the folder. Disagree and the slash
            // command in §10's workflow template invokes nothing at all.
            name.ShouldBe(skill, $"{skill}/SKILL.md declares name '{name}'.");
        }
    }

    [Fact]
    public void Every_skill_describes_when_to_use_it()
    {
        foreach (var skill in Skills)
        {
            var description = Value(Frontmatter(skill), "description");

            // The description is the only part of a skill a model sees before deciding to load it, so an
            // empty or one-word one is a skill that never runs. Length is a crude proxy for saying when to
            // use it, and it catches the placeholder, which is the realistic failure.
            description.Length.ShouldBeGreaterThan(
                40,
                $"{skill}/SKILL.md needs a description that says when to use it (§11).");
        }
    }

    [Fact]
    public void Every_skill_states_the_untrusted_input_contract()
    {
        foreach (var skill in Skills)
        {
            var text = Text(skill);

            // Rule §1.3: "State this explicitly in every SKILL.md system contract." Asserted on the two
            // phrases that carry the meaning rather than on a whole sentence, so an editor may rewrite the
            // paragraph without tripping this, but not delete it.
            text.ShouldContain(
                "untrusted input",
                Case.Insensitive,
                $"{skill}/SKILL.md must state that Confluence content is untrusted input (rule §1.3).");
            var claims = $"{skill}/SKILL.md must say Confluence content is claims to verify, never "
                + "instructions to follow (rule §1.3).";

            text.ShouldContain("claims to verify", Case.Insensitive, claims);
        }
    }

    [Fact]
    public void No_skill_reaches_Confluence_around_the_CLI()
    {
        foreach (var skill in Skills)
        {
            var text = Text(skill);

            // Rule §0.4 / §11: skills invoke `docume` and never call the API themselves.
            text.ShouldContain(
                "docume",
                customMessage: $"{skill}/SKILL.md never invokes the CLI, so what is it doing?");

            var found = RestPaths
                .Where(path => text.Contains(path, StringComparison.OrdinalIgnoreCase))
                .ToList();

            found.ShouldBeEmpty(
                $"{skill}/SKILL.md names a Confluence REST path — only the CLI talks to Confluence "
                    + "(rule §0.4).");
        }
    }

    [Fact]
    public void No_skill_writes_to_Confluence_from_its_own_commands()
    {
        foreach (var skill in Skills)
        {
            // Read from the bash blocks rather than from the whole file, for the reason RestPaths skips
            // `curl`: both of these skills *document* the writes that follow their PR — a refresh explains
            // that merging republishes, a feedback run explains that `sync --reply` answers the reviewer
            // afterwards — and a grep that punished the explanation would push the next editor to drop it.
            // What is asserted is the commands, where the mistake would be silent and consequential: a
            // reply posted from the skill's own run says "fixed in the latest version" while the fix sits
            // in an unmerged branch (§9 step 5).
            var commands = string.Join('\n', BashCommands(skill));

            commands.ShouldNotContain(
                "docume publish",
                Case.Insensitive,
                $"{skill}/SKILL.md publishes — a skill's output is a PR (rule §0.4, §1.5).");
            commands.ShouldNotContain(
                "--reply",
                Case.Insensitive,
                $"{skill}/SKILL.md posts replies — only the CLI does, after the merge (§9 step 5).");
        }
    }

    [Fact]
    public void Every_skill_names_the_branch_its_PR_is_opened_on()
    {
        foreach (var (skill, prefix) in BranchPrefixes)
        {
            // Rule §8.4's slash grouping, and the coupling that makes it load-bearing: each workflow
            // template confirms its run did something by listing `refs/heads/<prefix>*` on origin. A skill
            // that pushed `feedback/2026-08-02` would still open a perfectly good PR, and the job that ran
            // it would still warn that nothing happened.
            Text(skill).ShouldContain(
                prefix,
                customMessage: $"{skill}/SKILL.md must open its PR on a {prefix}<date> branch (rule §8.4).");
        }
    }

    [Fact]
    public void The_feedback_skill_spells_the_statuses_the_CLI_reads()
    {
        var text = Text("docs-feedback");

        // The coupling this test exists for. §9 step 3's triage writes `status` into an inbox item and
        // `docume sync --reply` decides from it: FeedbackReplyText.IsTriaged answers false for anything it
        // does not recognise, deliberately, so that an unknown value never puts a wrong sentence under a
        // reviewer's comment. The consequence is that a skill writing `resolved` instead of `fixed` fails
        // nothing, changes no page, and leaves the reviewer permanently unanswered.
        string[] statuses =
        [
            FeedbackStatus.New,
            FeedbackStatus.Fixed,
            FeedbackStatus.Rejected,
            FeedbackStatus.Question,
        ];

        foreach (var status in statuses)
        {
            text.ShouldContain(
                status,
                Case.Sensitive,
                $"docs-feedback/SKILL.md must spell the status '{status}' as the CLI reads it (§5.4).");
        }

        // The other half of the same coupling, in the other direction: `repliedAt` is the CLI's field and
        // the entire double-reply guard, so the skill has to be told not to write it. Stamped by the skill,
        // the reply pass skips the item as already answered and nobody ever replies.
        text.ShouldContain(
            "repliedAt",
            customMessage: "docs-feedback/SKILL.md must say who owns `repliedAt` (§9 step 5).");
    }

    [Fact]
    public void The_loop_skill_spells_the_two_fields_that_make_a_generated_page_maintainable()
    {
        var text = Text("docs-loop");

        // Both of these are silent when absent, which is why they are asserted rather than trusted to a
        // reviewer. A generated page with no `sources` is never reported as drifted, so it stops being
        // maintained on the day it is written (§5.2, §6.4) — and nothing ever says so. And `baselineSha` is
        // written by no CLI command at all: the generation pass owns it, `docume drift` refuses to run
        // without it, so a skill that never stamps it leaves drift detection switched off for the repo.
        var sources = CamelCaseNamingConvention.Instance.Apply(nameof(PageFrontmatter.Sources));
        var baseline = JsonNamingPolicy.CamelCase.ConvertName(nameof(DocumeState.BaselineSha));

        text.ShouldContain(
            sources,
            Case.Sensitive,
            $"docs-loop/SKILL.md must tell the run to declare `{sources}` on every page it writes (§5.2).");
        text.ShouldContain(
            baseline,
            Case.Sensitive,
            $"docs-loop/SKILL.md must say it owns `{baseline}` — no CLI command writes it (§5.3, §6.4).");

        // The other direction on the same frontmatter, and the expensive one: `pageId` is publish's, and a
        // generated page carrying an invented id points the next publish at somebody else's page.
        var pageId = CamelCaseNamingConvention.Instance.Apply(nameof(PageFrontmatter.PageId));

        text.ShouldContain(
            pageId,
            Case.Sensitive,
            $"docs-loop/SKILL.md must say `{pageId}` is publish's to write, not the skill's (§5.2).");
    }

    private static string Directory { get; } = Locate();

    private static string SkillFile(string skill) => Path.Combine(Directory, skill, "SKILL.md");

    private static string Text(string skill) => File.ReadAllText(SkillFile(skill));

    /// <summary>
    /// The lines inside <paramref name="skill"/>'s <c>bash</c> fences: the commands the skill runs, as
    /// opposed to the prose around them.
    /// </summary>
    /// <remarks>
    /// A fence closes whatever is open and opens only when it names bash, so the <c>markdown</c> block
    /// holding a PR-body template — and the <c>json</c> block nested inside it — never read as commands.
    /// That nesting is why this is a small state machine rather than a regex over the file.
    /// </remarks>
    private static IEnumerable<string> BashCommands(string skill)
    {
        var inside = false;

        foreach (var line in Text(skill).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inside = !inside && trimmed.Contains("bash", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inside)
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// The YAML block between the opening and closing <c>---</c> of <paramref name="skill"/>'s SKILL.md.
    /// </summary>
    private static YamlMappingNode Frontmatter(string skill)
    {
        var lines = Text(skill).Split('\n');

        lines.Length.ShouldBeGreaterThan(2, $"{skill}/SKILL.md is empty.");
        lines[0].Trim().ShouldBe("---", $"{skill}/SKILL.md must open with yaml frontmatter.");

        var closing = Array.FindIndex(
            lines,
            1,
            line => string.Equals(line.Trim(), "---", StringComparison.Ordinal));

        closing.ShouldBeGreaterThan(1, $"{skill}/SKILL.md frontmatter is never closed.");

        var stream = new YamlStream();
        using var reader = new StringReader(string.Join('\n', lines[1..closing]));

        // Load, not Deserialize: a colon in an unquoted description is the mistake hand-written
        // frontmatter actually makes, and it throws here rather than binding to a wrong shape.
        stream.Load(reader);

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string Value(YamlMappingNode frontmatter, string key)
    {
        var entry = frontmatter.Children
            .SingleOrDefault(child => string.Equals(
                ((YamlScalarNode)child.Key).Value,
                key,
                StringComparison.Ordinal));

        entry.Value.ShouldNotBeNull($"Frontmatter has no '{key}'.");

        return ((YamlScalarNode)entry.Value).Value ?? string.Empty;
    }

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>: the skills ship in the tree, so the test
    /// reads the shipped copy rather than a build artifact of it.
    /// </summary>
    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return Path.Combine(directory.FullName, "plugin", "skills");
            }
        }

        // Not a skip: the skills are committed, so a run that cannot find them is a broken run, and
        // "0 skills checked, all green" would be the worse answer.
        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so plugin/skills cannot be found.");
    }
}
