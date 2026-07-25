using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace DocuMe.Core.Tests.Cli;

/// <summary>
/// The `docume` CLI run the way a user runs it: a process, with arguments, read back through its exit
/// code and its two streams. Everything else in this suite calls DocuMe.Core directly, so
/// <c>src/DocuMe.Cli</c> — the argument wiring, the exit codes, the printed prose — had no test at all
/// until this class; the first run of it found the root command naming itself after the package id.
/// </summary>
/// <remarks>
/// Only the commands that need no network are driven here; the Confluence-facing three are
/// <see cref="CliConfluenceTests"/>, which points the same process at a local server.
/// </remarks>
public sealed partial class CliExecutionTests : IDisposable
{
    // A port nothing listens on, so a command that reaches for Confluence when it should not fails in
    // milliseconds against the loopback rather than sending anything anywhere.
    private const string BaseUrl = "http://127.0.0.1:1/wiki";
    private const string SpaceKey = "SBX";

    private readonly string _root = Directory.CreateTempSubdirectory("docume-cli-tests").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_usage_line_names_the_command_the_tool_installs_as()
    {
        var run = Invoke(_root, "--help");

        run.Code.ShouldBe(0, run.Diagnostics);

        // System.CommandLine names a RootCommand after the entry assembly, which made every usage line
        // and every parse error read "DocuMe.Cli" — the package id, not something anyone can type.
        var because = $"`docume --help` tells the reader to run something other than "
            + $"<ToolCommandName> from DocuMe.Cli.csproj.{Environment.NewLine}{run.Diagnostics}";

        UsageLine(run).ShouldBe($"{ToolCommandName} [command] [options]", because);
    }

    [Fact]
    public void Every_subcommand_usage_line_names_the_installed_command()
    {
        foreach (var command in ShippedCommands())
        {
            var run = Invoke(_root, command, "--help");

            run.Code.ShouldBe(0, run.Diagnostics);

            var because = $"`docume {command} --help` names the wrong executable.{Environment.NewLine}"
                + run.Diagnostics;

            UsageLine(run).ShouldStartWith($"{ToolCommandName} {command} ", customMessage: because);
        }
    }

    /// <summary>
    /// The drift guard for the loop above: without it, a command dropped from the root would silently
    /// shrink the set every other test in this class iterates over.
    /// </summary>
    [Fact]
    public void The_help_lists_every_command_Program_registers()
    {
        var registered = RegisteredCommands();

        registered.ShouldNotBeEmpty("No `<Name>Command.Build()` found in Program.cs, so the scan is broken.");

        const string because = "The commands the root help lists are not the ones Program.cs hangs off the "
            + "root — a command was registered without reaching help, or the reverse.";

        ShippedCommands().ShouldBe(registered, ignoreOrder: true, customMessage: because);
    }

    [Fact]
    public void A_bare_invocation_prints_the_help_it_would_print_for_help()
    {
        var bare = Invoke(_root);
        var asked = Invoke(_root, "--help");

        bare.Code.ShouldBe(0, bare.Diagnostics);

        // `docume` alone is what a reader types first. Silence, or a usage error, is the worst possible
        // answer to it, so Program.cs rewrites no args to --help; this is that rewrite.
        bare.Output.ShouldBe(asked.Output, "A bare `docume` no longer prints the help `--help` prints.");
    }

    [Fact]
    public void An_unrecognized_option_fails_and_says_which_one_on_stderr()
    {
        var run = Invoke(_root, "--nope");

        run.Code.ShouldBe(1, run.Diagnostics);

        // On stderr, not stdout: a CI step that pipes stdout to a report must still surface the error.
        run.Error.Contains("'--nope'", StringComparison.Ordinal)
            .ShouldBeTrue($"The parse error does not name the offending token.{Environment.NewLine}{run.Diagnostics}");
    }

    [Fact]
    public void The_version_it_reports_is_the_version_the_solution_carries()
    {
        var run = Invoke(_root, "--version");

        run.Code.ShouldBe(0, run.Diagnostics);

        // README.md's quickstart runs `docume --version` as its check that the install worked, so this
        // is the first output of the tool anyone sees. §12 releases everything off one number.
        var because = $"`docume --version` disagrees with <Version> in Directory.Build.props "
            + $"({SolutionVersion}).{Environment.NewLine}{run.Diagnostics}";

        run.Output.Trim().ShouldStartWith(SolutionVersion, customMessage: because);
    }

