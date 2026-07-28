using System.Text.RegularExpressions;
using Shouldly;

namespace DocuMe.Core.Tests.Packaging;

/// <summary>
/// <c>CHANGELOG.md</c> (PLAN.md §12) as a set of claims about the tree, checked against the tree.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="QuickstartTests"/> pins one thing about this file — that its leading version heading agrees
/// with <c>Directory.Build.props</c>. Nothing pinned the eighty lines underneath, and they are what a
/// consumer actually reads to decide what they are getting. A wrong line here is not a stale cross
/// reference: it is a decision made on bad information, by a reader with no way to tell a live claim from
/// one that was true four milestones ago.
/// </para>
/// <para>
/// The section that rots hardest is <c>### Not in this release</c>, because it claims ABSENCE. Every other
/// line goes stale when something is deleted, which is rare and deliberate. An absence claim goes stale
/// when somebody BUILDS the thing, which is the normal case, and building it is the one event nobody
/// thinks to grep the changelog for. That is exactly what happened: the composite action was listed as
/// not-in-this-release, then shipped, and the bullet sat there through eleven iterations.
/// </para>
/// <para>
/// These tests say nothing about prose or ordering. The property under test is that what the file promises
/// — and what it promises is missing — is still true.
/// </para>
/// </remarks>
public sealed partial class ChangelogTests
{
    /// <summary>The section listing what the release deliberately does not contain.</summary>
    private const string AbsenceSection = "### Not in this release";

    /// <summary>Where the packaging and CI claims live.</summary>
    private const string PackagingSection = "### Packaging";

    /// <summary>
    /// Options the commands carry for plumbing. A changelog that lists a command's real flags and skips
    /// these is not making a claim about them, so they do not count against an inventory.
    /// </summary>
    /// <remarks>
    /// Deliberately unguarded, and the one bound in this class that is: its silent direction is a SURPLUS
    /// entry, and nothing in the tree separates plumbing from a feature flag somebody wanted excused. Every
    /// candidate rule (declared by N commands, absent from the changelog, most-shared-flag) either misses
    /// <c>--allow-protected-space</c>, which the inventory test below names as the hazard, or is undone by
    /// the same edit that widens the list. Growing this array is a judgement; make it in the open.
    /// </remarks>
    private static readonly string[] Plumbing = ["--config", "--state"];

    /// <summary>Targets <c>init</c> writes that the changelog names by path.</summary>
    private static readonly string[] ScaffoldedPaths =
        ["_meta/STYLE.md", "_meta/state.json", ".config/dotnet-tools.json"];

    /// <summary>Files the changelog says <c>/docs-loop</c> keeps.</summary>
    private static readonly string[] LoopPaths = ["_meta/PROGRESS.md", "_meta/GAPS.md"];

    /// <summary>
    /// The house analyzer packs, named so the count claim can say WHICH ones rather than only how many.
    /// It bounds nothing: the number is read off <c>Directory.Packages.props</c>.
    /// </summary>
    private static readonly string[] AnalyzerPacks =
        ["StyleCop.Analyzers", "Roslynator.Analyzers", "SonarAnalyzer.CSharp", "Meziantou.Analyzer"];

    /// <summary>Wordings that mark a list as a sample rather than an inventory.</summary>
    private static readonly string[] Hedges = ["e.g.", "such as", "among", "including"];

    /// <summary>
    /// Flags System.CommandLine provides rather than <c>&lt;Command&gt;Command.cs</c> declaring them. They
    /// are real, they are documented as real in <c>docs/wiki/20-reference/cli.md</c>, and no
    /// <c>new Option&lt;&gt;</c> will ever be found for them — so a changelog naming one is not inventing it.
    /// Both halves of that sentence are asserted, because this list is what excuses a flag from existing.
    /// </summary>
    private static readonly string[] Builtins = ["--help", "--version"];

    [Fact]
    public void The_changelog_describes_a_release_and_is_not_a_stub()
    {
        // Everything below searches this text; a placeholder file would pass most of them vacuously.
        var changelog = Changelog();

        changelog.ShouldContain(
            PackagingSection,
            customMessage: $"CHANGELOG.md lost '{PackagingSection}', which the packaging assertions read.");
        changelog.ShouldContain(
            "### The CLI",
            customMessage: "CHANGELOG.md no longer describes the CLI, so it cannot be release notes.");
    }

