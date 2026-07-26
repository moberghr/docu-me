using System.Text.RegularExpressions;
using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// PLAN.md §6's command specifications held against the surface the CLI actually has.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure this exists to catch.</strong> §6 is the build spec every milestone was derived
/// from, and nothing read it back. <see cref="Cli.CliReferencePageTests"/> pins
/// <c>docs/wiki/20-reference/cli.md</c> against <c>--help</c> in both directions and deliberately
/// excludes PLAN.md, because the plan narrates the build; <see cref="Packaging.ChangelogTests"/> pins the
/// release notes. So a flag §6 specified and nobody built was invisible to the suite, to the reference
/// page (which documents what exists) and to the milestone gates (which name deliverables, not options).
/// That is how <c>--notify-reviewers</c> — §6.2 step 7's "optionally post a footer comment" — sat
/// unbuilt through M2, M3 and a milestone review that called both feature-complete. Its name appeared
/// nowhere in <c>src/</c>, <c>tests/</c> or <c>docs/</c>.
/// </para>
/// <para>
/// <strong>Why the deviations are listed rather than tolerated.</strong> Three of §6's declarations are
/// still unbuilt (see <see cref="UnbuiltOptions"/>), and none of them is a bug this suite can decide to
/// fix: whether <c>docume</c> grows a global <c>--verbose</c>, or §6's global paragraph is corrected to
/// describe what shipped, is a spec decision. Listing each one keeps it visible and makes the record
/// double-entry — a new gap fails this test, and so does closing a gap without striking it off the list.
/// A blanket "compare only what is built" would have swallowed <c>--notify-reviewers</c> too.
/// </para>
/// <para>
/// The surface of record is the CLI's own <c>--help</c>, read from the process, and only the option
/// column of it: option names appear in description prose as well (<c>--changed-since</c> cites
/// <c>git diff --name-only</c>), and a scan of the whole help text reports those as options.
/// </para>
/// </remarks>
public sealed partial class PlanCommandSpecTests
{
    /// <summary>Every command §6 gives a subsection, in section order.</summary>
    private static readonly string[] SpecifiedCommands =
        ["init", "publish", "sync", "drift", "dashboard", "status", "convert"];

    /// <summary>
    /// The options §6's preamble calls global: "Global: <c>--config &lt;path&gt;</c> …, <c>--verbose</c>,
    /// <c>--json</c> (machine output)". Every command therefore declares them, and every command is
    /// checked for them.
    /// </summary>
    private static readonly string[] GlobalOptions = ["--config", "--verbose", "--json"];

    /// <summary>
    /// Flags §6 names as another tool's, which claim nothing about <c>docume</c>'s surface. Held to being
    /// still-present and still-foreign by
    /// <see cref="Every_foreign_flag_exclusion_is_still_earning_its_place"/>, so an exclusion cannot
    /// outlive the prose that needs it and start hiding a real option.
    /// </summary>
    private static readonly (string Option, string Owner)[] ForeignOptions =
        [("--name-only", "git diff --name-only, which §6.2 and §6.4 both cite as how the scope is computed")];

    /// <summary>
    /// §6 declarations that are not built, with the command they are missing from and why the gap stands.
    /// Every entry is a spec decision nobody has taken, not a defect this suite may quietly accept.
    /// </summary>
    private static readonly (string Option, string[] Commands, string Why)[] UnbuiltOptions =
    [
        ("--verbose", SpecifiedCommands,
            "Declared global by §6's preamble and built nowhere. No command takes it, and nothing in the "
            + "repo records a decision to drop it: either the flag is owed a verbosity level that reaches "
            + "the Spectre output and the Confluence client, or §6's preamble is owed a correction."),
        ("--json", ["init", "publish", "sync", "drift", "dashboard", "convert"],
            "Declared global by §6's preamble and built on `status` alone (StatusCommand.cs). `drift` "
            + "spells its machine output `--format json` instead, which §6.4 specifies and which covers "
            + "the same need under another name; the other five have no machine output at all."),
        ("--config", ["init", "convert"],
            "Built on the five commands that read docume.json to find a wiki. `init` writes the file in "
            + "the working directory rather than being pointed at one, and `convert` takes the wiki root "
            + "as its argument plus `--renderer`, so neither has a config to resolve — but §6's preamble "
            + "says global without excepting them."),
    ];

