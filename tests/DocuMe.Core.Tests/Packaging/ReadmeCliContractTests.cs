using System.Text.RegularExpressions;
using DocuMe.Core.Config;
using DocuMe.Core.Scaffolding;
using Shouldly;

namespace DocuMe.Core.Tests.Packaging;

/// <summary>
/// The root <c>README.md</c>'s claims about how the CLI is <em>invoked</em>: which command takes which
/// option, which commands read <c>docume.json</c>, which names are Confluence labels, and the counts the
/// prose spells out as English words.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="QuickstartTests"/> already checks that every command, scaffold target and skill the README
/// names exists. What nothing checked was the sentence that tells a reader how to <em>run</em> one. The
/// contributor section said "Every command except <c>init</c> reads <c>docume.json</c> from the working
/// directory, so point it at a consumer repo with <c>--config &lt;path&gt;</c>" — and
/// <c>docume convert --config x docs/wiki</c> exits 1 with "Unrecognized command or argument", because
/// <c>convert</c> takes a wiki root and reads no config at all. Two of the seven commands take no
/// <c>--config</c>, so the count was wrong and the remedy it prescribed did not exist.
/// </para>
/// <para>
/// A missing command name breaks loudly the first time somebody types it. An option attached to the wrong
/// command is worse: it parses as an unrecognized argument, prints a usage block, and reads like the
/// reader's mistake. So these tests hang each option off the command the README hangs it off, and check a
/// spelled-out count against the set it claims to count — a table can grow a row while the sentence above
/// it still says "thirteen".
/// </para>
/// </remarks>
public sealed partial class ReadmeCliContractTests
{
    /// <summary>Where the contributor guidance about <c>--config</c> lives.</summary>
    private const string ConfigOption = "--config";

    [Fact]
    public void Every_option_the_README_hangs_on_a_command_exists_on_that_command()
    {
        var options = CommandOptions();
        var invocations = ReadmeInvocations();

        // A README that names no `docume <cmd> --opt` at all would pass every assertion below vacuously.
        invocations.ShouldNotBeEmpty(
            "README.md hangs no option off any `docume` command, so this scan is broken rather than clean.");

        var wrong = invocations
            .Where(invocation => !options.TryGetValue(invocation.Command, out var declared)
                || !declared.Contains(invocation.Option, StringComparer.Ordinal))
            .Select(invocation => $"docume {invocation.Command} {invocation.Option}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var known = string.Join(
            "\n",
            options.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"  {entry.Key}: {string.Join(' ', entry.Value.Order(StringComparer.Ordinal))}"));

        var message = "README.md tells the reader to run invocations the CLI does not accept: "
            + $"[{string.Join(", ", wrong)}]. An option that exists on another command still exits 1 with "
            + $"\"Unrecognized command or argument\" here. What each command declares:\n{known}";

