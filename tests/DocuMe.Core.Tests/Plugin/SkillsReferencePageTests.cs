using System.Reflection;
using System.Text.Json;
using DocuMe.Core.Feedback;
using DocuMe.Core.State;
using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Plugin;

/// <summary>
/// <c>docs/wiki/30-automation/skills.md</c>'s skill table, triage routes, baseline paragraphs, untrusted-input
/// warning and install block, against what the <c>SKILL.md</c> files in <c>plugin/skills/</c>, the
/// manifests and the CLI actually do.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SkillContractTests"/> holds the skills to the clauses rule §1.3 and §0.4 require of them.
/// Nothing held the page that *describes* those skills to the same files, and at iter93 six of its claims
/// were wrong. The two that get acted on came first: the install block told a reader to run
/// <c>claude plugin install docume</c> when every other copy of the install (README.md, plugin/README.md,
/// PLAN.md §12, release.yml) is the slash command <c>/plugin install docume@docume</c> — the qualifier being
/// what keeps the id unambiguous once the plugin is in Moberg's marketplace too — and the closing sentence
/// promised "a DocuMe message rather than a missing-command error from a shell" for a repo that has not run
/// <c>docume init</c>, which is exactly backwards: the manifest pin is what <c>init</c> writes, so that repo
/// gets <c>dotnet</c>'s own "Cannot find a tool in the manifest file that has a command named 'docume'"
/// (.mtk/paths-93/probe-tool-manifest.mjs records both endings).
/// </para>
/// <para>
/// The other four were descriptions, and the sharpest is the triage list: the page routed a comment to
/// <c>_meta/GAPS.md</c> when "it is a question, not a claim", where the skill routes on "the code cannot
/// settle it" — a claim about another repo, or one the run merely suspects, is a claim and lands there.
/// Under the page's three triggers it matched none of them, and the nearest reading is the decline, whose
/// sentence the CLI posts verbatim under the reviewer's comment: "After checking it against the code, the
/// page is staying as it is", about a point nothing checked. Then: <c>baselineSha</c> was credited to
/// <c>/docs-loop</c> alone while <c>/docs-refresh</c> stamps it to a different value and the refresh section
/// said nothing about it; "all three end the same way — a pull request" was silent about the ending all
/// three have when there is no work, which the page named for one of them; and the warning re-described
/// instruction-shaped text as "a claim about the page it was left on" instead of what happens to it.
/// </para>
/// <para>
/// Every set here is derived from the skills rather than listed. Where a phrase has to be hand-written (the
/// per-row condition, the stamp value) the token is asserted to appear in that skill's own SKILL.md as well,
/// so the vocabulary cannot drift away from the file it describes.
/// </para>
/// </remarks>
public sealed class SkillsReferencePageTests
{
    private const string PagePath = "docs/wiki/30-automation/skills.md";
    private const string ReadmePath = "README.md";
    private const string ProbePath = ".mtk/paths-93/probe-tool-manifest.mjs";

    /// <summary>The clause every generation skill carries and <c>docs-feedback</c> deliberately does not.</summary>
    private const string WriterClause = "field you may write";

    private const string RowHeader = "| Skill | Use it when |";

    /// <summary>What <c>dotnet</c> says when the pin <c>docume init</c> writes is missing.</summary>
    private const string ManifestError = "Cannot find a tool in the manifest file";

    /// <summary>
    /// The condition each skill's row must name in its "Opens nothing when" cell, and the token the check
    /// runs on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written, and anchored: <see cref="Every_row_says_when_its_skill_opens_nothing"/> asserts each
    /// token appears in its own SKILL.md and in none of the others, which is what lets the row check run
    /// both ways. A skill that stops for a new reason needs its token changed here, and the class says so.
    /// </para>
    /// <para>
    /// <c>docs-loop</c>'s token was the bare word <c>todo</c> until <c>docs-processes</c> shipped. That
    /// skill keeps an inventory of its own with the same four states by design, so it carries the word too
    /// and the uniqueness check failed on a page that was perfectly accurate. The longer phrase is the
    /// heading of the same edge case in the same file, and it names the file the row already points a
    /// reader at.
    /// </para>
    /// </remarks>
    private static readonly (string Skill, string Condition)[] EmptyRunConditions =
    [
        ("docs-loop", "Nothing is `todo`"),
        ("docs-refresh", "hasDrift"),
        ("docs-feedback", "untriaged"),
        ("docs-processes", "process inventory is exhausted"),
    ];