    private static readonly string[] PlanPath = ["PLAN.md"];

    /// <summary>
    /// Anti-vacuity guard: every other assertion here reads the parsed spec, so a renamed heading or a
    /// reformatted §6 would turn them all green by finding nothing at all.
    /// </summary>
    [Fact]
    public void Section_6_parses_into_one_subsection_per_command()
    {
        const string moved = "§6's global paragraph no longer names the three options this test checks "
            + "every command for. Reconcile GlobalOptions with the paragraph before trusting the rest.";

        SpecSections().Keys.ShouldBe(SpecifiedCommands, ignoreOrder: true);
        PreambleOptions().ShouldBe(GlobalOptions, ignoreOrder: true, customMessage: moved);
    }

    /// <summary>
    /// The check itself, in both directions: an option §6 declares and no command has fails, and so does
    /// a gap on <see cref="UnbuiltOptions"/> that has since been built.
    /// </summary>
    [Fact]
    public void Every_option_the_plan_declares_is_built_or_listed_as_unbuilt()
    {
        var sections = SpecSections();
        var invocations = InvokedOptions();

        var unlisted = new List<string>();
        var stale = new List<string>();

        foreach (var command in SpecifiedCommands)
        {
            var declared = new HashSet<string>(GlobalOptions, StringComparer.Ordinal);
            declared.UnionWith(sections[command]);
            declared.UnionWith(invocations.TryGetValue(command, out var named) ? named : []);
            declared.ExceptWith(ForeignOptions.Select(foreign => foreign.Option));

            var shipped = DeclaredOptions(command);
            var recorded = RecordedGaps(command);

            unlisted.AddRange(declared
                .Where(option => !shipped.Contains(option) && !recorded.Contains(option))
                .Select(option => $"`docume {command}` does not have {option}"));

            stale.AddRange(recorded
                .Where(shipped.Contains)
                .Select(option => $"`docume {command}` now has {option}"));
        }

        unlisted.ShouldBeEmpty(
            customMessage: "PLAN.md §6 specifies an option the CLI does not have. Build it, or record it "
                + $"in {nameof(UnbuiltOptions)} with why the gap stands — an unbuilt spec'd flag that "
                + "nothing reports is how --notify-reviewers survived three milestones.");

        stale.ShouldBeEmpty(
            customMessage: $"An option listed in {nameof(UnbuiltOptions)} is built now. Strike it off the "
                + "list: a record of gaps that keeps naming closed ones stops being read.");
    }

    /// <summary>
    /// A foreign-flag exclusion has to stay both true and needed. If <c>--name-only</c> ever becomes a real
    /// <c>docume</c> option, or leaves §6's prose, the exclusion is silently narrowing the check above.
    /// </summary>
    [Fact]
    public void Every_foreign_flag_exclusion_is_still_earning_its_place()
    {
        var plan = PlanText();
        var shipped = SpecifiedCommands.SelectMany(DeclaredOptions).ToHashSet(StringComparer.Ordinal);

        foreach (var (option, owner) in ForeignOptions)
        {
            var gone = $"{option} is excluded from the §6 sweep as {owner}, but §6 no longer mentions it. "
                + "Drop the exclusion rather than carrying a rule with nothing to rule on.";

            var claimed = $"{option} is excluded from the §6 sweep as belonging to {owner}, and it is now "
                + "a docume option. The exclusion is hiding a real part of the surface.";

            plan.ShouldContain(option, customMessage: gone);
            shipped.ShouldNotContain(option, customMessage: claimed);
        }
    }

