using System.CommandLine;
using DocuMe.Core.Scaffolding;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume init</c> — scaffolds <c>docume.json</c> and a <c>docs/wiki</c>
/// skeleton in the current directory (PLAN.md §6.1). Idempotent: existing files
/// are reported as skipped, never overwritten.
/// </summary>
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

        var command = new Command(
            "init",
            "Scaffold docume.json and a docs/wiki skeleton in the current directory.")
        {
            spaceOption,
            baseUrlOption,
        };

        command.SetAction(parseResult =>
        {
            var space = parseResult.GetValue(spaceOption);
            var baseUrl = parseResult.GetValue(baseUrlOption);

            var results = ProjectScaffolder.Scaffold(Directory.GetCurrentDirectory(), space, baseUrl);
            Render(results);
            return 0;
        });

        return command;
    }

    private static void Render(IReadOnlyList<ScaffoldResult> results)
    {
        var table = new Table().AddColumn("File").AddColumn("Result");

        foreach (var result in results)
        {
            var status = result.Action == ScaffoldAction.Created
                ? "[green]created[/]"
                : "[yellow]skipped[/]";
            table.AddRow(result.RelativePath.EscapeMarkup(), status);
        }

        AnsiConsole.Write(table);
    }
}