    /// <summary>
    /// The value each generation skill stamps into <c>baselineSha</c>, as the word its section has to carry.
    /// </summary>
    /// <remarks>
    /// They are not all the same on purpose (<c>/docs-loop</c> the oldest generation sha still in
    /// <c>PROGRESS.md</c>, <c>/docs-processes</c> the oldest across both progress files,
    /// <c>/docs-refresh</c> the head it regenerated everything against), and a page that states one rule
    /// for the field states a rule that is wrong for one of them.
    /// </remarks>
    /// <summary>
    /// What the page's opening sentence about the quiet ending has to name, as a reader reads it. The
    /// SKILL.md side of the same fact is spelled the other way round ("no branch, no commit, no PR"), so the
    /// two vocabularies are separate on purpose: the page is prose about the skills, not a copy of them.
    /// </summary>
    private static readonly string[] Absences = ["branch", "commit", "pull request"];

    private static readonly (string Skill, string Stamp)[] BaselineStamps =
    [
        ("docs-loop", "oldest"),
        ("docs-processes", "oldest"),
        ("docs-refresh", "head"),
    ];

    [Fact]
    public void The_table_lists_every_skill_the_plugin_ships_and_no_others()
    {
        var rows = Rows();

        const string mismatched = "The page's table and plugin/skills/ describe different sets of skills. A "
            + "row for a skill that is not installed is a slash command a reader types and waits for.";

        rows.Select(row => row.Skill).ShouldBe(Skills(), ignoreOrder: true, mismatched);

        var intro = Intro();

        intro.ShouldContain(
            Count(Skills().Count),
            Case.Insensitive,
            "The page opens by counting the skills the plugin ships, and the count is now wrong.");

        foreach (var (skill, cell) in rows.Select(row => (row.Skill, row.Branch)))
        {
            var prefix = BranchPrefix(skill);

            var wrong = $"`/{skill}`'s row must name the {prefix}<date> branch its PR is opened on: the "
                + "workflow templates confirm a run did something by listing that prefix on origin.";

            cell.ShouldContain(prefix, customMessage: wrong);
        }
    }

    /// <summary>
    /// Every skill that documents an ending with no pull request has its condition on the page, and no row
    /// claims one for a skill that has no such ending.
    /// </summary>
    /// <remarks>
    /// The failure this guards is iter92's shape: the page said a run with nothing drifted "opens nothing"
    /// for <c>/docs-refresh</c> and said nothing for the other two, so the silence asserted that they always
    /// open one. Every one of them stops instead — no unit <c>todo</c>, no process left, no page drifted,
    /// no untriaged item — and a consumer who reads a PR as the only ending treats a quiet nightly job as a
    /// broken one.
    /// </remarks>
    [Fact]
    public void Every_row_says_when_its_skill_opens_nothing()
    {
        var quiet = Skills().Where(DocumentsAnEmptyRun).ToList();

        quiet.ShouldNotBeEmpty("No SKILL.md documents an ending without a PR, so this class checks nothing.");

        var rows = Rows();
        var wrong = new List<string>();

        foreach (var (skill, condition) in EmptyRunConditions)
        {
            var owning = Skills().Where(other => Text(other).Contains(condition, StringComparison.Ordinal));

            owning.ShouldBe(
                [skill],
                $"'{condition}' no longer identifies {skill} alone, so the row check cannot run both ways.");

            var cell = rows.Single(row => string.Equals(row.Skill, skill, StringComparison.Ordinal)).OpensNothing;
            var named = cell.Contains(condition, StringComparison.OrdinalIgnoreCase);

            if (quiet.Contains(skill, StringComparer.Ordinal) && !named)
            {
                wrong.Add($"{skill} stops without a PR and its row never says when");
            }

            if (!quiet.Contains(skill, StringComparer.Ordinal) && named)
            {
                wrong.Add($"{skill}'s row promises an ending the skill no longer has");
            }
        }

        const string silent = "A row that does not say when a skill opens nothing leaves a reader to conclude "
            + "it always opens something, and a quiet nightly run then reads as a failure. Offenders:";

        wrong.ShouldBeEmpty(silent);

        // The count has to sit in the sentence that names the ending rather than anywhere in the opening.
        // The intro repeats the count twice, once about the PR and once about its absence, so a check on the
        // whole paragraph is satisfied by the wrong one — which is how this assertion first passed a
        // mutation that deleted the claim (.mtk/paths-93/mutate-skills-page.mjs).
        var claim = Intro()
            .Split('.')
            .FirstOrDefault(sentence => Absences.All(
                token => sentence.Contains(token, StringComparison.OrdinalIgnoreCase)));

        claim.ShouldNotBeNull(
            "The page's opening no longer says a run can end with no branch, no commit and no pull request.");

        var counted = $"The sentence about the ending without a PR must count the skills that have one: "
            + $"'All {Count(quiet.Count)}'. A hedge there ('some of them') is the omission this page already "
            + "made once, one level vaguer.";

        claim.ShouldContain($"All {Count(quiet.Count)}", Case.Insensitive, counted);
    }