    [Fact]
    public void Init_scaffolds_the_tree_and_a_second_run_writes_nothing_new()
    {
        var work = Scratch(nameof(Init_scaffolds_the_tree_and_a_second_run_writes_nothing_new));

        var first = Invoke(work, "init", "--space", SpaceKey, "--base-url", BaseUrl);

        first.Code.ShouldBe(0, first.Diagnostics);
        File.Exists(Path.Combine(work, "docume.json")).ShouldBeTrue(first.Diagnostics);

        Snapshot(work).ShouldNotBeEmpty("`docume init` wrote no files at all.");

        // Edited between the runs, because §9.4's promise is not "init writes the same bytes twice" —
        // it is that a file the repo already has survives. A snapshot of untouched output would pass
        // against a scaffolder that rewrites everything it finds.
        File.AppendAllText(
            Path.Combine(work, "docs", "wiki", "README.md"),
            $"{Environment.NewLine}Written by the consumer, after init.{Environment.NewLine}");

        var edited = Snapshot(work);
        var second = Invoke(work, "init", "--space", SpaceKey, "--base-url", BaseUrl);

        second.Code.ShouldBe(0, second.Diagnostics);

        const string because = "A second `docume init` changed files on disk. Rule §9.4: it never "
            + "overwrites what is already there, it reports the skip.";

        Snapshot(work).ShouldBe(edited, because);
    }

    [Fact]
    public void Init_refuses_a_legacy_map_without_adopt()
    {
        var work = Scratch(nameof(Init_refuses_a_legacy_map_without_adopt));

        var run = Invoke(work, "init", "--legacy-map", "map.json");

        run.Code.ShouldBe(1, run.Diagnostics);

        // Refused before anything is written, rather than scaffolded and then complained about: a run
        // that half-happened is worse to recover from than one that did not start.
        Directory.EnumerateFileSystemEntries(work)
            .ShouldBeEmpty($"The refused run still scaffolded into the directory.{Environment.NewLine}{run.Diagnostics}");

        run.Flowed.Contains("--adopt", StringComparison.Ordinal)
            .ShouldBeTrue($"The refusal does not say what the map needs.{Environment.NewLine}{run.Diagnostics}");
    }

    [Fact]
    public void Init_adopt_with_no_wiki_to_adopt_exits_nonzero()
    {
        var work = Scratch(nameof(Init_adopt_with_no_wiki_to_adopt_exits_nonzero));

        var run = Invoke(work, "init", "--adopt", "--space", SpaceKey, "--base-url", BaseUrl);

        // Everything else init does still happened, so the files are there; the adoption is what did
        // not, and a consumer who asked for it must not read exit 0 as "done".
        run.Code.ShouldBe(1, run.Diagnostics);
        File.Exists(Path.Combine(work, "docume.json")).ShouldBeTrue(run.Diagnostics);

        run.Flowed.Contains("no page entries", StringComparison.Ordinal)
            .ShouldBeTrue($"The failure does not say the adoption wrote nothing.{Environment.NewLine}{run.Diagnostics}");
    }

    [Fact]
    public void Convert_accepts_the_wiki_init_just_scaffolded()
    {
        var work = Scaffolded(nameof(Convert_accepts_the_wiki_init_just_scaffolded));

        var run = Invoke(work, "convert", Path.Combine("docs", "wiki"));

        run.Code.ShouldBe(0, run.Diagnostics);

        // The scaffolded README is the first page every consumer converts. If the tool cannot convert
        // its own template, the install story fails on the first command after init.
        run.Flowed.Contains("failed: 0", StringComparison.Ordinal)
            .ShouldBeTrue($"The scaffolded wiki does not convert cleanly.{Environment.NewLine}{run.Diagnostics}");
    }

    [Fact]
    public void Convert_reports_a_missing_wiki_root_and_exits_nonzero()
    {
        var work = Scaffolded(nameof(Convert_reports_a_missing_wiki_root_and_exits_nonzero));

        var run = Invoke(work, "convert", "not-a-directory");

        run.Code.ShouldBe(1, run.Diagnostics);

        run.Flowed.Contains("not found", StringComparison.Ordinal)
            .ShouldBeTrue($"A missing wiki root is not reported as missing.{Environment.NewLine}{run.Diagnostics}");
    }

