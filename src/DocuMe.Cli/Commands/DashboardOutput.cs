using DocuMe.Core.Dashboard;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// How a dashboard upsert reads in the terminal, and what it exits with. Shared by
/// <see cref="DashboardCommand"/> and by <c>drift --mark</c>, which refreshes the same page (PLAN.md §6.4).
/// </summary>
/// <remarks>
/// One voice for one page: the two commands write the same dashboard, and a reader comparing their output
/// should not have to work out whether "UPDATED" and "updated page 300001" mean the same thing.
/// </remarks>
internal static class DashboardOutput
{
    /// <summary>
    /// Reports <paramref name="result"/> and returns the process exit code — 0 for anything that landed,
    /// 1 for a space that does not resolve.
    /// </summary>
    /// <param name="console">
    /// Where the line goes. <c>drift --mark</c> passes a stderr console in the machine formats, where
    /// stdout carries a JSON document or a PR comment body.
    /// </param>
    /// <param name="result">The outcome from <see cref="DashboardPublisher.UpsertAsync"/>.</param>
    /// <param name="spaceKey">The space the page was looked for in, for the not-found message.</param>
    /// <param name="pageTitle">The dashboard page title.</param>
    public static int Report(
        IAnsiConsole console,
        DashboardUpsertResult result,
        string spaceKey,
        string pageTitle)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case DashboardUpsertOutcome.SpaceNotFound:
                console.MarkupLine(
                    $"[red]Confluence has no space with key '{spaceKey.EscapeMarkup()}', or this account "
                    + "cannot see it. Check confluence.spaceKey in docume.json (§5.1).[/]");

                return 1;

            case DashboardUpsertOutcome.Created:
                console.MarkupLine(
                    $"[green]CREATED[/] [blue]{pageTitle.EscapeMarkup()}[/] "
                    + $"[grey](page {Page(result)})[/]");

                return 0;

            case DashboardUpsertOutcome.Unchanged:
                console.MarkupLine(
                    $"[green]UNCHANGED[/] [blue]{pageTitle.EscapeMarkup()}[/] "
                    + $"[grey](page {Page(result)}, v{result.Version}) — the rendered body matches what is "
                    + "published, so no version was spent.[/]");

                return 0;

            default:
                console.MarkupLine(
                    $"[green]UPDATED[/] [blue]{pageTitle.EscapeMarkup()}[/] "
                    + $"[grey](page {Page(result)}, now v{result.Version})[/]");

                return 0;
        }
    }

    private static string Page(DashboardUpsertResult result) =>
        (result.PageId ?? "unknown").EscapeMarkup();
}