    /// <summary>
    /// The three triage routes carry the conditions <c>docs-feedback</c>'s own verdict table decides from.
    /// </summary>
    /// <remarks>
    /// Derived from that table because each verdict selects a sentence <see cref="FeedbackReplyText"/> posts
    /// verbatim under the reviewer's comment, so a route with the wrong trigger does not merely mislead a
    /// reader of the page: it puts a false statement in Confluence, signed by the account DocuMe
    /// authenticates as.
    /// </remarks>
    [Fact]
    public void The_triage_routes_carry_the_conditions_the_skill_decides_from()
    {
        var verdicts = Verdicts();
        var triaged = TriagedStatuses();

        verdicts.Select(verdict => verdict.Status).ShouldBe(
            triaged,
            ignoreOrder: true,
            "docs-feedback/SKILL.md's verdict table and the statuses the reply pass answers have diverged.");

        var section = string.Join('\n', Section("`/docs-feedback`"));

        section.ShouldContain(
            Count(triaged.Count),
            Case.Insensitive,
            "The page counts the triage routes, and the count is no longer the number of triaged statuses.");

        foreach (var (status, condition) in verdicts)
        {
            var trigger = Trigger(condition);

            var missing = $"The page must route on the condition the skill decides '{status}' from "
                + $"(\"{trigger}\"). A route with a narrower trigger sends an item to the verdict whose "
                + "sentence the CLI posts under the reviewer's comment, and that sentence is then a lie.";

            section.ShouldContain(trigger, Case.Insensitive, missing);
        }
    }

    /// <summary>
    /// The <c>baselineSha</c> paragraphs name every skill that may write the field, each with the value it
    /// stamps.
    /// </summary>
    [Fact]
    public void Every_skill_that_may_write_the_baseline_says_so_in_its_own_section()
    {
        var writers = Skills().Where(skill => Text(skill).Contains(WriterClause, StringComparison.Ordinal)).ToList();

        writers.ShouldNotBeEmpty($"No SKILL.md says which '{WriterClause}', so nothing here is derived.");

        var field = JsonNamingPolicy.CamelCase.ConvertName(nameof(DocumeState.BaselineSha));
        var wrong = new List<string>();

        foreach (var skill in Skills())
        {
            var section = string.Join('\n', Section($"`/{skill}`"));
            var mentions = section.Contains(field, StringComparison.Ordinal);

            if (writers.Contains(skill, StringComparer.Ordinal) && !mentions)
            {
                wrong.Add($"{skill} may write `{field}` and its section never mentions it");
            }
        }

        const string silent = "A section that says nothing about the baseline reads as a skill that does not "
            + "touch it. The consequence is the one /docs-refresh's own procedure warns about: a refresh that "
            + "rewrites pages and leaves the field alone reports the same pages again tomorrow night. "
            + "Offenders:";

        wrong.ShouldBeEmpty(silent);

        foreach (var (skill, stamp) in BaselineStamps)
        {
            writers.ShouldContain(
                skill,
                $"{skill} no longer writes `{field}`, so its stamp value is not the page's to state.");
            Text(skill).ShouldContain(
                stamp,
                Case.Insensitive,
                $"'{stamp}' is no longer {skill}/SKILL.md's word for what it stamps.");

            var value = $"`/{skill}`'s section must say it stamps the {stamp} sha. The two generation skills "
                + "stamp different values, so one rule stated for the field is wrong for one of them.";

            string.Join('\n', Section($"`/{skill}`")).ShouldContain(stamp, Case.Insensitive, value);
        }

        // The on-branch refresh is the exception to its own stamp, and it is the case a user asks for by
        // name ("refresh the docs for the change I just made"). Named only when the skill still has it.
        const string BranchCase = "merge base";

        if (Text("docs-refresh").Contains(BranchCase, StringComparison.OrdinalIgnoreCase))
        {
            const string exception = "/docs-refresh's section must name the run that starts from a merge base: "
                + "it is the one that deliberately does not stamp, and a reader applying the general rule to "
                + "it points the next nightly diff at a commit the default branch does not contain.";

            string.Join('\n', Section("`/docs-refresh`")).ShouldContain(BranchCase, Case.Insensitive, exception);
        }
    }

