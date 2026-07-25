using System.CommandLine;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Git;
using DocuMe.Core.Markdown;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume publish</c> — PLAN.md §6.2. Loads the config, tree and state, converts every page,
/// decides what the run does with it, prints the plan, and — unless <c>--dry-run</c> — executes it:
/// pages upserted parents-first, changed attachments uploaded, invalidated approvals revoked,
/// <c>state.json</c> written.
/// </summary>
/// <remarks>
/// The decision logic lives in <see cref="PublishPipeline"/> and the write path in
/// <see cref="PublishExecutor"/>, so tests drive both without System.CommandLine; this file is
/// argument parsing, file resolution and Spectre output.
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
        var treeOption = new Option<bool>("--tree")
        {
            Description = "Also print the page tree the run would build (parents resolved from the "
                + "directory index pages).",
        };
        var changedSinceOption = new Option<string>("--changed-since")
        {
            Description = "Write only the pages touched since <sha> — `git diff --name-only` over the "
                + "wiki root, including the pages whose images changed. The whole tree is still loaded, "
                + "converted and checked for orphans.",
        };
        var pageOption = new Option<string[]>("--page")
        {
            Description = "Write only these pages (wiki-root-relative markdown paths). Repeatable; a "
                + "path that is not in the tree is an error.",
            DefaultValueFactory = _ => [],
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
            treeOption,
            changedSinceOption,
            pageOption,
        };

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetValue(configOption)!,
            parseResult.GetValue(stateOption),
            parseResult.GetValue(dryRunOption),
            parseResult.GetValue(forceOption),
            parseResult.GetValue(allowProtectedSpaceOption),
            parseResult.GetValue(treeOption),
            parseResult.GetValue(changedSinceOption),
            parseResult.GetValue(pageOption) ?? [],
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string configPath,
        string? statePath,
        bool dryRun,
        bool force,
        bool allowProtectedSpace,
        bool printTree,
        string? changedSince,
        string[] pagePaths,
        CancellationToken cancellationToken)
    {
        if (changedSince is { Length: > 0 } && pagePaths.Length > 0)
        {
            return Fail(
                "--changed-since and --page cannot be combined: each one narrows the run in its own way, "
                + "and guessing which you meant would be worse than asking. Pick one.");
        }

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

        ScopeResolution resolved;
        try
        {
            resolved = await ResolveScopeAsync(wikiRoot, changedSince, pagePaths, tree, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitException ex)
        {
            // A scope that cannot be computed must not fall back to the whole tree: the caller asked for
            // less, and silently publishing more is the one outcome they did not ask for.
            return Fail(ex.Message);
        }

        if (resolved.UnknownPaths.Count > 0)
        {
            return FailUnknownPages(resolved.UnknownPaths, tree);
        }

        var scope = resolved.Scope;

        if (scope is not null)
        {
            AnsiConsole.MarkupLine(
                $"Scope:     [blue]{scope.Description.EscapeMarkup()}[/] "
                + $"[grey]({scope.Paths.Count} path(s) in scope)[/]");
        }

        var report = PublishPipeline.Plan(
            config,
            tree,
            state,
            new PublishOptions
            {
                Force = force,
                AllowProtectedSpace = allowProtectedSpace,
                Scope = scope,

                // One date for the whole run, in UTC so a laptop and a CI runner agree (§8).
                GeneratedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            });

        Render(report);

        if (printTree)
        {
            RenderTree(report, config.Confluence.RootPageId);
        }

        if (!report.CanPublish)
        {
            return 1;
        }

        if (dryRun)
        {
            return 0;
        }

        return await PublishAsync(config, repoRoot, wikiRoot, resolvedStatePath, report, state, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The write half (§6.2 steps 5-8). Credentials come from the environment and nowhere else
    /// (rule §1.1); every decision was already made by the plan printed above.
    /// </summary>
    private static async Task<int> PublishAsync(
        DocumeConfig config,
        string repoRoot,
        string wikiRoot,
        string statePath,
        PublishReport report,
        DocumeState state,
        CancellationToken cancellationToken)
    {
        ConfluenceCredentials credentials;
        try
        {
            credentials = ConfluenceCredentials.FromEnvironment();
        }
        catch (ConfluenceCredentialsException ex)
        {
            return Fail(ex.Message);
        }

        if (!Uri.TryCreate(config.Confluence.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            return Fail(
                $"confluence.baseUrl '{config.Confluence.BaseUrl}' is not an absolute URL. It should look "
                + "like https://your-site.atlassian.net/wiki (PLAN.md §5.1).");
        }

        using var client = ConfluenceClient.Create(new ConfluenceClientOptions { BaseUrl = baseUrl }, credentials);

        var rendererPath = Path.GetFullPath(Path.Combine(repoRoot, config.Mermaid.Renderer));
        var executor = new PublishExecutor(client, wikiRoot, new MermaidRenderer(rendererPath).RenderAsync);
        var sha = await GitRepository.TryReadHeadAsync(repoRoot, cancellationToken).ConfigureAwait(false);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"Publishing to [blue]{baseUrl.ToString().EscapeMarkup()}[/] as "
            + $"[blue]{credentials.Email.EscapeMarkup()}[/]…");

        var outcome = await executor
            .ExecuteAsync(config, report, state, new PublishExecutionOptions { RepoSha = sha }, cancellationToken)
            .ConfigureAwait(false);

        // Persisted before anything is reported, and persisted even after a failure: a page id earned
        // by a create must not be lost, or the next run creates the page again and Confluence rejects
        // the duplicate title (§6.2 step 8).
        if (outcome.StateChanged)
        {
            StateStore.Save(statePath, outcome.State);
            AnsiConsole.MarkupLine($"State written: [blue]{statePath.EscapeMarkup()}[/]");
        }

        RenderOutcome(outcome, sha);

        return outcome.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// Turns <c>--changed-since</c> / <c>--page</c> into a <see cref="PublishScope"/>, or answers
    /// <c>null</c> for a whole-tree run (§6.2, last paragraph).
    /// </summary>
    /// <remarks>
    /// Only <c>--page</c> is checked against the tree. A file <c>--changed-since</c> reports may
    /// legitimately name a deleted page, an image, or something under <c>_meta/</c>, whereas a
    /// hand-typed page path that matches nothing is a typo, and a typo that publishes nothing while
    /// exiting 0 is the mistake this check exists to prevent.
    /// </remarks>
    private static async Task<ScopeResolution> ResolveScopeAsync(
        string wikiRoot,
        string? changedSince,
        string[] pagePaths,
        WikiTree tree,
        CancellationToken cancellationToken)
    {
        if (changedSince is { Length: > 0 } sha)
        {
            // The wiki root, not the repo root: git answers in paths relative to it, which is what the
            // plan keys on.
            var changed = await GitRepository.ChangedFilesSinceAsync(wikiRoot, sha, cancellationToken)
                .ConfigureAwait(false);

            return new ScopeResolution(PublishScope.ForFilesChangedSince(sha, changed), []);
        }

        if (pagePaths.Length == 0)
        {
            return new ScopeResolution(null, []);
        }

        var scope = PublishScope.ForPages(pagePaths);

        return new ScopeResolution(scope, scope.MissingFrom(tree.Pages.Select(page => page.Path)));
    }

    /// <summary>
    /// A <c>--page</c> path that names nothing, said with what the tree does hold — the answer a
    /// mistyped path needs is the spelling that would have worked.
    /// </summary>
    private static int FailUnknownPages(IReadOnlyList<string> unknown, WikiTree tree)
    {
        AnsiConsole.MarkupLine(
            $"[red]--page names {unknown.Count} path(s) that are not pages in this wiki:[/]");
        foreach (var path in unknown)
        {
            AnsiConsole.MarkupLine($"  [red]•[/] {path.EscapeMarkup()}");
        }

        AnsiConsole.MarkupLine(
            $"[grey]Paths are wiki-root-relative and case-sensitive. The tree has {tree.Pages.Count} "
            + "page(s):[/]");
        RenderPaths(tree.Pages.Select(page => page.Path));

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

        RenderScope(report);
        RenderApprovals(report);
        RenderOrphans(report);
        RenderFailures(report);
        RenderVerdict(report);
    }

    /// <summary>
    /// What the scope held back (§6.2 <c>--changed-since</c> / <c>--page</c>). Printed by every scoped
    /// run, including one that held nothing back: a filtered run that reads like a full run is this
    /// flag's failure mode, so the numbers always come with what narrowed them.
    /// </summary>
    private static void RenderScope(PublishReport report)
    {
        if (report.Scope is not { } scope)
        {
            return;
        }

        var excluded = report.ExcludedByScope;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[blue]SCOPE[/] — {scope.Description.EscapeMarkup()} put {scope.Paths.Count} path(s) in "
            + $"scope; {excluded.Count} page(s) a full run would have written are left alone");

        if (scope.Paths.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey]Nothing is in scope, so this run writes nothing.[/]");
        }

        RenderPaths(excluded.Select(page => page.Path));
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

        var scoped = report.Scope is { } scope
            ? $" Scope {scope.Description} held back {report.ExcludedByScope.Count} page(s)."
            : string.Empty;

        AnsiConsole.MarkupLine(
            $"[green]PLAN OK[/] — {report.Pages.Count} page(s) convert; "
            + $"{report.CreateCount + report.UpdateCount} body write(s), {report.UploadCount} upload(s)."
            + $"{scoped.EscapeMarkup()} Nothing was written.");
    }

    /// <summary>
    /// The page tree the run would build (<see cref="PageHierarchy"/>), which is the shape a human
    /// reviewing a bulk publish wants to check before it happens rather than after.
    /// </summary>
    private static void RenderTree(PublishReport report, string? rootPageId)
    {
        var root = rootPageId is { Length: > 0 } id
            ? $"confluence.rootPageId {id}"
            : "the space root (no confluence.rootPageId set)";

        var tree = new Tree($"[grey]{root.EscapeMarkup()}[/]");
        var nodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);

        // report.Pages is tree order, so a parent is always added before the children that look it up.
        foreach (var page in report.Pages)
        {
            var label = $"{page.Title.EscapeMarkup()} [grey]({page.Path.EscapeMarkup()})[/]";
            var parent = page.ParentPath is { } parentPath && nodes.TryGetValue(parentPath, out var node)
                ? node.AddNode(label)
                : tree.AddNode(label);

            nodes[page.Path] = parent;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(tree);
    }

    private static void RenderOutcome(PublishOutcome outcome, string? sha)
    {
        AnsiConsole.WriteLine();

        var table = new Table().AddColumn("Result").AddColumn("Pages");
        table.AddRow("[green]created[/]", outcome.CreatedCount.ToString());
        table.AddRow("[yellow]updated[/]", outcome.UpdatedCount.ToString());
        table.AddRow("[blue]attachments only[/]", outcome.AttachmentOnlyCount.ToString());
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"Attachments uploaded: {outcome.UploadedAttachmentCount}");

        if (outcome.ApprovalsRevokedCount > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Approvals revoked: {outcome.ApprovalsRevokedCount}[/] "
                + "(the `approved` label was removed and state moved to needs-review)");
        }

        if (sha is { Length: > 0 } && outcome.Succeeded)
        {
            AnsiConsole.MarkupLine($"[grey]lastPublishedSha: {sha.EscapeMarkup()}[/]");
        }

        foreach (var warning in outcome.Warnings.Take(PagesPerSection))
        {
            AnsiConsole.MarkupLine($"[yellow]•[/] {warning.EscapeMarkup()}");
        }

        if (outcome.Warnings.Count > PagesPerSection)
        {
            AnsiConsole.MarkupLine($"  [grey]… and {outcome.Warnings.Count - PagesPerSection} more warning(s)[/]");
        }

        RenderPageFailures(outcome);
        RenderOutcomeVerdict(outcome);
    }

    private static void RenderPageFailures(PublishOutcome outcome)
    {
        if (outcome.Failures.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[red]PAGES THAT FAILED[/] — {outcome.Failures.Count}");

        foreach (var failure in outcome.Failures.Take(PagesPerSection))
        {
            AnsiConsole.MarkupLine($"  [red]•[/] {failure.Path.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"    [grey]{failure.Message.EscapeMarkup()}[/]");
        }

        if (outcome.Failures.Count > PagesPerSection)
        {
            AnsiConsole.MarkupLine($"  [grey]… and {outcome.Failures.Count - PagesPerSection} more page(s)[/]");
        }
    }

    private static void RenderOutcomeVerdict(PublishOutcome outcome)
    {
        AnsiConsole.WriteLine();

        if (outcome.StoppedBecause is { } stopped)
        {
            AnsiConsole.MarkupLine($"[red]STOPPED[/] — {stopped.EscapeMarkup()}");
            return;
        }

        if (outcome.Failures.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]PARTIALLY PUBLISHED[/] — {outcome.Pages.Count} page(s) published, "
                + $"{outcome.Failures.Count} failed. Re-run to retry the failures.");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[green]PUBLISHED[/] — {outcome.Pages.Count} page(s) written, "
            + $"{outcome.UploadedAttachmentCount} attachment(s) uploaded.");
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

    /// <summary>
    /// The scope a run will use, plus the <c>--page</c> paths that matched nothing. Two fields rather
    /// than an exception: an unmatched path is an ordinary user mistake with a long answer, not a fault.
    /// </summary>
    private sealed record ScopeResolution(PublishScope? Scope, IReadOnlyList<string> UnknownPaths);
}