    /// <summary>The gaps <see cref="UnbuiltOptions"/> records for one command.</summary>
    private static HashSet<string> RecordedGaps(string command) =>
        UnbuiltOptions
            .Where(gap => gap.Commands.Contains(command, StringComparer.Ordinal))
            .Select(gap => gap.Option)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Command → the options its own §6 subsection names.</summary>
    private static Dictionary<string, HashSet<string>> SpecSections()
    {
        var body = SectionSix();
        var sections = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var match in SubsectionHeading().Matches(body).Cast<Match>())
        {
            var command = match.Groups["name"].Value;
            var start = match.Index + match.Length;
            var next = SubsectionHeading().Match(body, start);
            var end = next.Success ? next.Index : body.Length;

            sections[command] = OptionNames(body[start..end]);
        }

        return sections;
    }

    /// <summary>The options named before the first subsection: §6's global paragraph.</summary>
    private static HashSet<string> PreambleOptions()
    {
        var body = SectionSix();
        var first = SubsectionHeading().Match(body);

        return OptionNames(first.Success ? body[..first.Index] : body);
    }

    /// <summary>
    /// Command → options named in a <c>docume …</c> invocation anywhere in the plan, §6 or not: §8-§12
    /// spell flags too (<c>docume sync --reply</c>), and a flag the plan tells someone to type is a flag
    /// the plan declares.
    /// </summary>
    private static Dictionary<string, HashSet<string>> InvokedOptions()
    {
        var invoked = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var match in Invocation().Matches(PlanText()).Cast<Match>())
        {
            var command = match.Groups["name"].Value;

            if (!SpecifiedCommands.Contains(command, StringComparer.Ordinal))
            {
                continue;
            }

            if (!invoked.TryGetValue(command, out var options))
            {
                options = new HashSet<string>(StringComparer.Ordinal);
                invoked[command] = options;
            }

            options.UnionWith(OptionNames(match.Groups["args"].Value));
        }

        return invoked;
    }

    private static HashSet<string> OptionNames(string text) =>
        OptionName().Matches(text).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>§6's body, up to §7's heading.</summary>
    private static string SectionSix()
    {
        var match = SectionSixBody().Match(PlanText());

        match.Success.ShouldBeTrue(
            "PLAN.md has no \"## 6. CLI command specifications\" section, so this whole file is reading "
            + "nothing. Point it at §6's new heading rather than deleting it.");

        return match.Groups["body"].Value;
    }

    /// <summary>
    /// The options one command declares, read out of the option column of its own <c>--help</c>. Alias
    /// lists (<c>-?, -h, --help</c>) contribute only their long forms, which is what PLAN.md spells.
    /// </summary>
    private static HashSet<string> DeclaredOptions(string command)
    {
        var run = DocumeCli.Invoke(DocumeCli.RepoRoot, command, "--help");
        run.Code.ShouldBe(0, run.Diagnostics);

        var lines = run.Output.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var index = lines.FindIndex(line => line.StartsWith("Options:", StringComparison.Ordinal));

        index.ShouldBeGreaterThanOrEqualTo(0, $"No \"Options:\" section in `docume {command} --help`.");

        var options = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines.Skip(index + 1).TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal)))
        {
            // Two-or-more spaces separate the option column from its description; a wrapped description
            // line contributes nothing, because none of its words start with "--".
            var column = OptionColumn().Match(line);

            if (!column.Success)
            {
                continue;
            }

            options.UnionWith(OptionNames(column.Groups["aliases"].Value));
        }

        options.ShouldNotBeEmpty($"Parsed no options at all out of `docume {command} --help`.");

        return options;
    }

    private static string PlanText() =>
        File.ReadAllText(Path.Combine(DocumeCli.RepoRoot, Path.Combine(PlanPath)));

    [GeneratedRegex(@"\n## 6\. CLI command specifications\n(?<body>[\s\S]*?)(?=\n## 7\. )", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SectionSixBody();

    [GeneratedRegex(@"### 6\.\d+ `docume (?<name>[a-z]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SubsectionHeading();

    [GeneratedRegex(@"`docume (?<name>[a-z]+)(?<args>[^`]*)`", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Invocation();

    [GeneratedRegex(@"--[a-z][a-z0-9-]*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionName();

    [GeneratedRegex(@"^ {2}(?<aliases>\S[^\n]*?)(?: {2,}|$)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionColumn();
}
