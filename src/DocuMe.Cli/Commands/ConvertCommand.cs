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

        var renderMermaidOption = new Option<bool>("--render-mermaid")
        {
            Description =
                "Also render every mermaid diagram through Node and report the ones that fail. "
                + "Off by default: it starts a process per diagram and needs beautiful-mermaid "
                + "installed. Without it, diagrams are counted but never checked.",
        };
        var rendererOption = new Option<string>("--renderer")
        {
            Description =
                "Path to render-mermaid.mjs for --render-mermaid. Defaults to where `docume init` "
                + "scaffolds it and docume.json -> mermaid.renderer names it.",
            DefaultValueFactory = _ => "tools/render-mermaid.mjs",
        };

        var command = new Command(
            "convert",
            "Convert every wiki page and report failures and degradations. Read-only: nothing is published.")
        {
            wikiRootArgument,
            acceptOption,
            renderMermaidOption,
            rendererOption,
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            var wikiRoot = parseResult.GetValue(wikiRootArgument)!;
            var policy = new AcceptancePolicy(parseResult.GetValue(acceptOption) ?? []);
            var renderer = parseResult.GetValue(renderMermaidOption)
                ? new MermaidRenderer(Path.GetFullPath(parseResult.GetValue(rendererOption)!))
                : null;

            return RunAsync(wikiRoot, policy, renderer, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        string wikiRoot,
        AcceptancePolicy policy,
        MermaidRenderer? mermaidRenderer,
        CancellationToken cancellationToken)
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

        if (mermaidRenderer is not null && report.Diagrams.Count > 0)
        {
            var distinct = report.Diagrams.Select(diagram => diagram.Source).Distinct(StringComparer.Ordinal).Count();
            AnsiConsole.MarkupLine($"Rendering {distinct} distinct mermaid diagram(s) through Node…");

            try
            {
                report = await MermaidAcceptance
                    .RenderDiagramsAsync(report, mermaidRenderer, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MermaidRenderException ex)
            {
                // A setup fault, not a diagram's: it would have failed every diagram identically,
                // so reporting it as findings would read as a broken corpus.
                AnsiConsole.MarkupLine($"[red]The mermaid render pass could not run:[/] {ex.Message.EscapeMarkup()}");
                return 1;
            }
        }

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
        RenderDiagrams(report);
        RenderVerdict(report);
    }

    /// <summary>
    /// The mermaid section. Prints the dialect census either way, because "27 diagrams, none
    /// checked" is a materially different result from "27 diagrams, all render" and a report that
    /// looked identical in both cases would be the silent failure this pass exists to remove.
    /// </summary>
    private static void RenderDiagrams(AcceptanceReport report)
    {
        var diagrams = report.Diagrams;
        if (diagrams.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();

        if (report.Renders is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]MERMAID DIAGRAMS — {diagrams.Count} found, NOT CHECKED[/] "
                + "(pass --render-mermaid to render them; conversion cannot tell whether a diagram renders)");
            return;
        }

        var renders = report.Renders;
        var color = renders.AllRendered ? "green" : "red";
        AnsiConsole.MarkupLine(
            $"[{color}]MERMAID DIAGRAMS[/] — {renders.Count} found ({renders.DistinctCount} distinct), "
            + $"{renders.FailedCount} failed to render on {renders.FailedPageCount} page(s)");

        foreach (var group in renders.Failures)
        {
            AnsiConsole.WriteLine();

            // Detail, not Reason: Reason is the grouping key and has every quoted run elided,
            // including the list of headers the renderer accepts — the only actionable half.
            AnsiConsole.MarkupLine($"  [red]{group.Count} diagram(s)[/]: {group.Detail.EscapeMarkup()}");
            RenderConstructs(
                group.ByDialect,
                group.Occurrences.Select(occurrence => ((string?)occurrence.Dialect, occurrence.Path)));
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]dialect census (every diagram, rendered or not):[/]");
        foreach (var dialect in renders.ByDialect)
        {
            AnsiConsole.MarkupLine($"      [bold]{dialect.Construct.EscapeMarkup()}[/] ({dialect.Count})");
        }
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

        // An unchecked diagram is named in the verdict itself: the bar can be MET while the corpus
        // holds a diagram that will fail at publish, and a bare "MET" would hide exactly that.
        var uncheckedNote = report.Renders is null && report.Diagrams.Count > 0
            ? $", {report.Diagrams.Count} diagram(s) unchecked"
            : string.Empty;

        if (report.MeetsAcceptanceBar)
        {
            AnsiConsole.MarkupLine(
                $"[green]ACCEPTANCE (PLAN.md §4.4): MET[/] — {report.PageCount} pages, 0 errors, "
                + $"0 warnings{uncheckedNote}");
            return;
        }

        var diagrams = report.Renders is null || report.Renders.AllRendered
            ? string.Empty
            : $", {report.Renders.FailedCount} unrenderable diagram(s)";

        AnsiConsole.MarkupLine(
            $"[red]ACCEPTANCE (PLAN.md §4.4): NOT MET[/] — {report.FailedPageCount} failed page(s), "
            + $"{report.WarningCount} warning(s){diagrams}{uncheckedNote}");
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