    [Fact]
    public void Drift_without_a_baseline_exits_nonzero_and_says_how_to_set_one()
    {
        var work = Scaffolded(nameof(Drift_without_a_baseline_exits_nonzero_and_says_how_to_set_one));

        var run = Invoke(work, "drift");

        // No CLI command writes baselineSha — the generation pass stamps it — so a fresh repo lands here
        // often. The scaffolded workflows guard the empty case before calling, which leaves this path to
        // the person at a terminal: it has to name the two ways out.
        run.Code.ShouldBe(1, run.Diagnostics);

        run.Flowed.Contains("--baseline", StringComparison.Ordinal)
            .ShouldBeTrue($"The message does not name the flag that fixes it.{Environment.NewLine}{run.Diagnostics}");

        run.Flowed.Contains("baselineSha", StringComparison.Ordinal)
            .ShouldBeTrue($"The message does not name the state field that fixes it.{Environment.NewLine}{run.Diagnostics}");
    }

    [Fact]
    public void Status_offline_reports_without_asking_confluence_anything()
    {
        var work = Scaffolded(nameof(Status_offline_reports_without_asking_confluence_anything));

        var run = Invoke(work, "status", "--offline", "--json");

        run.Code.ShouldBe(0, run.Diagnostics);

        using var report = JsonDocument.Parse(run.Output);

        var confluence = report.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Single(check => string.Equals(check.GetProperty("name").GetString(), "confluence", StringComparison.Ordinal));

        // --offline is what docs-refresh.yml relies on to stay free of a token. "not-checked", not "ok":
        // a check that was skipped must never read as a check that passed.
        confluence.GetProperty("outcome").GetString().ShouldBe("not-checked", run.Diagnostics);

        // The detail, not just the outcome: the probe is also skipped when no credentials are in the
        // environment, so an outcome-only assertion would pass on a build that ignored --offline
        // entirely. This is the reason it gives, and it has to be the flag.
        var detail = confluence.GetProperty("detail").GetString() ?? string.Empty;

        detail.Contains("--offline", StringComparison.Ordinal)
            .ShouldBeTrue($"The probe was skipped for some other reason than --offline.{Environment.NewLine}{detail}");
    }

    [Fact]
    public void Status_fails_on_drift_only_when_asked_to()
    {
        var work = Scaffolded(nameof(Status_fails_on_drift_only_when_asked_to));

        var advisory = Invoke(work, "status", "--offline");
        var gated = Invoke(work, "status", "--offline", "--fail-on-drift");

        // The scaffolded page has never been published, which is drift. Without the flag status is
        // advisory and a CI step that only wanted the report stays green.
        advisory.Code.ShouldBe(0, advisory.Diagnostics);
        gated.Code.ShouldNotBe(0, gated.Diagnostics);
    }