        wrong.ShouldBeEmpty(message);
    }

    [Fact]
    public void The_commands_that_take_a_config_path_are_exactly_the_ones_the_README_names()
    {
        var options = CommandOptions();

        var takesConfig = options
            .Where(entry => entry.Value.Contains(ConfigOption, StringComparer.Ordinal))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        var takesNone = options
            .Where(entry => !entry.Value.Contains(ConfigOption, StringComparer.Ordinal))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        var paragraph = Paragraph(ConfigOption);

        // Both halves, because the sentence was wrong in both: it undercounted the exceptions AND
        // prescribed an option the exceptions do not have.
        var unnamed = takesConfig
            .Where(command => !paragraph.Contains($"`{command}`", StringComparison.Ordinal))
            .ToList();

        var unaccounted = takesNone
            .Where(command => !paragraph.Contains($"`{command}`", StringComparison.Ordinal))
            .ToList();

        var namedMessage = $"README.md's `{ConfigOption}` paragraph does not name [{string.Join(", ", unnamed)}], "
            + $"which read docume.json and take {ConfigOption}. Reader points the CLI at the wrong repo.";

        unnamed.ShouldBeEmpty(namedMessage);

        var accountedMessage = $"README.md's `{ConfigOption}` paragraph does not account for "
            + $"[{string.Join(", ", unaccounted)}], which take no {ConfigOption}. Telling the reader to pass "
            + "it to them prints a usage block that reads like their mistake.";

        unaccounted.ShouldBeEmpty(accountedMessage);

        var spelled = SpelledCount(paragraph, "commands read");
        var countMessage = $"README.md says {spelled} commands read docume.json and {takesConfig.Count} "
            + $"actually do ({string.Join(", ", takesConfig)}).";

        spelled.ShouldBe(takesConfig.Count, countMessage);
    }

    [Fact]
    public void The_names_the_README_presents_as_labels_are_labels()
    {
        var labels = new LabelsConfig();

        var real = new HashSet<string>([labels.Approved, labels.Stale], StringComparer.Ordinal);

        var presented = LabelMention()
            .Matches(Readme())
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        presented.ShouldNotBeEmpty("README.md calls nothing a label, so this scan is broken rather than clean.");

        var invented = presented.Where(name => !real.Contains(name)).ToList();

        // `needs-review` is the one that invites this: it sits in the same lifecycle row as a real label
        // and looks exactly like one, but it is an ApprovalStatus in state.json and there is no Confluence
        // label by that name for a reader to go looking for.
        var message = $"README.md presents [{string.Join(", ", invented)}] as Confluence label(s), and "
            + $"LabelsConfig only defines [{string.Join(", ", real.Order(StringComparer.Ordinal))}]. "
            + "An approval state is not a label; say which it is.";

        invented.ShouldBeEmpty(message);
    }

    [Fact]
    public void The_scaffold_count_the_README_spells_out_matches_what_init_writes()
    {
        var written = Scaffold().Count;
        var spelled = SpelledCount(Paragraph("targets, and it is idempotent"), "targets");

        // QuickstartTests already checks the table against the scaffolder row by row. This is the sentence
        // above the table, which a new target grows past without anybody noticing.
        var message = $"README.md says `docume init` writes {spelled} targets and ProjectScaffolder writes "
            + $"{written}. The table is checked row by row elsewhere; this is the count in the prose.";

        spelled.ShouldBe(written, message);
    }

    [Fact]
    public void The_style_guide_topics_the_README_names_are_the_ones_init_scaffolds()
    {
        var styleGuide = ScaffoldedStyleGuide();

        var topics = StyleTopic()
            .Matches(styleGuide)
            .Select(match => match.Groups["topic"].Value)
            .ToList();

        var headings = styleGuide
            .Split('\n')
            .Count(line => line.StartsWith('#'));

        var paragraph = Paragraph("STYLE.md` first");

        var missing = topics
            .Where(topic => !paragraph.Contains(topic, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var topicMessage = $"README.md tells the reader to fill in STYLE.md but does not name "
            + $"[{string.Join(", ", missing)}], which `docume init` scaffolds as the sections the skills read.";

        missing.ShouldBeEmpty(topicMessage);

        var spelled = SpelledCount(paragraph, "bullets");
        var countMessage = $"README.md says STYLE.md has {spelled} bullets and the scaffolded file has "
            + $"{topics.Count} ({string.Join(", ", topics)}).";

        spelled.ShouldBe(topics.Count, countMessage);

        // The shape, not just the count: the scaffolded file is bullets under one heading, and docs-loop's
        // SKILL.md says so in as many words. Calling them "headings" sends the reader looking for four.
        var shapeMessage = $"The scaffolded STYLE.md has {headings} heading(s) and {topics.Count} bullets, so "
            + "README.md must not call those topics headings.";

        headings.ShouldBe(1, shapeMessage);
        paragraph.ShouldNotContain("headings", Case.Insensitive, shapeMessage);
    }

    [Fact]
    public void The_versions_the_prerequisites_pin_are_the_ones_the_tree_uses()
    {
        var prerequisites = Section("### 0. Prerequisites");
        var renderer = File.ReadAllText(Path.Combine(RepoRoot, "templates", "tools", "render-mermaid.mjs"));

        var sdk = System.Text.Json.JsonDocument
            .Parse(File.ReadAllText(Path.Combine(RepoRoot, "global.json")))
            .RootElement.GetProperty("sdk")
            .GetProperty("version")
            .GetString();

        sdk.ShouldNotBeNull("global.json pins no SDK version.");

        prerequisites.ShouldContain(
            sdk,
            customMessage: $"README.md's prerequisites do not pin SDK {sdk}, which global.json requires.");

        // The renderer's own install line is the authority: it is what the error message tells the reader
        // to run when the dependency is missing, so a README pinning a different version teaches a build
        // that fails at render time.
        //
        // Every occurrence, not the first: render-mermaid.mjs names the spec twice (a header comment and
        // the missing-dependency message), and a first-match read passes against the stale copy while the
        // other one moves. Asserting they agree catches the renderer disagreeing with itself too.
        var pinned = Distinct(NpmInstall(), renderer, "spec", "beautiful-mermaid@<version>");
        var pinMessage = $"README.md's prerequisites do not pin `{pinned}`, which render-mermaid.mjs tells the "
            + "reader to install when it cannot resolve the package.";

        prerequisites.ShouldContain(pinned, customMessage: pinMessage);

        var floor = Distinct(NodeFloor(), renderer, "major", "a Node version floor");
        var floorMessage = $"README.md's prerequisites do not ask for Node {floor}, which "
            + "render-mermaid.mjs requires.";

        prerequisites.ShouldContain($"Node {floor}", customMessage: floorMessage);
    }

    [Fact]
    public void The_analyzer_pack_count_the_README_spells_out_matches_the_package_props()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot, "Directory.Packages.props"));

        var packs = AnalyzerPackage()
            .Matches(props)
            .Select(match => match.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        packs.ShouldNotBeEmpty("Directory.Packages.props declares no analyzer pack, so this scan is broken.");

        var spelled = SpelledCount(Paragraph("analyzer packs"), "house analyzer packs");
        var message = $"README.md says {spelled} house analyzer packs run as errors and "
            + $"Directory.Packages.props declares {packs.Count} ({string.Join(", ", packs)}).";

        spelled.ShouldBe(packs.Count, message);
    }

    /// <summary>
    /// Every <c>(command, option)</c> pair the README tells a reader to type, from both places it does:
    /// a single code span or fenced line holding the whole invocation, and a table row whose first cell
    /// is the command and whose later cells list its options separately.
    /// </summary>
    private static List<(string Command, string Option)> ReadmeInvocations()
    {
        var pairs = new List<(string Command, string Option)>();

        foreach (var line in CodeText(Readme()).Split('\n'))
        {
            var command = CommandMention().Match(line);

            if (!command.Success)
            {
                continue;
            }

            pairs.AddRange(OptionMention()
                .Matches(line)
                .Select(option => (command.Groups["name"].Value, option.Groups["option"].Value)));
        }

        foreach (var row in Readme().Split('\n').Where(IsCommandRow))
        {
            var spans = CodeSpan().Matches(row).Select(match => match.Groups["code"].Value).ToList();
            var command = CommandMention().Match(spans[0]);

            if (!command.Success)
            {
                continue;
            }

            pairs.AddRange(spans
                .Skip(1)
                .SelectMany(span => OptionMention().Matches(span).Cast<Match>())
                .Select(option => (command.Groups["name"].Value, option.Groups["option"].Value)));
        }

        return pairs;
    }

    private static bool IsCommandRow(string line) => line.StartsWith("| `docume ", StringComparison.Ordinal);

    /// <summary>Every long option each <c>Commands/&lt;Name&gt;Command.cs</c> declares, keyed by command.</summary>
    private static Dictionary<string, HashSet<string>> CommandOptions()
    {
        var directory = Path.Combine(RepoRoot, "src", "DocuMe.Cli", "Commands");

        var options = Directory
            .EnumerateFiles(directory, "*Command.cs")
            .ToDictionary(
                file => Path.GetFileNameWithoutExtension(file)
                    .Replace("Command", string.Empty, StringComparison.Ordinal)
                    .ToLowerInvariant(),
                file => new HashSet<string>(
                    OptionDeclaration()
                        .Matches(File.ReadAllText(file))
                        .Select(match => match.Groups["option"].Value),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

        options.ShouldNotBeEmpty("No `<Name>Command.cs` found, so the option scan is broken rather than clean.");

        return options;
    }

    /// <summary>
    /// The one value <paramref name="pattern"/> captures across <paramref name="text"/>, failing when the
    /// file names two different ones. A first-match read is how a stale copy keeps a claim looking true.
    /// </summary>
    private static string Distinct(Regex pattern, string text, string group, string what)
    {
        var values = pattern
            .Matches(text)
            .Select(match => match.Groups[group].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var message = $"render-mermaid.mjs names {values.Count} different values for {what} "
            + $"([{string.Join(", ", values)}]), so there is no single pin for README.md to agree with.";

        values.Count.ShouldBe(1, message);

        return values[0];
    }

    /// <summary>The number written as an English word in front of <paramref name="noun"/>.</summary>
    private static int SpelledCount(string text, string noun)
    {
        var index = text.IndexOf(noun, StringComparison.OrdinalIgnoreCase);

        index.ShouldBeGreaterThanOrEqualTo(0, $"The text under test does not mention '{noun}'.");

        var words = text[..index].Split([' ', '\n', '—'], StringSplitOptions.RemoveEmptyEntries);
        var candidate = words.Length > 0 ? words[^1].Trim('`', ',', '(') : string.Empty;

        Numbers.ShouldContainKey(
            candidate.ToLowerInvariant(),
            $"'{candidate}' in front of '{noun}' is not an English number this test knows.");

        return Numbers[candidate.ToLowerInvariant()];
    }

    /// <summary>The blank-line delimited paragraph holding <paramref name="marker"/>, and only one.</summary>
    private static string Paragraph(string marker)
    {
        var paragraphs = Readme()
            .Split("\n\n", StringSplitOptions.None)
            .Where(paragraph => paragraph.Contains(marker, StringComparison.Ordinal))
            .ToList();

        // Exactly one: a whole-file search passes while the claim under test is deleted, as long as the
        // same words appear anywhere else in the README.
        var message = $"README.md holds {paragraphs.Count} paragraphs containing '{marker}', and the "
            + "assertions below need exactly one to scope to.";

        paragraphs.Count.ShouldBe(1, message);

        return paragraphs[0];
    }

    /// <summary>The text under <paramref name="heading"/>, to the next heading at the same level or shallower.</summary>
    private static string Section(string heading)
    {
        var readme = Readme();
        var start = readme.IndexOf(heading, StringComparison.Ordinal);

        start.ShouldBeGreaterThanOrEqualTo(0, $"README.md has no '{heading}' section.");

        var level = heading.TakeWhile(character => character == '#').Count();
        var lines = readme[start..].Split('\n');

        var end = Array.FindIndex(
            lines,
            1,
            line => line.StartsWith('#') && line.TakeWhile(character => character == '#').Count() <= level);

        return string.Join('\n', end < 0 ? lines : lines[..end]);
    }

    /// <summary>The fenced blocks and inline code spans of a markdown document, joined by newlines.</summary>
    private static string CodeText(string markdown)
    {
        var collected = new List<string>();
        var fenced = false;

        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;

                continue;
            }

            if (fenced)
            {
                collected.Add(line);

                continue;
            }

            collected.AddRange(CodeSpan().Matches(line).Select(match => match.Groups["code"].Value));
        }

        return string.Join('\n', collected);
    }

    /// <summary>What <c>docume init</c> writes into an empty directory.</summary>
    private static List<string> Scaffold()
        => ProjectScaffolder
            .Scaffold(Directory.CreateTempSubdirectory("docume-readme-contract").FullName, "DOCS", BaseUrl)
            .Select(result => result.RelativePath)
            .ToList();

    /// <summary>The <c>_meta/STYLE.md</c> body <c>docume init</c> scaffolds.</summary>
    private static string ScaffoldedStyleGuide()
    {
        var directory = Directory.CreateTempSubdirectory("docume-readme-style").FullName;

        var style = ProjectScaffolder
            .Scaffold(directory, "DOCS", BaseUrl)
            .Select(result => result.RelativePath)
            .Single(path => path.EndsWith("STYLE.md", StringComparison.Ordinal));

        return File.ReadAllText(Path.Combine(directory, style));
    }

    private const string BaseUrl = "https://example.atlassian.net/wiki";

    private static readonly Dictionary<string, int> Numbers = new(StringComparer.Ordinal)
    {
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["thirteen"] = 13,
    };

    private static string RepoRoot { get; } = Locate();

    private static string Readme() => File.ReadAllText(Path.Combine(RepoRoot, "README.md"));

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "README.md")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("Walked to the filesystem root without finding README.md.");

        return directory.FullName;
    }

    [GeneratedRegex(@"\bdocume (?<name>[a-z][a-z-]*)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommandMention();

    [GeneratedRegex(@"(?<option>--[a-z][a-z-]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionMention();

    [GeneratedRegex(
        @"new Option<[^>]*>\(""(?<option>--[a-z][a-z-]+)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionDeclaration();

    [GeneratedRegex(@"`(?<code>[^`\n]+)`", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"`(?<name>[a-z][a-z-]*)` label", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LabelMention();

    [GeneratedRegex(@"^- \*\*(?<topic>[A-Za-z]+):\*\*", RegexOptions.ExplicitCapture | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StyleTopic();

    [GeneratedRegex(
        @"(?<spec>beautiful-mermaid@[0-9][0-9.]*)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex NpmInstall();

    [GeneratedRegex(@"Node >= (?<major>[0-9]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NodeFloor();

    [GeneratedRegex(
        @"PackageVersion Include=""(?<id>[A-Za-z0-9.]*Analyzers?(?:\.[A-Za-z]+)?)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnalyzerPackage();
}