    /// <summary>
    /// No source under <c>src/</c> assigns <c>BaselineSha</c> anything but a copy of itself, which is the
    /// page's "no CLI command writes it".
    /// </summary>
    /// <remarks>
    /// An absence claim, so it is checked by scanning for the thing that would falsify it rather than by
    /// naming today's commands: a future <c>docume baseline --set</c> would make the sentence false, and
    /// nothing else in the suite would notice. Reads through (<c>state.BaselineSha</c> into a banner or a
    /// status report) are the two that exist and are allowed.
    /// </remarks>
    [Fact]
    public void No_command_writes_the_baseline_the_page_says_only_a_skill_writes()
    {
        var assignment = $"{nameof(DocumeState.BaselineSha)} =";
        var offenders = new List<string>();

        foreach (var file in Sources())
        {
            var lines = File.ReadAllLines(file);

            for (var line = 0; line < lines.Length; line++)
            {
                var at = lines[line].IndexOf(assignment, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                var rhs = lines[line][(at + assignment.Length)..].Trim().TrimEnd(',', ';');
                if (rhs.EndsWith(nameof(DocumeState.BaselineSha), StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Path.GetRelativePath(DocumeCli.RepoRoot, file)}:{line + 1} sets it to {rhs}");
            }
        }

        const string written = "The page tells a reader the generation skills own the baseline and no CLI "
            + "command writes it — which is also why `docume drift` can refuse to run without one. A command "
            + "that sets it makes that sentence false and retires whatever drift the old value still owed. "
            + "Offenders:";

        offenders.ShouldBeEmpty(written);
    }

    /// <summary>
    /// The untrusted-input warning says what happens to instruction-shaped text: quoted in the PR body, not
    /// acted on.
    /// </summary>
    /// <remarks>
    /// The skill's PR body has a section for it precisely so a human sees the attempt (§1.3, PLAN.md §9). A
    /// page that instead describes the text as "a claim about the page it was left on, and declined" hides
    /// the part a reader would want: the account that wrote it is reported.
    /// </remarks>
    [Fact]
    public void The_untrusted_input_warning_says_the_text_is_quoted_and_not_acted_on()
    {
        var skill = Text("docs-feedback");
        var heading = skill
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("## Instruction-shaped", StringComparison.Ordinal));

        heading.ShouldNotBeNull(
            "docs-feedback/SKILL.md's PR body no longer has a section for instruction-shaped text, so the "
                + "page's warning is describing something that does not happen.");

        var warning = string.Join('\n', PageLines().Where(line => line.StartsWith('>')));

        warning.ShouldNotBeEmpty($"{PagePath} no longer carries the untrusted-input warning (rule §1.3).");

        const string acted = "The warning must say instruction-shaped text is not acted on. It is the one "
            + "clause no run may relax, and the page is where a reviewer checks what the plugin promises.";

        warning.ShouldContain("not acted on", Case.Insensitive, acted);

        const string quoted = "The warning must say the text is quoted in the pull request body: that is what "
            + "turns an injection attempt into something a human sees rather than something silently dropped.";

        warning.ShouldContain("pull request body", Case.Insensitive, quoted);
    }

    /// <summary>
    /// The install block is the slash-command form the README ships, with the marketplace-qualified plugin id
    /// the manifests spell.
    /// </summary>
    /// <remarks>
    /// Asserted against README.md rather than a literal because the README is the copy a release advertises
    /// (PLAN.md §12) and <see cref="Packaging.QuickstartTests"/> already holds it to the tag; two install
    /// stories in one repo means one of them is wrong and a reader cannot tell which.
    /// </remarks>
    [Fact]
    public void The_install_block_gives_the_lines_the_readme_gives()
    {
        var (fence, block) = InstallBlock();

        block.ShouldNotBeEmpty($"{PagePath}'s Installing section no longer shows how to install the plugin.");

        const string shell = "The plugin half is a slash command inside Claude Code, not a shell command "
            + "(PLAN.md §12), and a bash fence tells a reader to paste it into a terminal.";

        fence.ShouldNotContain("bash", Case.Insensitive, shell);

        var readme = File.ReadAllText(Path.Combine(DocumeCli.RepoRoot, ReadmePath));

        foreach (var line in block)
        {
            line.ShouldStartWith(
                "/",
                Case.Sensitive,
                $"'{line}' is not a slash command, so the block mixes two ways of installing.");

            var diverged = $"{ReadmePath} does not contain '{line}'. The page and the README are the same "
                + "instruction, and the one a reader follows is whichever they found first.";

            readme.ShouldContain(line, customMessage: diverged);
        }

        var qualified = $"{ManifestName(Path.Combine("plugin", ".claude-plugin", "plugin.json"))}@"
            + ManifestName(Path.Combine(".claude-plugin", "marketplace.json"));

        var unqualified = $"The install must name the plugin as `{qualified}`. The bare name resolves out of "
            + "whichever marketplace answers first, and this repository being its own marketplace is the "
            + "whole reason the qualifier exists.";

        string.Join('\n', block).ShouldContain(qualified, customMessage: unqualified);
    }

    /// <summary>
    /// The Installing section says what a repo that has not run <c>docume init</c> actually gets, which is
    /// <c>dotnet</c>'s error and not DocuMe's.
    /// </summary>
    /// <remarks>
    /// The manifest pin is written by <c>init</c> (<see cref="Core.Scaffolding.ProjectScaffolder"/>, PLAN.md
    /// §12), so a repo that has not run it cannot reach any DocuMe code at all through
    /// <c>dotnet tool run docume</c>. The page said the opposite, which sends a consumer debugging a failed
    /// skill run looking for the wrong message. Both endings are recorded in the probe.
    /// </remarks>
    [Fact]
    public void The_install_section_names_the_error_a_missing_tool_manifest_gives()
    {
        var section = string.Join('\n', Section("Installing"));

        const string dotnets = "The Installing section must quote the error a repo without the pin gets, "
            + $"which is dotnet's and not DocuMe's. See {ProbePath} for both endings.";

        section.ShouldContain(ManifestError, customMessage: dotnets);

        var pin = Path.Combine(".config", "dotnet-tools.json").Replace('\\', '/');

        const string invocation = "The section must say the skills invoke `dotnet tool run docume`: it is what "
            + "makes the pin load-bearing, and a reader who installs the tool globally and nothing else has a "
            + "plugin whose every command fails.";

        section.ShouldContain("dotnet tool run docume", customMessage: invocation);
        section.ShouldContain(
            pin,
            customMessage: $"The section must name `{pin}` as what `docume init` writes (PLAN.md §12).");
    }

    /// <summary>The statuses the reply pass answers, from the constants rather than a list.</summary>
    private static List<string> TriagedStatuses() => typeof(FeedbackStatus)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral)
        .Select(field => field.GetRawConstantValue() as string)
        .OfType<string>()
        .Where(FeedbackReplyText.IsTriaged)
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// <c>docs-feedback</c>'s verdict table as (status, when it is the right verdict), triaged rows only.
    /// </summary>
    /// <remarks>
    /// The file has three tables and this reads rows from all of them, then keeps the ones whose first cell
    /// is a status the reply pass answers. A status the skill misspelled therefore drops out here and fails
    /// the set comparison rather than passing vacuously.
    /// </remarks>
    private static List<(string Status, string Condition)> Verdicts() => Text("docs-feedback")
        .Split('\n')
        .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
        .Select(line => line.Split('|'))
        .Where(cells => cells.Length > 4)
        .Select(cells => (Status: cells[1].Trim().Trim('`'), Condition: cells[3].Trim()))
        .Where(row => FeedbackReplyText.IsTriaged(row.Status))
        .OrderBy(row => row.Status, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// The decisive clause of a verdict's condition: everything up to the first <c>, so </c> or <c> and </c>,
    /// which is where the cell stops saying when and starts saying what follows.
    /// </summary>
    private static string Trigger(string condition)
    {
        int[] cuts =
        [
            condition.IndexOf(", so ", StringComparison.Ordinal),
            condition.IndexOf(" and ", StringComparison.Ordinal),
        ];

        var at = cuts.Where(cut => cut > 0).DefaultIfEmpty(-1).Min();

        return at < 0 ? condition : condition[..at];
    }

    /// <summary>Whether a skill documents an ending that opens no pull request.</summary>
    /// <remarks>
    /// They phrase it differently and all of them name the same three things, so the tokens are the
    /// derivation: "no branch, no commit, no PR" in any order.
    /// </remarks>
    private static bool DocumentsAnEmptyRun(string skill)
    {
        var text = Text(skill);

        string[] absent = ["no branch", "no commit", "no PR"];

        return absent.All(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The <c>docs/…-</c> branch prefix a skill's SKILL.md opens its PR on.</summary>
    private static string BranchPrefix(string skill)
    {
        var prefixes = Text(skill)
            .Split('\n', ' ', '"', '`', '<')
            .Where(token => token.StartsWith("docs/", StringComparison.Ordinal)
                && token.Contains('-', StringComparison.Ordinal))
            .Select(token => token[..(token.IndexOf('-', StringComparison.Ordinal) + 1)])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        prefixes.Count.ShouldBe(
            1,
            $"{skill}/SKILL.md names {prefixes.Count} branch prefixes ({string.Join(", ", prefixes)}).");

        return prefixes[0];
    }

    /// <summary>The page's opening paragraphs, up to the table.</summary>
    private static string Intro() => string.Join(
        '\n',
        PageLines().TakeWhile(line => !line.StartsWith(RowHeader, StringComparison.Ordinal)));

    private static List<(string Skill, string OpensNothing, string Branch)> Rows()
    {
        var lines = PageLines();
        var header = lines.FindIndex(line => line.StartsWith(RowHeader, StringComparison.Ordinal));

        header.ShouldBeGreaterThanOrEqualTo(0, $"{PagePath} no longer has the skill table.");

        return lines
            .Skip(header + 2)
            .TakeWhile(line => line.StartsWith('|'))
            .Select(row => row.Split('|'))
            .Where(cells => cells.Length > 5)
            .Select(cells => (
                Skill: cells[1].Trim().Trim('`').TrimStart('/'),
                OpensNothing: cells[3],
                Branch: cells[4]))
            .ToList();
    }

    /// <summary>The fence line and the command lines of the Installing section's first code block.</summary>
    private static (string Fence, List<string> Block) InstallBlock()
    {
        var section = Section("Installing");
        var opening = section.FindIndex(line => line.StartsWith("```", StringComparison.Ordinal));

        opening.ShouldBeGreaterThanOrEqualTo(0, $"{PagePath}'s Installing section has no code block.");

        var block = section
            .Skip(opening + 1)
            .TakeWhile(line => !line.StartsWith("```", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        return (section[opening], block);
    }

    /// <summary>The lines of the section whose heading contains <paramref name="anchor"/>.</summary>
    private static List<string> Section(string anchor)
    {
        var lines = PageLines();

        var start = lines.FindIndex(line =>
            line.StartsWith("## ", StringComparison.Ordinal)
            && line.Contains(anchor, StringComparison.Ordinal));

        start.ShouldBeGreaterThanOrEqualTo(0, $"{PagePath} no longer has a '{anchor}' section.");

        return lines
            .Skip(start + 1)
            .TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>The <c>name</c> of a manifest, as the id a user types is spelled.</summary>
    private static string ManifestName(string relativePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(DocumeCli.RepoRoot, relativePath)));

        return document.RootElement.GetProperty("name").GetString()
            ?? throw new InvalidOperationException($"{relativePath} declares no name.");
    }

    /// <summary>Every committed C# source under <c>src/</c>, build output excluded.</summary>
    private static IEnumerable<string> Sources() => Directory
        .EnumerateFiles(Path.Combine(DocumeCli.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static List<string> Skills() => Directory
        .EnumerateDirectories(Path.Combine(DocumeCli.RepoRoot, "plugin", "skills"))
        .Select(Path.GetFileName)
        .OfType<string>()
        .Order(StringComparer.Ordinal)
        .ToList();

    private static string Text(string skill)
        => File.ReadAllText(Path.Combine(DocumeCli.RepoRoot, "plugin", "skills", skill, "SKILL.md"));

    private static List<string> PageLines()
        => File.ReadAllLines(Path.Combine(DocumeCli.RepoRoot, PagePath)).ToList();

    /// <summary>The number word the page's prose uses for <paramref name="count"/>.</summary>
    private static string Count(int count) => count switch
    {
        1 => "one",
        2 => "two",
        3 => "three",
        4 => "four",
        _ => throw new ArgumentOutOfRangeException(
            nameof(count),
            count,
            "The page counts these sets in words, and this one is outside the range it spells out."),
    };
}