    /// <summary>
    /// The rot guard. A "not built yet" note outlives the milestone that built the thing, and nothing
    /// re-reads printed prose — which is how `status` and the §6.5 dashboard both spent M4 through M6
    /// telling readers that `docume sync --comments` did not exist while it was shipping in the help
    /// two commands away.
    /// </summary>
    [Fact]
    public void No_status_gap_says_a_command_the_cli_ships_is_unbuilt()
    {
        var work = Scaffolded(nameof(No_status_gap_says_a_command_the_cli_ships_is_unbuilt));

        var run = Invoke(work, "status", "--offline", "--json");

        run.Code.ShouldBe(0, run.Diagnostics);

        var shipped = ShippedCommands();
        var gaps = Gaps(run);

        gaps.ShouldNotBeEmpty("`status` reported no gaps at all, so this guard is checking nothing.");

        foreach (var gap in gaps)
        {
            var named = CommandMention()
                .Matches(gap)
                .Select(match => match.Groups["name"].Value)
                .Where(shipped.Contains)
                .ToList();

            if (named.Count == 0)
            {
                continue;
            }

            var because = $"A `status` gap says `docume {string.Join("`, `docume ", named)}` is not built, "
                + $"but the CLI ships it. Say what the report cannot compute, not that the command is "
                + $"missing.{Environment.NewLine}{gap}";

            gap.Contains("not built", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(because);
        }
    }

    /// <summary>
    /// Every `docume &lt;command&gt;` a gap note points a reader at has to be one they can run: these
    /// strings are the only place the report tells someone what to do next.
    /// </summary>
    [Fact]
    public void Every_command_a_status_gap_names_is_one_the_cli_ships()
    {
        var work = Scaffolded(nameof(Every_command_a_status_gap_names_is_one_the_cli_ships));

        var run = Invoke(work, "status", "--offline", "--json");

        run.Code.ShouldBe(0, run.Diagnostics);

        var shipped = ShippedCommands();

        var unknown = Gaps(run)
            .SelectMany(gap => CommandMention().Matches(gap).Select(match => match.Groups["name"].Value))
            .Where(name => !shipped.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var because = $"`status` sends the reader to commands that do not exist: "
            + $"[{string.Join(", ", unknown)}].";

        unknown.ShouldBeEmpty(because);
    }

    private static List<string> Gaps(CliRun run)
    {
        using var report = JsonDocument.Parse(run.Output);

        return report.RootElement.GetProperty("notYetAvailable")
            .EnumerateArray()
            .Select(gap => gap.GetString() ?? string.Empty)
            .ToList();
    }

    /// <summary>The line under "Usage:", which is where the executable name shows up.</summary>
    private static string UsageLine(CliRun run)
    {
        var lines = run.Output.Split('\n').Select(line => line.Trim()).ToList();
        var header = lines.FindIndex(line => string.Equals(line, "Usage:", StringComparison.Ordinal));

        header.ShouldBeGreaterThanOrEqualTo(0, $"No \"Usage:\" section in the help.{Environment.NewLine}{run.Diagnostics}");

        var usage = lines.Skip(header + 1).FirstOrDefault(line => line.Length > 0);

        usage.ShouldNotBeNull($"Nothing follows \"Usage:\".{Environment.NewLine}{run.Diagnostics}");

        return usage;
    }

    /// <summary>The subcommands the root help offers, which is the set a reader can actually reach.</summary>
    private HashSet<string> ShippedCommands()
    {
        var run = Invoke(_root, "--help");
        var lines = run.Output.Split('\n').ToList();
        var header = lines.FindIndex(line => line.StartsWith("Commands:", StringComparison.Ordinal));

        header.ShouldBeGreaterThanOrEqualTo(0, $"No \"Commands:\" section in the help.{Environment.NewLine}{run.Diagnostics}");

        var names = lines.Skip(header + 1)
            .TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal))
            .Select(line => line.Trim().Split(' ', 2)[0])
            .Where(name => name.Length > 0);

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>The subcommands <c>Program.cs</c> hangs off the root, lowercased.</summary>
    private static HashSet<string> RegisteredCommands()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src", "DocuMe.Cli", "Program.cs"));

        var names = CommandRegistration()
            .Matches(program)
            .Select(match => match.Groups["name"].Value.ToLowerInvariant());

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>Every file under <paramref name="directory"/> as path-to-content-hash, for comparison.</summary>
    private static SortedDictionary<string, string> Snapshot(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var hash = SHA256.HashData(File.ReadAllBytes(file));
            snapshot[Path.GetRelativePath(directory, file)] = Convert.ToHexString(hash);
        }

        return snapshot;
    }

    private string Scratch(string name)
    {
        var work = Path.Combine(_root, name);
        Directory.CreateDirectory(work);

        return work;
    }

    private string Scaffolded(string name)
    {
        var work = Scratch(name);
        var run = Invoke(work, "init", "--space", SpaceKey, "--base-url", BaseUrl);

        run.Code.ShouldBe(0, $"The fixture's own `docume init` failed.{Environment.NewLine}{run.Diagnostics}");

        return work;
    }

    private static CliRun Invoke(string workingDirectory, params string[] args) =>
        DocumeCli.Invoke(workingDirectory, args);

    private static string RepoRoot => DocumeCli.RepoRoot;

    private static string SolutionVersion => DocumeCli.SolutionVersion;

    private static string ToolCommandName => DocumeCli.ToolCommandName;

    [GeneratedRegex(@"(?<name>\w+)Command\.Build\(\)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommandRegistration();

    [GeneratedRegex(@"`docume (?<name>[a-z][a-z-]*)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommandMention();
}
