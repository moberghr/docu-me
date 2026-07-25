using System.Text.RegularExpressions;
using Shouldly;

namespace DocuMe.Core.Tests.Cli;

/// <summary>
/// Pins the prose that tells someone to run <c>docume</c> to the command surface the CLI actually
/// has: the reference page's option tables in both directions, and every <c>docume …</c> invocation
/// in a consumer-facing file.
/// </summary>
/// <remarks>
/// <para>
/// Written after the pattern that had already shipped two consumer-facing bugs: <b>when a capability
/// has two descriptions, fixing one does not fix the other, and nothing cross-checks them.</b> The
/// CLI declares its options in <c>src/DocuMe.Cli/Commands/</c>; <c>docs/wiki/20-reference/cli.md</c>
/// describes them again for a reader, and it is published to Confluence as DocuMe's own wiki. Its
/// first run found <c>--allow-protected-space</c> missing from both the <c>drift</c> and the
/// <c>dashboard</c> table — the one-run escape from the write lock that CLAUDE.md §0.1 and rule §1.4
/// rest on, undocumented on exactly the two write paths a reader would need it for.
/// </para>
/// <para>
/// The surface of record is the CLI's own <c>--help</c>, read from the process (the suite never
/// references the CLI assembly — see <see cref="DocumeCli"/>). Only the <b>option column</b> of the
/// <c>Options:</c> block counts: option names also appear in description PROSE
/// (<c>--changed-since</c> cites <c>git diff --name-only</c>, <c>--state</c> cites
/// <c>docume sync --labels</c>), and a scan of the whole help text reports both as declared options
/// that do not exist.
/// </para>
/// </remarks>
public sealed partial class CliReferencePageTests
{
    private static readonly string[] ReferencePagePath = ["docs", "wiki", "20-reference", "cli.md"];

    /// <summary>
    /// Documented once in page-wide prose rather than in each command's table: the two
    /// <c>--config</c>/<c>--state</c> paragraphs cover every command that takes them, and the root
    /// adds <c>--help</c>/<c>--version</c>. Exempt from the per-table requirement, nothing else.
    /// </summary>
    private static readonly HashSet<string> PageWideOptions =
        new(["--config", "--state", "--help", "--version"], StringComparer.Ordinal);

    /// <summary>
    /// Files that TELL SOMEONE to run the tool, so a stale flag in one costs a consumer a failed run.
    /// Deliberately not the whole repo: PLAN.md, GATES.md, CHANGELOG.md, tasks/, docs/plans/,
    /// docs/specs/ and tools/loop/state.json NARRATE the build, and scanning them buries the real
    /// gaps under sentences that happen to contain the word "docume".
    /// </summary>
    private static readonly string[] InstructionRoots =
    [
        "README.md",
        "docs/wiki",
        "plugin",
        "templates",
        "actions",
        ".github/workflows",
        "schema",
    ];

    [Fact]
    public void The_reference_page_has_a_section_for_every_command_and_no_others()
    {
        var documented = PageSections().Keys.ToHashSet(StringComparer.Ordinal);
        const string because = "docs/wiki/20-reference/cli.md documents a different set of commands "
            + "than `docume --help` offers. A reader of DocuMe's own wiki gets a command that does "
            + "not exist, or never learns about one that does.";

        documented.ShouldBe(ShippedCommands(), ignoreOrder: true, customMessage: because);
    }

    [Fact]
    public void Every_declared_option_is_in_its_commands_option_table()
    {
        var sections = PageSections();
        var missing = new List<string>();

        foreach (var command in ShippedCommands().OrderBy(name => name, StringComparer.Ordinal))
        {
            var tabled = TabledOptions(sections[command]);

            var undocumented = DeclaredOptions(command)
                .Where(option => !PageWideOptions.Contains(option))
                .Where(option => !tabled.Contains(option))
                .OrderBy(option => option, StringComparer.Ordinal);

            missing.AddRange(undocumented.Select(option => $"{command} {option}"));
        }

        var because = "These options are declared by the CLI and absent from their command's option "
            + $"table in docs/wiki/20-reference/cli.md: {string.Join(", ", missing)}. That page is "
            + "published to Confluence as DocuMe's own reference, so a reader cannot discover them.";

        missing.ShouldBeEmpty(because);
    }

    [Fact]
    public void Every_option_the_reference_page_documents_is_one_the_command_declares()
    {
        var sections = PageSections();
        var phantom = new List<string>();

        foreach (var command in ShippedCommands().OrderBy(name => name, StringComparer.Ordinal))
        {
            var declared = DeclaredOptions(command);

            var invented = TabledOptions(sections[command])
                .Where(option => !declared.Contains(option))
                .OrderBy(option => option, StringComparer.Ordinal);

            phantom.AddRange(invented.Select(option => $"{command} {option}"));
        }

        var because = "The reference page documents options the CLI does not have: "
            + $"{string.Join(", ", phantom)}. Either the page is stale or the option was renamed "
            + "without it; a reader following the page gets a parse error.";

        phantom.ShouldBeEmpty(because);
    }