    [Fact]
    public void Nothing_the_changelog_lists_as_absent_is_in_the_tree()
    {
        var section = Section(AbsenceSection);

        // A deliberate removal of the section should land here and be re-read by a human, not pass quietly:
        // the assertions below are the only thing standing between an absence claim and a shipped feature.
        section.ShouldNotBeNullOrWhiteSpace(
            $"CHANGELOG.md has no '{AbsenceSection}'. If that is deliberate, delete this test with it; "
            + "if it was renamed, point the test at the new heading.");

        // Only claims this repository can falsify. The Moberg marketplace entry is also listed as absent
        // and is deliberately NOT here: that entry lives in another repository, so no file in this tree
        // can prove it shipped, and a check that cannot fail is worse than no check.
        var falsifiable = new (string Claim, Regex Mentions, string Evidence)[]
        {
            ("the composite action", CompositeActionMention(), Path.Combine("actions", "action.yml")),
            ("the workflow templates", TemplatesAbsenceMention(), Path.Combine("templates", "workflows")),
        };

        foreach (var (claim, mentions, evidence) in falsifiable)
        {
            if (!mentions.IsMatch(section))
            {
                continue;
            }

            var path = Path.Combine(RepoRoot, evidence);
            var shipped = File.Exists(path) || Directory.Exists(path);

            shipped.ShouldBeFalse(
                $"CHANGELOG.md lists {claim} under '{AbsenceSection}', but {evidence} is in the tree. "
                + "Move it into the section describing what ships and say what a consumer gets.");
        }
    }

    [Fact]
    public void The_composite_action_is_described_where_a_consumer_would_look_for_it()
    {
        // The other half of the bug above: the action shipped at iter75 and the release notes went on
        // saying it had not. Absence of a false claim is not the same as presence of a true one.
        File.Exists(Path.Combine(RepoRoot, "actions", "action.yml")).ShouldBeTrue(
            "actions/action.yml is gone. If the composite action was withdrawn, this test and the "
            + "CHANGELOG bullet describing it both need to go.");

        var packaging = Section(PackagingSection);

        packaging.ShouldNotBeNull($"CHANGELOG.md has no '{PackagingSection}' section.");

        const string unnamed = "The composite action ships and the release notes never name its ref, so a "
            + "consumer cannot find out how to point at it. Name it under Packaging.";

        packaging.ShouldContain("actions@v", customMessage: unnamed);
    }

