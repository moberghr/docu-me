using System.CommandLine;
using DocuMe.Core.Config;
using DocuMe.Core.Scaffolding;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume init</c> — scaffolds <c>docume.json</c> and a wiki skeleton in the current
/// directory (PLAN.md §6.1). Idempotent: existing files are reported as skipped, never
/// overwritten.
/// </summary>
/// <remarks>
/// <c>--adopt</c> is the one mode that can fail without failing a file write: it builds the state file
/// from a wiki the repo already has, and every reason it cannot leaves the file alone
/// (<see cref="WikiAdopter"/>). A consumer who asked to adopt and got nothing must not read exit 0 as
/// "done", so an unadopted state row is exit 1 with the reason printed.
/// </remarks>
internal static class InitCommand
{
    public static Command Build()
    {
        var spaceOption = new Option<string>("--space")
        {
            Description = "Confluence space key to write into docume.json.",
        };
        var baseUrlOption = new Option<string>("--base-url")
        {
            Description = "Confluence wiki base URL to write into docume.json.",
        };
        var adoptOption = new Option<bool>("--adopt")
        {
            Description = "Build _meta/state.json from the wiki this repo already has (one entry per "
                + "page, pageIds seeded from frontmatter) instead of writing an empty one.",
        };
        var legacyMapOption = new Option<string>("--legacy-map")
        {
            Description = "Path to a JSON '<page path>': '<page id>' map from whatever published the "
                + "wiki before, to seed pageIds from. Requires --adopt.",
        };
        var agentOption = new Option<string>("--agent")
        {
            Description = "Workflow agent to scaffold: claude or copilot. Defaults to the recorded choice, then claude.",
        };

        var command = new Command(
            "init",
            "Scaffold docume.json and a wiki skeleton in the current directory.")
        {
            spaceOption,
            baseUrlOption,
            adoptOption,
            legacyMapOption,
            agentOption,
        };

        command.SetAction(parseResult =>
        {
            var space = parseResult.GetValue(spaceOption);
            var baseUrl = parseResult.GetValue(baseUrlOption);
            var adopt = parseResult.GetValue(adoptOption);
            var legacyMap = parseResult.GetValue(legacyMapOption);
            var agentName = parseResult.GetValue(agentOption);

            AgentRail? agent = null;

            if (agentName is not null)
            {
                agent = agentName.ToLowerInvariant() switch
                {
                    "claude" => AgentRail.Claude,
                    "copilot" => AgentRail.Copilot,
                    _ => null,
                };

                if (agent is null)
                {
                    AnsiConsole.MarkupLine(
                        "[red]--agent must be either claude or copilot.[/] Drop the option to keep the recorded choice.");

                    return 1;
                }
            }

            if (!adopt && legacyMap is { Length: > 0 })
            {
                // Refused rather than treated as implying --adopt: a flag that silently switches the
                // command's mode is worse than one that says it needs company.
                AnsiConsole.MarkupLine(
                    "[red]--legacy-map seeds page ids into an adopted state file, so it does nothing "
                    + "without --adopt.[/] Add --adopt, or drop the map.");

                return 1;
            }

            var results = ProjectScaffolder.Scaffold(
                Directory.GetCurrentDirectory(),
                space,
                baseUrl,
                adopt,
                legacyMap,
                agent);

            Render(results);

            return adopt ? AdoptionExitCode(results) : 0;
        });

        return command;
    }

    /// <summary>
    /// Exit 1 when <c>--adopt</c> wrote no entries. Everything else in <c>init</c> either writes or
    /// skips a file that was already right, which is success; an adoption that did not happen is not.
    /// </summary>
    private static int AdoptionExitCode(IReadOnlyList<ScaffoldResult> results)
    {
        var state = results.SingleOrDefault(r => r.RelativePath.EndsWith(
            ProjectScaffolder.StateFile,
            StringComparison.Ordinal));

        if (state is null || state.Action != ScaffoldAction.Skipped)
        {
            return 0;
        }

        AnsiConsole.MarkupLine(
            "[red]--adopt wrote no page entries[/] — the note above says why. Nothing else this run "
            + "created was undone; fix that one thing and run init --adopt again.");

        return 1;
    }

    private static void Render(IReadOnlyList<ScaffoldResult> results)
    {
        var table = new Table().AddColumn("File").AddColumn("Result");

        foreach (var result in results)
        {
            table.AddRow(result.RelativePath.EscapeMarkup(), Describe(result.Action));
        }

        AnsiConsole.Write(table);

        // Below the table rather than as a third column: a note explains why a path is not the
        // obvious one and runs to a sentence or two, which a column would wrap into noise.
        foreach (var note in results.Where(r => r.Note is not null))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]note[/] {note.RelativePath.EscapeMarkup()}: {note.Note!.EscapeMarkup()}");
        }
    }

    /// <summary>
    /// Throws on an unhandled action rather than defaulting to "skipped": a new
    /// <see cref="ScaffoldAction"/> reported as a no-op would be the most misleading thing this table
    /// could say.
    /// </summary>
    private static string Describe(ScaffoldAction action) => action switch
    {
        ScaffoldAction.Created => "[green]created[/]",
        ScaffoldAction.Updated => "[blue]updated[/]",
        ScaffoldAction.Skipped => "[yellow]skipped[/]",
        _ => throw new ArgumentOutOfRangeException(
            nameof(action),
            action,
            "Unhandled scaffold action."),
    };
}