    [Fact]
    public void Every_documented_invocation_names_a_real_command_with_real_options()
    {
        var shipped = ShippedCommands();
        var declared = shipped.ToDictionary(
            command => command,
            DeclaredOptions,
            StringComparer.Ordinal);

        var gaps = new List<string>();

        foreach (var (path, invocations) in DocumentedInvocations())
        {
            foreach (var (line, invocation) in invocations)
            {
                var tokens = invocation.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var command = tokens[0];

                if (!shipped.Contains(command))
                {
                    gaps.Add($"{path}:{line} — no `{command}` command (`docume {invocation}`)");
                    continue;
                }

                var unknown = tokens.Skip(1)
                    .Where(token => token.StartsWith("--", StringComparison.Ordinal))
                    .Select(token => token.Split('=')[0])
                    .Where(option => !IsPlaceholder(option))
                    .Where(option => !declared[command].Contains(option));

                gaps.AddRange(unknown.Select(option =>
                    $"{path}:{line} — `{command}` has no `{option}` (`docume {invocation}`)"));
            }
        }

        var because = "These files tell a reader or an agent to run something the CLI cannot parse:"
            + Environment.NewLine + string.Join(Environment.NewLine, gaps);

        gaps.ShouldBeEmpty(because);
    }

    /// <summary>
    /// The sweep above passes trivially if the scan matches nothing, and its regex has to survive
    /// escaped backticks, line continuations and yaml block scalars. This fails when it goes blind.
    /// </summary>
    [Fact]
    public void The_invocation_scan_reaches_the_files_that_carry_the_instructions()
    {
        var found = DocumentedInvocations();
        var scanned = found.Keys.ToHashSet(StringComparer.Ordinal);

        // Each of these tells someone to run the tool, in a different syntax the scan has to survive:
        // markdown fences, a SKILL.md's prose, and a yaml `run:` block scalar.
        string[] mustReach =
        [
            "README.md",
            "docs/wiki/20-reference/cli.md",
            "plugin/skills/docs-loop/SKILL.md",
            "templates/workflows/docs-publish.yml",
        ];

        foreach (var path in mustReach)
        {
            var blind = $"The invocation scan found no `docume …` in {path}, which is full of them. "
                + "Its regex has gone blind, and the sweep that depends on it is now passing on an "
                + "empty set.";

            scanned.ShouldContain(path, blind);
        }

        var total = found.Values.Sum(list => list.Count);
        var collapsed = $"The invocation scan found only {total} invocations across the repo's "
            + "consumer-facing files. It found 132 when this test was written; a collapse that large "
            + "means the scan broke, not that the docs shrank.";

        total.ShouldBeGreaterThan(100, collapsed);
    }

    /// <summary>An <c>ALL-CAPS</c> or bracketed stand-in in prose claims nothing about the surface.</summary>
    private static bool IsPlaceholder(string option) =>
        option.Length > 2 && (char.IsUpper(option[2]) || option[2] is '<' or '{' or '$');

    /// <summary>The subcommands the root help offers, which is the set a reader can actually reach.</summary>
    private static HashSet<string> ShippedCommands()
    {
        var run = Help();
        var block = Section(run, "Commands:");

        var names = block
            .Select(line => line.Trim().Split(' ', 2)[0])
            .Where(name => name.Length > 0);

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>
    /// The options one command declares, read out of the option column of its own <c>--help</c>.
    /// Alias lists (<c>-?, -h, --help</c>) contribute only their long forms; the page tables and every
    /// documented invocation use those.
    /// </summary>
    private static HashSet<string> DeclaredOptions(string command)
    {
        var options = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in Section(Help(command), "Options:"))
        {
            // Two-or-more spaces separate the option column from its description. A wrapped
            // description line contributes nothing: none of its words start with "--".
            var column = OptionColumn().Match(line);

            if (!column.Success)
            {
                continue;
            }

            var aliases = column.Groups["aliases"].Value.Split(',');

            foreach (var alias in aliases)
            {
                var name = alias.Trim().Split(' ')[0];

                if (name.StartsWith("--", StringComparison.Ordinal))
                {
                    options.Add(name);
                }
            }
        }

        options.ShouldNotBeEmpty($"Parsed no options at all out of `docume {command} --help`.");

        return options;
    }

    /// <summary>
    /// The reference page split by its <c>## `docume &lt;command&gt;`</c> headings, so an option found
    /// in one command's table cannot count as documenting another's.
    /// </summary>
    private static Dictionary<string, string> PageSections()
    {
        var page = File.ReadAllText(Path.Combine([RepoRoot, .. ReferencePagePath]));
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var match in CommandSection().Matches(page).Cast<Match>())
        {
            sections[match.Groups["name"].Value] = match.Groups["body"].Value;
        }