    [Fact]
    public void Every_command_the_changelog_describes_is_one_you_can_run()
    {
        var registered = RegisteredCommands();
        var described = DescribedCommands();

        described.ShouldNotBeEmpty("The changelog describes no `docume` subcommand at all.");

        var unknown = described.Except(registered, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        unknown.ShouldBeEmpty(
            $"CHANGELOG.md describes commands that are not registered: [{string.Join(", ", unknown)}].");
    }

    [Fact]
    public void Every_command_that_ships_has_a_line_in_the_release_notes()
    {
        var registered = RegisteredCommands();
        var described = DescribedCommands();

        var undocumented = registered.Except(described, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        // The quieter direction, and the one a release forgets: a command a consumer paid for and cannot
        // discover from the notes.
        undocumented.ShouldBeEmpty(
            $"These commands ship and the changelog never mentions them: [{string.Join(", ", undocumented)}].");
    }

    [Fact]
    public void Every_flag_the_changelog_names_exists_on_the_command_it_names_it_under()
    {
        var wrong = new List<string>();

        foreach (var command in RegisteredCommands().Order(StringComparer.Ordinal))
        {
            var real = OptionsOf(command);
            var bullet = BulletFor(command);

            if (real is null || bullet is null)
            {
                continue;
            }

            var invented = NamedFlags(bullet)
                .Except(real, StringComparer.Ordinal)
                .Except(Builtins, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            wrong.AddRange(invented.Select(flag => $"`{flag}` under `docume {command}`"));
        }

        wrong.ShouldBeEmpty($"CHANGELOG.md names flags that do not exist: [{string.Join(", ", wrong)}].");
    }

    [Fact]
    public void A_flag_list_written_as_an_inventory_is_a_complete_inventory()
    {
        var incomplete = new List<string>();
        var inventories = 0;

        foreach (var command in RegisteredCommands().Order(StringComparer.Ordinal))
        {
            var real = OptionsOf(command);
            var bullet = BulletFor(command);

            if (real is null || bullet is null || !ReadsAsInventory(bullet))
            {
                continue;
            }

            inventories++;

            var named = NamedFlags(bullet);

            var missing = real
                .Except(named, StringComparer.Ordinal)
                .Except(Plumbing, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (missing.Count == 0)
            {
                continue;
            }

            incomplete.Add($"`docume {command}` omits [{string.Join(", ", missing)}]");
        }

        // The failure this catches has now shipped three times in three different files: a list that reads
        // as exhaustive, quietly missing `--allow-protected-space`, the one escape from the write lock.
        // Either name every flag or write the sentence as a sample ("e.g.", "such as", "including").
        incomplete.ShouldBeEmpty(
            $"A changelog flag list reads as complete but is not: {string.Join("; ", incomplete)}.");

        // Exactly one bullet qualifies today, so `Hedges` and the opener between them decide whether this
        // test reads anything at all: one hedge word added to that bullet retires the check and nothing
        // fails. A count of zero problems is only evidence once the denominator is asserted.
        const string vacuous = "No CHANGELOG.md bullet reads as a complete flag inventory, so this test "
            + "checked nothing. Either the opener wording moved or the bullet gained a hedge word.";

        inventories.ShouldBeGreaterThan(0, vacuous);
    }

    [Fact]
    public void The_analyzer_packs_this_class_names_are_the_ones_the_props_file_declares()
    {
        var declared = DeclaredAnalyzerPacks();

        var unnamed = declared.Except(AnalyzerPacks, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var withdrawn = AnalyzerPacks.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        // Both directions, because the counted claim above now reads this file rather than this list. An
        // added pack makes the changelog's number wrong; a removed one makes this list describe a tree
        // that moved on.
        unnamed.ShouldBeEmpty(
            $"Directory.Packages.props declares analyzer packs this class does not name: "
            + $"[{string.Join(", ", unnamed)}]. Name them here and restate the number in CHANGELOG.md and README.md.");

        withdrawn.ShouldBeEmpty(
            $"This class names analyzer packs Directory.Packages.props no longer declares: "
            + $"[{string.Join(", ", withdrawn)}].");
    }

    [Fact]
    public void Every_path_these_tests_filter_on_is_a_path_the_changelog_still_names()
    {
        var changelog = Changelog();

        var unmentioned = ScaffoldedPaths
            .Concat(LoopPaths)
            .Where(path => !changelog.Contains(path, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Both checks over these paths run only where the changelog names one, so a reworded sentence does
        // not fail them — it retires them, and leaves a list describing a release note two edits ago.
        unmentioned.ShouldBeEmpty(
            $"CHANGELOG.md no longer names [{string.Join(", ", unmentioned)}], so nothing verifies them. "
            + "Re-point the entry at the new wording, or drop it along with the claim it was checking.");
    }

    [Fact]
    public void The_flags_exempted_as_built_ins_are_built_in()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var command in RegisteredCommands())
        {
            var options = OptionsOf(command);

            if (options is null)
            {
                continue;
            }

            declared.UnionWith(options);
        }

        var real = Builtins.Where(flag => declared.Contains(flag)).Order(StringComparer.Ordinal).ToList();

        // A built-in is excused from existing because no `new Option<>` declares it. One that IS declared is
        // an ordinary flag, and exempting it turns off the check that the changelog named it under the
        // right command — the only check there is, since CliReferencePageTests skips CHANGELOG.md.
        real.ShouldBeEmpty(
            $"[{string.Join(", ", real)}] are exempted as System.CommandLine built-ins and a command "
            + "declares them, so the exemption is hiding a real flag rather than describing a built-in one.");

        var cli = File.ReadAllText(Path.Combine(RepoRoot, "docs", "wiki", "20-reference", "cli.md"));

        var undocumented = Builtins
            .Where(flag => !cli.Contains($"`{flag}`", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        undocumented.ShouldBeEmpty(
            $"[{string.Join(", ", undocumented)}] are exempted as built-ins and "
            + "docs/wiki/20-reference/cli.md does not document them, so nothing says they are real.");
    }

    [Theory]
    [InlineData(@"(?<n>\w+) workflow templates shipped by", "templates/workflows/*.yml")]
    [InlineData(@"(?<n>\w+) Moberg house analyzer packs", "analyzer packs in Directory.Packages.props")]
    [InlineData(@"scaffolds a consumer repo in (?<n>\w+) targets", "rows in the README's scaffold table")]
    public void Every_number_the_changelog_states_is_the_number_in_the_tree(string pattern, string counts)
    {
        var match = Regex.Match(Changelog(), pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        // A regex that stopped matching means the sentence was rewritten and the count is now unpinned,
        // which is the same hole as a wrong count.
        match.Success.ShouldBeTrue(
            $"CHANGELOG.md no longer states a count for {counts} in the form this test reads. "
            + "Re-point the pattern at the new wording rather than deleting the case.");

        var stated = Cardinal(match.Groups["n"].Value);

        stated.ShouldNotBeNull($"'{match.Groups["n"].Value}' is not a number this test can read.");
        stated.ShouldBe(ActualCount(pattern), $"CHANGELOG.md states the wrong number of {counts}.");
    }

    [Fact]
    public void Every_skill_and_scaffolded_path_the_changelog_names_is_real()
    {
        var changelog = Changelog();
        var missing = new List<string>();

        foreach (var skill in SkillMention().Matches(changelog).Select(match => match.Groups["skill"].Value).Distinct(StringComparer.Ordinal))
        {
            if (Directory.Exists(Path.Combine(RepoRoot, "plugin", "skills", skill)))
            {
                continue;
            }

            missing.Add($"/{skill}");
        }

        var sources = ScaffoldingSources();

        missing.AddRange(ScaffoldedPaths
            .Where(path => changelog.Contains(path, StringComparison.Ordinal))
            .Where(path => !sources.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)));

        var loop = File.ReadAllText(Path.Combine(RepoRoot, "plugin", "skills", "docs-loop", "SKILL.md"));

        missing.AddRange(LoopPaths
            .Where(path => changelog.Contains(path, StringComparison.Ordinal))
            .Where(path => !loop.Contains(Path.GetFileName(path), StringComparison.Ordinal)));

        missing.ShouldBeEmpty($"CHANGELOG.md names things nothing in the tree provides: [{string.Join(", ", missing)}].");
    }

    /// <summary>The subcommands <c>Program.cs</c> hangs off the root, lowercased.</summary>
    private static HashSet<string> RegisteredCommands()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src", "DocuMe.Cli", "Program.cs"));

        var names = CommandRegistration()
            .Matches(program)
            .Select(match => match.Groups["name"].Value.ToLowerInvariant());

        var registered = new HashSet<string>(names, StringComparer.Ordinal);

        registered.ShouldNotBeEmpty("No `<Name>Command.Build()` found in Program.cs, so the scan is broken.");

        return registered;
    }

    /// <summary>Every <c>`docume &lt;word&gt;</c> the changelog spells inside a code span.</summary>
    private static HashSet<string> DescribedCommands()
    {
        var names = CommandMention()
            .Matches(Changelog())
            .Select(match => match.Groups["name"].Value);

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>The options <c>&lt;Command&gt;Command.cs</c> declares, or null when there is no such file.</summary>
    private static HashSet<string>? OptionsOf(string command)
    {
        var name = char.ToUpperInvariant(command[0]) + command[1..];
        var path = Path.Combine(RepoRoot, "src", "DocuMe.Cli", "Commands", $"{name}Command.cs");

        if (!File.Exists(path))
        {
            return null;
        }

        var flags = OptionDeclaration()
            .Matches(File.ReadAllText(path))
            .Select(match => match.Groups["flag"].Value);

        return new HashSet<string>(flags, StringComparer.Ordinal);
    }

    /// <summary>
    /// The bullet describing one command: from <c>- `docume &lt;name&gt;</c> to the next bullet or heading,
    /// so a flag is only ever read against the command it was written under.
    /// </summary>
    private static string? BulletFor(string command)
    {
        var lines = Changelog().Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith($"- `docume {command}", StringComparison.Ordinal));

        if (start < 0)
        {
            return null;
        }

        var collected = new List<string> { lines[start] };

        for (var i = start + 1; i < lines.Length && !StartsBlock(lines[i]); i++)
        {
            collected.Add(lines[i]);
        }

        return string.Join('\n', collected);
    }

    private static bool StartsBlock(string line) =>
        line.StartsWith('-') || line.StartsWith('#');

    private static HashSet<string> NamedFlags(string text)
    {
        var flags = FlagMention().Matches(text).Select(match => match.Groups["flag"].Value);

        return new HashSet<string>(flags, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a bullet's flag list reads as the whole set. "With <c>--a</c>, <c>--b</c> and <c>--c</c>."
    /// claims completeness; the hedged forms explicitly do not.
    /// </summary>
    private static bool ReadsAsInventory(string bullet)
    {
        if (!InventoryOpener().IsMatch(bullet))
        {
            return false;
        }

        return !Array.Exists(Hedges, hedge => bullet.Contains(hedge, StringComparison.OrdinalIgnoreCase));
    }

    private static int? Cardinal(string word)
    {
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["three"] = 3,
            ["four"] = 4,
            ["six"] = 6,
            ["seven"] = 7,
            ["thirteen"] = 13,
        };

        if (numbers.TryGetValue(word, out var spelled))
        {
            return spelled;
        }

        return int.TryParse(word, out var digits) ? digits : null;
    }

    /// <summary>What the tree actually holds for each counted claim, keyed by the pattern that found it.</summary>
    private static int ActualCount(string pattern)
    {
        if (pattern.Contains("workflow templates", StringComparison.Ordinal))
        {
            return Directory.EnumerateFiles(Path.Combine(RepoRoot, "templates", "workflows"), "*.yml").Count();
        }

        if (pattern.Contains("analyzer packs", StringComparison.Ordinal))
        {
            return DeclaredAnalyzerPacks().Count;
        }

        // init's scaffold targets. The README's step-3 table is the hand-reviewed list of them and is
        // already pinned against init's real output by QuickstartTests, so it is the authority here
        // rather than a second, independently-drifting guess at the same number.
        const string heading = "### 3. Scaffold your repo";

        var readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));
        var start = readme.IndexOf(heading, StringComparison.Ordinal);

        start.ShouldBeGreaterThanOrEqualTo(0, "README.md lost its scaffold section, so the count is unpinned.");

        // Stop at the next heading. Running to end-of-file sweeps up the Commands and Skills tables too,
        // which is how this counted 23 the first time it ran.
        var body = readme[(start + heading.Length)..];
        var next = NextHeading().Match(body);

        return TableRow().Count(next.Success ? body[..next.Index] : body);
    }

    private static List<string> ScaffoldingSources()
    {
        var directory = Path.Combine(RepoRoot, "src", "DocuMe.Core", "Scaffolding");

        // Every scaffolding source at once: which class writes which target has moved before, and the
        // claim under test is "init produces this", not "this particular class does".
        var text = Directory
            .EnumerateFiles(directory, "*.cs")
            .Select(File.ReadAllText)
            .ToList();

        // Derived from ScaffoldedPaths rather than written out again: a second spelling of the same three
        // file names is a mirror, and the direction that rots is a path added above without its name here.
        return ScaffoldedPaths
            .Select(path => Path.GetFileName(path))
            .Where(name => text.Exists(source => source.Contains(name, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// Every analyzer pack <c>Directory.Packages.props</c> declares, read off the file. Counting the
    /// <c>AnalyzerPacks</c> entries that appear there instead would bound the answer at four, so a fifth
    /// pack could never make the changelog's number wrong — only a deletion could.
    /// </summary>
    private static List<string> DeclaredAnalyzerPacks()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot, "Directory.Packages.props"));

        var packs = AnalyzerPackage()
            .Matches(props)
            .Select(match => match.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        packs.ShouldNotBeEmpty("Directory.Packages.props declares no analyzer pack, so this scan is broken.");

        return packs;
    }

    /// <summary>The text under <paramref name="heading"/>, up to the next heading of the same level or shallower.</summary>
    private static string? Section(string heading)
    {
        var changelog = Changelog();
        var start = changelog.IndexOf(heading, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        var body = changelog[(start + heading.Length)..];
        var next = NextHeading().Match(body);

        return next.Success ? body[..next.Index] : body;
    }

    private static string Changelog() =>
        File.ReadAllText(Path.Combine(RepoRoot, "CHANGELOG.md"));

    [GeneratedRegex(@"(?<name>\w+)Command\.Build\(\)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommandRegistration();

    [GeneratedRegex(@"`docume (?<name>[a-z][a-z-]*)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommandMention();

    [GeneratedRegex(@"new Option<[^>]+>\(""(?<flag>--[a-z-]+)""", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionDeclaration();

    [GeneratedRegex(@"`(?<flag>--[a-z-]+)`", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FlagMention();

    [GeneratedRegex(@"\bWith\s+`--", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InventoryOpener();

    [GeneratedRegex(@"`/(?<skill>docs-[a-z][a-z-]*)`", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SkillMention();

    [GeneratedRegex(@"composite\s+(GitHub\s+)?Action", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CompositeActionMention();

    [GeneratedRegex(@"workflow templates? (are|is) not", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TemplatesAbsenceMention();

    [GeneratedRegex(@"^#{2,3}\s", RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NextHeading();

    [GeneratedRegex(@"^\| `(?<path>[^`]+)`", RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TableRow();

    [GeneratedRegex(
        @"PackageVersion Include=""(?<id>[A-Za-z0-9.]*Analyzers?(?:\.[A-Za-z]+)?)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnalyzerPackage();

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>: the changelog ships in the tree and has no
    /// build artifact, so the shipped copy is the one under test.
    /// </summary>
    private static string RepoRoot { get; } = Locate();

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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so CHANGELOG.md cannot be found.");
    }
}
