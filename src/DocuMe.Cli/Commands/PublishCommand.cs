using System.CommandLine;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Markdown;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume publish</c> — PLAN.md §6.2. This build wires the <c>--dry-run</c> half: it loads the
/// config, tree and state, converts every page, decides what a real run would do with it, and prints
/// the plan. Nothing is uploaded and nothing is written, including <c>state.json</c>.
/// </summary>
/// <remarks>
/// The decision and reporting logic lives in <see cref="PublishPipeline"/> and
/// <see cref="PublishReport"/> so tests drive it without System.CommandLine; this file is argument
/// parsing, file resolution and Spectre output.
/// </remarks>
internal static class PublishCommand
{
    /// <summary>How many pages are listed per section before the rest are summarized.</summary>
    private const int PagesPerSection = 15;

    /// <summary>Where <c>docume init</c> scaffolds the state file, relative to the wiki root (§5.3).</summary>
    private const string DefaultStateFile = "_meta/state.json";

    public static Command Build()
    {
        var configOption = new Option<string>("--config")
        {
            Description = "Path to docume.json. Its directory is the repo root wiki.root resolves against.",
            DefaultValueFactory = _ => ConfigLoader.DefaultFileName,
        };
        var stateOption = new Option<string>("--state")
        {
            Description = $"Path to state.json. Defaults to <wiki.root>/{DefaultStateFile}; a missing "
                + "file plans a first publish (every page created).",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Convert, decide and print the plan without writing anything.",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Republish every page even when nothing changed, re-uploading its attachments.",
        };
        var allowProtectedSpaceOption = new Option<bool>("--allow-protected-space")
        {
            Description = "Write into a space listed in confluence.protectedSpaces. One run only; "
                + "there is no config value that grants this.",
        };

        var command = new Command(
            "publish",
            "Convert the wiki and publish it to Confluence. --dry-run plans the run and writes nothing.")
        {
            configOption,
            stateOption,
            dryRunOption,
            forceOption,
            allowProtectedSpaceOption,
        };

        command.SetAction(parseResult => Run(
            parseResult.GetValue(configOption)!,
            parseResult.GetValue(stateOption),
            parseResult.GetValue(dryRunOption),
            parseResult.GetValue(forceOption),
            parseResult.GetValue(allowProtectedSpaceOption)));

        return command;
    }

    private static int Run(
        string configPath,
        string? statePath,
        bool dryRun,
        bool force,
        bool allowProtectedSpace)
    {
        var fullConfigPath = Path.GetFullPath(configPath);

        DocumeConfig config;
        try
        {
            config = ConfigLoader.Load(fullConfigPath);
        }
        catch (ConfigNotFoundException ex)
        {
            return Fail(ex.Message);
        }
        catch (ConfigValidationException ex)
        {
            return Fail(ex.Message);
        }
        catch (JsonException ex)
        {
            return Fail($"{fullConfigPath} is not valid JSON: {ex.Message}");
        }

        var repoRoot = Path.GetDirectoryName(fullConfigPath) ?? Directory.GetCurrentDirectory();
        var wikiRoot = Path.GetFullPath(Path.Combine(repoRoot, config.Wiki.Root));
        var resolvedStatePath = statePath is { Length: > 0 }
            ? Path.GetFullPath(statePath)
            : Path.Combine(wikiRoot, DefaultStateFile.Replace('/', Path.DirectorySeparatorChar));

        AnsiConsole.MarkupLine($"Config:    [blue]{fullConfigPath.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"Wiki root: [blue]{wikiRoot.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"Space:     [blue]{(config.Confluence.SpaceKey ?? "?").EscapeMarkup()}[/]");

        WikiTree tree;
        try
        {
            tree = WikiTree.Load(wikiRoot, config.Wiki);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Fail(ex.Message);
        }
        catch (WikiTreeException ex)
        {
            AnsiConsole.MarkupLine($"[red]The wiki tree cannot be published as it stands ({ex.Errors.Count}):[/]");
            foreach (var error in ex.Errors)
            {
                AnsiConsole.MarkupLine($"  [red]•[/] {error.EscapeMarkup()}");
            }

            return 1;
        }

        var state = LoadState(resolvedStatePath, out var stateFailure);
        if (state is null)
        {
            return Fail(stateFailure!);
        }

        var report = PublishPipeline.Plan(
            config,
            tree,
            state,
            new PublishOptions
            {
                Force = force,
                AllowProtectedSpace = allowProtectedSpace,

                // One date for the whole run, in UTC so a laptop and a CI runner agree (§8).
                GeneratedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            });

        Render(report);

        if (!report.CanPublish)
        {
            return 1;
        }

        if (dryRun)
        {
            return 0;
        }

        // Said plainly rather than dressed up as success: the plan above is real, the upload is not
        // built yet. §6.2's write half (upsert, attachment upload, labels, state write-back,
        // --changed-since / --page / --prune) lands in the next slice.
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[yellow]Writing to Confluence is not implemented yet — nothing was published.[/] "
            + "Re-run with --dry-run to plan without this warning.");
        return 1;
    }

