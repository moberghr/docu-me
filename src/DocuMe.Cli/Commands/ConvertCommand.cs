using System.CommandLine;
using DocuMe.Core.Acceptance;
using DocuMe.Core.Markdown;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume convert</c> — converts every page of a wiki and reports what happened, grouped by
/// construct and by dialect. This is PLAN.md §4.4's acceptance check ("all 79 AurServices pages
/// convert with zero errors and zero unknown-construct warnings") made runnable.
/// </summary>
/// <remarks>
/// Read-only: no Confluence call, no credentials, nothing written — not even the storage format it
/// renders. Exit code 0 means the corpus clears §4.4's bar, 1 means it does not (or the tree
/// cannot be loaded at all), so it doubles as a CI pre-flight for a consumer repo.
/// </remarks>
internal static class ConvertCommand
{
    /// <summary>How many pages are listed per construct before the rest are summarized.</summary>
    private const int PagesPerConstruct = 10;

    public static Command Build()
    {
        var wikiRootArgument = new Argument<string>("wiki-root")
        {
            Description = "Wiki root directory to convert (the folder holding the markdown pages).",
        };
        var acceptOption = new Option<string[]>("--accept")
        {
            Description =
                "Diagnostic code to treat as an accepted loss: still reported, but counted as a "
                + "note instead of a warning. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command(
            "convert",
            "Convert every wiki page and report failures and degradations. Read-only: nothing is published.")
        {
            wikiRootArgument,
            acceptOption,
        };

        command.SetAction(parseResult =>
        {
            var wikiRoot = parseResult.GetValue(wikiRootArgument)!;
            var policy = new AcceptancePolicy(parseResult.GetValue(acceptOption) ?? []);

            return Run(wikiRoot, policy);
        });

        return command;
    }

    private static int Run(string wikiRoot, AcceptancePolicy policy)
    {
        var full = Path.GetFullPath(wikiRoot);
        AnsiConsole.MarkupLine($"Wiki root: [blue]{full.EscapeMarkup()}[/]");

        WikiTree tree;
        try
        {
            tree = WikiTree.Load(full);
        }
        catch (DirectoryNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }
        catch (WikiTreeException ex)
        {
            // The tree's own problems are §4.4 findings too — a page with no title or two pages
            // claiming one title cannot publish — so report them like failures instead of letting
            // the exception surface as a stack trace.
            AnsiConsole.MarkupLine($"[red]The wiki tree cannot be published as it stands ({ex.Errors.Count}):[/]");
            foreach (var error in ex.Errors)
            {
                AnsiConsole.MarkupLine($"  [red]•[/] {error.EscapeMarkup()}");
            }

            return 1;
        }

        var report = ConversionAcceptance.RunTree(tree, policy);
        Render(report);
        return report.MeetsAcceptanceBar ? 0 : 1;
    }

    private static void Render(AcceptanceReport report)
    {
        var notes = report.DiagnosticCount - report.WarningCount;
        AnsiConsole.MarkupLine(
            $"Pages: {report.PageCount}   failed: {report.FailedPageCount}   "
            + $"degradations: {report.DiagnosticCount} ({report.WarningCount} warnings, {notes} notes)");

        if (report.Policy.AcceptedCodes.Count > 0)
        {
            var accepted = string.Join(", ", report.Policy.AcceptedCodes);
            AnsiConsole.MarkupLine($"Accepted as notes: [grey]{accepted.EscapeMarkup()}[/]");
        }

        RenderFailures(report);
        RenderDiagnostics(report);
        RenderVerdict(report);
    }

    private static void RenderFailures(AcceptanceReport report)
    {
        if (report.Failures.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[red]FAILED PAGES BY CONSTRUCT[/] — {report.FailedPageCount} of {report.PageCount}");

        foreach (var group in report.Failures)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  [red]{group.Count} page(s)[/]: {group.Kind.EscapeMarkup()}");
            RenderConstructs(
                group.ByToken,
                group.Occurrences.Select(occurrence => (occurrence.Token, occurrence.Path)));

            // A message that quotes nothing has no dialect axis, so the pages still need listing.
            if (group.ByToken.Count == 0)
            {
                RenderPages(group.Occurrences.Select(occurrence => occurrence.Path));
            }
        }
    }

    private static void RenderDiagnostics(AcceptanceReport report)
    {
        if (report.Diagnostics.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]DEGRADATIONS BY CODE AND DIALECT[/] — {report.DiagnosticCount} total");

        foreach (var group in report.Diagnostics)
        {
            var color = group.Severity == DiagnosticSeverity.Warning ? "yellow" : "grey";
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"  [{color}]{group.Severity.ToString().ToLowerInvariant()}[/] [bold]{group.Code}[/] — "
                + $"{group.Count} occurrence(s) on {group.PageCount} page(s)");
            RenderConstructs(
                group.ByConstruct,
                group.Occurrences.Select(occurrence => ((string?)occurrence.Construct, occurrence.Path)));
            AnsiConsole.MarkupLine($"      [grey]e.g. {group.Occurrences[0].Message.EscapeMarkup()}[/]");
        }
    }

    private static void RenderVerdict(AcceptanceReport report)
    {
        AnsiConsole.WriteLine();

        if (report.MeetsAcceptanceBar)
        {
            AnsiConsole.MarkupLine(
                $"[green]ACCEPTANCE (PLAN.md §4.4): MET[/] — {report.PageCount} pages, 0 errors, 0 warnings");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[red]ACCEPTANCE (PLAN.md §4.4): NOT MET[/] — {report.FailedPageCount} failed page(s), "
            + $"{report.WarningCount} warning(s)");
    }

    /// <summary>The "by dialect" axis: each construct with its count and the pages it occurred on.</summary>
    private static void RenderConstructs(
        IReadOnlyList<ConstructCount> constructs,
        IEnumerable<(string? Construct, string Path)> occurrences)
    {
        var byConstruct = occurrences.ToList();

        foreach (var construct in constructs)
        {
            AnsiConsole.MarkupLine($"      [bold]{construct.Construct.EscapeMarkup()}[/] ({construct.Count})");
            RenderPages(byConstruct
                .Where(occurrence => string.Equals(occurrence.Construct, construct.Construct, StringComparison.Ordinal))
                .Select(occurrence => occurrence.Path));
        }
    }

    private static void RenderPages(IEnumerable<string> paths)
    {
        var distinct = paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        foreach (var path in distinct.Take(PagesPerConstruct))
        {
            AnsiConsole.MarkupLine($"        [grey]{path.EscapeMarkup()}[/]");
        }

        // Say what was dropped rather than letting a capped list read as the whole list.
        if (distinct.Count > PagesPerConstruct)
        {
            AnsiConsole.MarkupLine($"        [grey]… and {distinct.Count - PagesPerConstruct} more page(s)[/]");
        }
    }
}
