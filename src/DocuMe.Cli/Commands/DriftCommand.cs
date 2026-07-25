using System.CommandLine;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Drift;
using DocuMe.Core.Git;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume drift</c> — PLAN.md §6.4. Diffs two revisions, matches the changed files against every
/// page's <c>sources</c> globs, and reports which pages may no longer describe the code.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It writes nothing and reads nothing from Confluence.</strong> The whole answer is a
/// <c>git diff</c> plus a glob match, which is why this is the one §6.4 slice that needs no credentials,
/// no space and no network. <c>--mark</c> — the flag that adds the <c>stale</c> label — is the next
/// slice and is deliberately not accepted yet rather than accepted and ignored.
/// </para>
/// <para>
/// <strong>Advisory: exit 0 even when pages drifted</strong>, unless <c>--fail-on-drift</c>. A team that
/// wants a blocking check opts in; a team that does not gets a comment on the PR and keeps merging. A
/// broken <em>run</em> is a different thing and always exits 1 — a bad baseline must not read as "no
/// drift".
/// </para>
/// </remarks>
internal static class DriftCommand
{
    /// <summary>Where <c>docume init</c> scaffolds the state file, relative to the wiki root (§5.3).</summary>
    private const string DefaultStateFile = "_meta/state.json";

    private const string TableFormat = "table";
    private const string JsonFormat = "json";
    private const string CommentFormat = "github-comment";

    private static readonly string[] Formats = [TableFormat, JsonFormat, CommentFormat];