    /// <summary>
    /// Loads state, treating a missing file as "never published" rather than an error: that is what a
    /// first publish looks like, and every page then plans as a create.
    /// </summary>
    private static DocumeState? LoadState(string path, out string? failure)
    {
        failure = null;

        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine(
                $"State:     [yellow]{path.EscapeMarkup()}[/] [grey](not found — planning a first publish)[/]");
            return new DocumeState();
        }

        AnsiConsole.MarkupLine($"State:     [blue]{path.EscapeMarkup()}[/]");

        try
        {
            return StateStore.Load(path);
        }
        catch (StateVersionException ex)
        {
            failure = ex.Message;
            return null;
        }
        catch (JsonException ex)
        {
            failure = $"{path} is not valid JSON: {ex.Message}";
            return null;
        }
    }

    private static void Render(PublishReport report)
    {
        AnsiConsole.WriteLine();

        var table = new Table().AddColumn("Action").AddColumn("Pages").AddColumn("What a real run would do");
        table.AddRow("[green]create[/]", report.CreateCount.ToString(), "new page, all attachments uploaded");
        table.AddRow("[yellow]update[/]", report.UpdateCount.ToString(), "body rewritten, page version spent");
        table.AddRow(
            "[blue]attachments[/]",
            report.AttachmentOnlyCount.ToString(),
            "changed attachment bytes only, no page version");
        table.AddRow("[grey]skip[/]", report.SkipCount.ToString(), "nothing moved");
        AnsiConsole.Write(table);

        var unrendered = report.UnrenderedDiagramCount > 0
            ? $" ({report.UnrenderedDiagramCount} of them diagrams still to render)"
            : string.Empty;
        AnsiConsole.MarkupLine($"Attachment uploads: {report.UploadCount}{unrendered}");

        if (report.OrphanAttachmentCount > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Stale attachments state still lists: {report.OrphanAttachmentCount} "
                + "(reported, never deleted)[/]");
        }

        if (report.DiagnosticCount > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Conversion degradations: {report.DiagnosticCount} "
                + "(run `docume convert` for the grouped report)[/]");
        }

        RenderApprovals(report);
        RenderOrphans(report);
        RenderFailures(report);
        RenderVerdict(report);
    }

    /// <summary>
    /// The approvals a real run would revoke (§6.2 step 7, §8), listed by name: this is the one part
    /// of the plan a reviewer has to read before a bulk republish.
    /// </summary>
    private static void RenderApprovals(PublishReport report)
    {
        var invalidated = report.InvalidatedApprovals;
        if (invalidated.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]APPROVALS THIS RUN WOULD REVOKE[/] — {invalidated.Count} approved page(s) changed; "
            + "the `approved` label is removed and state moves to needs-review");
        RenderPaths(invalidated.Select(page => page.Path));
    }

    private static void RenderOrphans(PublishReport report)
    {
        if (report.OrphanPages.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]ORPHANS[/] — {report.OrphanPages.Count} state entr(ies) whose markdown file is gone; "
            + "deleted only by --prune after confirmation");
        RenderPaths(report.OrphanPages);
    }

    private static void RenderFailures(PublishReport report)
    {
        if (report.Failures.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[red]PAGES THE CONVERTER REFUSES[/] — {report.Failures.Count}");

        foreach (var failure in report.Failures.Take(PagesPerSection))
        {
            AnsiConsole.MarkupLine($"  [red]•[/] {failure.Path.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"    [grey]{failure.Message.EscapeMarkup()}[/]");
        }

        if (report.Failures.Count > PagesPerSection)
        {
            AnsiConsole.MarkupLine($"  [grey]… and {report.Failures.Count - PagesPerSection} more page(s)[/]");
        }
    }

    private static void RenderVerdict(PublishReport report)
    {
        AnsiConsole.WriteLine();

        if (report.WriteRefusal is { } refusal)
        {
            AnsiConsole.MarkupLine($"[red]REFUSED[/] — {refusal.EscapeMarkup()}");
            return;
        }

        if (report.Failures.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]NOT PUBLISHABLE[/] — {report.Failures.Count} page(s) the converter refuses. "
                + "No page publishes until every page converts.");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[green]PLAN OK[/] — {report.Pages.Count} page(s) convert; "
            + $"{report.CreateCount + report.UpdateCount} body write(s), {report.UploadCount} upload(s). "
            + "Nothing was written.");
    }

    private static void RenderPaths(IEnumerable<string> paths)
    {
        var listed = paths.ToList();

        foreach (var path in listed.Take(PagesPerSection))
        {
            AnsiConsole.MarkupLine($"  [grey]{path.EscapeMarkup()}[/]");
        }

        // Say what was dropped rather than letting a capped list read as the whole list.
        if (listed.Count > PagesPerSection)
        {
            AnsiConsole.MarkupLine($"  [grey]… and {listed.Count - PagesPerSection} more[/]");
        }
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLine($"[red]{message.EscapeMarkup()}[/]");
        return 1;
    }
}