        const string because = "Parsed no \"## `docume <command>`\" headings out of "
            + "docs/wiki/20-reference/cli.md, so every assertion against it would pass on nothing.";

        sections.ShouldNotBeEmpty(because);

        return sections;
    }

    /// <summary>The options named in the leading cell of a markdown table row inside one section.</summary>
    private static HashSet<string> TabledOptions(string section)
    {
        var names = TableRowOption().Matches(section)
            .Cast<Match>()
            .Select(match => match.Groups["name"].Value);

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every <c>docume …</c> invocation in a consumer-facing file, as repo-relative path to
    /// (line, argument string).
    /// </summary>
    private static Dictionary<string, List<(int Line, string Invocation)>> DocumentedInvocations()
    {
        var found = new Dictionary<string, List<(int, string)>>(StringComparer.Ordinal);

        foreach (var file in InstructionFiles())
        {
            var relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');

            foreach (var (line, text) in LogicalLines(File.ReadAllLines(file)))
            {
                foreach (var match in Invocation().Matches(text).Cast<Match>())
                {
                    var invocation = Normalize(match.Groups["args"].Value);

                    // "docume <command>" and a bare "docume --help" claim nothing about the surface.
                    if (invocation.Length == 0
                        || invocation[0] is '<' or '{' or '[' or '$'
                        || invocation.StartsWith("--", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!found.TryGetValue(relative, out var list))
                    {
                        list = [];
                        found[relative] = list;
                    }

                    list.Add((line, invocation));
                }
            }
        }

        return found;
    }

    /// <summary>Trailing shell comment dropped, and markdown/prose punctuation off the last token.</summary>
    private static string Normalize(string arguments)
    {
        var text = arguments.Trim();
        var comment = text.IndexOf('#', StringComparison.Ordinal);

        if (comment >= 0)
        {
            text = text[..comment];
        }

        return WhitespaceRun().Replace(text, " ").Trim().TrimEnd(']', '.', ',', ':', ')');
    }

    /// <summary>
    /// Physical lines folded on the shell's <c>\</c> continuation, since one <c>docume</c> call in a
    /// workflow's <c>run:</c> block is spread over several of them. The line number reported is the
    /// first physical line, which is where a reader has to look.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> LogicalLines(string[] lines)
    {
        var index = 0;

        while (index < lines.Length)
        {
            var text = lines[index];
            var start = index + 1;

            while (text.EndsWith('\\') && index + 1 < lines.Length)
            {
                index++;
                text = string.Concat(text.AsSpan(0, text.Length - 1), " ", lines[index].Trim());
            }

            index++;

            yield return (start, text);
        }
    }

    private static IEnumerable<string> InstructionFiles()
    {
        foreach (var root in InstructionRoots)
        {
            var path = Path.Combine(RepoRoot, root.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(path))
            {
                yield return path;
                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(file => Instructional().IsMatch(Path.GetExtension(file)))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    /// <summary>The indented lines under a named help header, up to the first that is not one.</summary>
    private static List<string> Section(CliRun run, string header)
    {
        var lines = run.Output.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var index = lines.FindIndex(line => line.StartsWith(header, StringComparison.Ordinal));

        index.ShouldBeGreaterThanOrEqualTo(
            0,
            $"No \"{header}\" section in the help.{Environment.NewLine}{run.Diagnostics}");

        return lines.Skip(index + 1)
            .TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToList();
    }

    private static CliRun Help(params string[] command)
    {
        var run = DocumeCli.Invoke(RepoRoot, [.. command, "--help"]);

        run.Code.ShouldBe(0, run.Diagnostics);

        return run;
    }

    private static string RepoRoot => DocumeCli.RepoRoot;

    [GeneratedRegex(@"^ {2}(?<aliases>\S[^\n]*?)(?: {2,}|$)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionColumn();

    [GeneratedRegex(@"\n## `docume (?<name>[a-z-]+)[^`]*`\n(?<body>[\s\S]*?)(?=\n## |$)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CommandSection();

    [GeneratedRegex(@"^\|\s*`(?<name>--[a-z0-9-]+)", RegexOptions.ExplicitCapture | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TableRowOption();

    // Stops at any shell separator, a closing quote or paren, an html tag, or a backslash: the
    // workflows cite the tool inside double-quoted `echo` strings as \`docume init\`, and that
    // escaped backtick ends the citation rather than continuing the command.
    [GeneratedRegex(@"(?:^|[\s`(""'>*-])docume (?<args>[^\n`|;&)""'<\\]*)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Invocation();

    [GeneratedRegex(@"^\.(md|ya?ml|json)$", RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Instructional();

    [GeneratedRegex(@"\s+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceRun();
}