    public static Command Build()
    {
        var configOption = new Option<string>("--config")
        {
            Description = "Path to docume.json. Its directory is the repo root wiki.root and every "
                + "'sources' glob resolve against.",
            DefaultValueFactory = _ => ConfigLoader.DefaultFileName,
        };
        var stateOption = new Option<string>("--state")
        {
            Description = $"Path to state.json. Defaults to <wiki.root>/{DefaultStateFile}; read only "
                + "for its baselineSha, and never written.",
        };
        var baselineOption = new Option<string>("--baseline")
        {
            Description = "Revision to diff from. Defaults to state.baselineSha — the commit the wiki "
                + "content was last generated against.",
        };
        var headOption = new Option<string>("--head")
        {
            Description = "Revision to diff to. Defaults to HEAD.",
            DefaultValueFactory = _ => "HEAD",
        };
        var formatOption = new Option<string>("--format")
        {
            Description = $"Output shape: {string.Join(" | ", Formats)}. 'github-comment' is a markdown "
                + "block for a PR comment; 'json' prints nothing else.",
            DefaultValueFactory = _ => TableFormat,
        };
        var failOnDriftOption = new Option<bool>("--fail-on-drift")
        {
            Description = "Exit 1 when any page is affected. Without it the command is advisory and "
                + "always exits 0.",
        };

        const string description =
            "Report which wiki pages derive from code changed between two revisions. Writes nothing.";

        var command = new Command("drift", description)
        {
            configOption,
            stateOption,
            baselineOption,
            headOption,
            formatOption,
            failOnDriftOption,
        };

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetValue(configOption)!,
            parseResult.GetValue(stateOption),
            parseResult.GetValue(baselineOption),
            parseResult.GetValue(headOption)!,
            parseResult.GetValue(formatOption)!,
            parseResult.GetValue(failOnDriftOption),
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string configPath,
        string? statePath,
        string? baseline,
        string head,
        string format,
        bool failOnDrift,
        CancellationToken cancellationToken)
    {
        // Validated before anything else, so a typo costs a message rather than a git process and a
        // tree load whose output then has nowhere to go. Reported on stderr because the one thing not
        // known here is which format the caller meant: a CI step piping stdout into a PR comment must
        // not post this sentence as the comment.
        if (!Formats.Contains(format, StringComparer.Ordinal))
        {
            return Fail($"--format '{format}' is not one of: {string.Join(", ", Formats)}.", quiet: true);
        }

        var quiet = !string.Equals(format, TableFormat, StringComparison.Ordinal);
        var fullConfigPath = Path.GetFullPath(configPath);

        DocumeConfig config;
        try
        {
            config = ConfigLoader.Load(fullConfigPath);
        }
        catch (ConfigNotFoundException ex)
        {
            return Fail(ex.Message, quiet);
        }
        catch (ConfigValidationException ex)
        {
            return Fail(ex.Message, quiet);
        }
        catch (JsonException ex)
        {
            return Fail($"{fullConfigPath} is not valid JSON: {ex.Message}", quiet);
        }

        var repoRoot = Path.GetDirectoryName(fullConfigPath) ?? Directory.GetCurrentDirectory();
        var wikiRoot = Path.GetFullPath(Path.Combine(repoRoot, config.Wiki.Root));
        var resolvedStatePath = statePath is { Length: > 0 }
            ? Path.GetFullPath(statePath)
            : Path.Combine(wikiRoot, DefaultStateFile.Replace('/', Path.DirectorySeparatorChar));

        DocumeState state;
        try
        {
            state = File.Exists(resolvedStatePath) ? StateStore.Load(resolvedStatePath) : new DocumeState();
        }
        catch (StateVersionException ex)
        {
            return Fail(ex.Message, quiet);
        }
        catch (JsonException ex)
        {
            return Fail($"{resolvedStatePath} is not valid JSON: {ex.Message}", quiet);
        }

        var resolvedBaseline = baseline is { Length: > 0 } ? baseline : state.BaselineSha;
        if (resolvedBaseline is not { Length: > 0 })
        {
            // Loud rather than "assume the whole history": a baseline nobody supplied would otherwise
            // silently become an arbitrary one, and every answer after that is fiction.
            return Fail(
                $"No baseline to diff from. Pass --baseline <sha>, or set baselineSha in "
                + $"{resolvedStatePath} (§5.3 — the commit the wiki was generated against).",
                quiet);
        }

        WikiTree tree;
        try
        {
            tree = WikiTree.Load(wikiRoot, config.Wiki);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Fail(ex.Message, quiet);
        }
        catch (WikiTreeException ex)
        {
            return Fail(
                $"The wiki tree cannot be read, so there are no sources to match against "
                + $"({ex.Errors.Count} error(s)):{Environment.NewLine}  - "
                + string.Join($"{Environment.NewLine}  - ", ex.Errors),
                quiet);
        }

        IReadOnlyList<string> changed;
        try
        {
            changed = await GitRepository
                .ChangedFilesBetweenAsync(repoRoot, resolvedBaseline, head, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            return Fail(ex.Message, quiet);
        }

        var report = DriftPlanner.Plan(resolvedBaseline, head, changed, tree.Pages);

        Report(report, format);

        return failOnDrift && report.HasDrift ? 1 : 0;
    }

    private static void Report(DriftReport report, string format)
    {
        switch (format)
        {
            case JsonFormat:
                // Console.WriteLine, not AnsiConsole: Spectre wraps to the terminal width, which would
                // put newlines inside JSON string values and hand a CI step a body it cannot parse.
                Console.WriteLine(report.ToJson());
                break;

            case CommentFormat:
                // Console.Write: the block already ends in a newline, and a markdown comment posted
                // through an API is bytes rather than terminal output.
                Console.Write(DriftComment.Render(report));
                break;

            default:
                RenderTable(report);
                break;
        }
    }

    private static void RenderTable(DriftReport report)
    {
        AnsiConsole.MarkupLine($"Baseline: [grey]{report.Baseline.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"Head:     [grey]{report.Head.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"Changed:  [grey]{report.ChangedFileCount} file(s)[/]");

        RenderPages(report);
        RenderVerdict(report);
    }

    /// <summary>
    /// The affected pages, uncapped: a drift report that quietly listed the first N pages would read as
    /// a complete answer when it was not. Only the per-pattern file lists are trimmed, and they say so.
    /// </summary>
    private static void RenderPages(DriftReport report)
    {
        if (!report.HasDrift)
        {
            return;
        }

        AnsiConsole.WriteLine();

        var table = new Table()
            .AddColumn("Page")
            .AddColumn("Path")
            .AddColumn("Files")
            .AddColumn("Matched patterns");

        foreach (var page in report.Pages)
        {
            table.AddRow(
                page.Title.EscapeMarkup(),
                page.Path.EscapeMarkup(),
                page.MatchedFileCount.ToString(),
                Patterns(page).EscapeMarkup());
        }

        AnsiConsole.Write(table);
    }

    private static string Patterns(DriftedPage page) => string.Join(
        Environment.NewLine,
        page.Matches.Select(match => $"{match.Pattern} ({match.Files.Count})"));

    private static void RenderVerdict(DriftReport report)
    {
        AnsiConsole.WriteLine();

        if (report.SourcesUndeclared)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No page declares a 'sources:' glob, so drift can never be reported "
                + $"({report.PageCount} page(s) in the tree). Add 'sources:' to page frontmatter to "
                + $"link a page to the code it documents (§5.2).[/]");

            return;
        }

        if (!report.HasDrift)
        {
            AnsiConsole.MarkupLine(
                $"[green]No documented sources touched.[/] [grey]{report.PagesWithSourcesCount} of "
                + $"{report.PageCount} page(s) declare sources.[/]");

            return;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]{report.AffectedCount} of {report.PagesWithSourcesCount} page(s) with declared "
            + $"sources may need review.[/] [grey]Advisory — nothing was changed or marked.[/]");
    }

    /// <summary>
    /// A failed run, which is never drift: exit 1 whatever <c>--fail-on-drift</c> says, and on stderr in
    /// the machine formats so a CI step never posts an error message as a PR comment body.
    /// </summary>
    private static int Fail(string message, bool quiet)
    {
        if (quiet)
        {
            Console.Error.WriteLine(message);

            return 1;
        }

        AnsiConsole.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

        return 1;
    }
}
