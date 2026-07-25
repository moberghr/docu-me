using DocuMe.Core.Config;
using DocuMe.Core.Markdown;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;

namespace DocuMe.Core.Status;

/// <summary>Where the report's inputs came from, echoed so its output names its own sources.</summary>
/// <remarks>
/// Paths rather than a repo root, because <c>--config</c> and <c>--state</c> can each point outside
/// the tree and a reader comparing two status runs needs to know which files each one read.
/// </remarks>
public sealed record StatusPaths
{
    /// <summary>Absolute path of <c>docume.json</c>.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>Absolute path of the wiki root.</summary>
    public required string WikiRoot { get; init; }

    /// <summary>Absolute path of the state file, whether or not it exists.</summary>
    public required string StatePath { get; init; }

    /// <summary>Whether the state file was there when it was read.</summary>
    public bool StateFileExists { get; init; }
}

/// <summary>
/// Builds a <see cref="StatusReport"/> from the same four inputs a publish uses (PLAN.md §6.6).
/// Pure: no network, no clock, no write.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It plans the run itself rather than taking a plan.</strong>
/// <see cref="PublishPipeline.Plan"/> is what decides whether a page drifted, and status must not
/// answer that question a second way — two traversals or two hashing paths could disagree, and a
/// status report that contradicts the publish it is supposed to preview is worse than no report. It
/// also removes the one way a caller could lie by accident: a plan made with <c>--force</c> marks
/// every page as an update, and a plan made with a scope marks the pages it held back as skips, so
/// either would be reported as a wiki-wide drift or a wiki-wide calm that is not there. Planning here,
/// with fixed options, makes both unreachable.
/// </para>
/// <para>
/// <strong>No banner date.</strong> <see cref="PublishOptions.GeneratedOn"/> is left null: the report
/// reads hashes and actions, never the body a run would upload, and a command that reported a date
/// would be reading a clock for nothing.
/// </para>
/// </remarks>
public static class StatusModel
{
    /// <summary>Name of the check that answers "does the tree load, and does it hold pages?".</summary>
    public const string TreeCheck = "wiki tree";

    /// <summary>Name of the check that answers "does every page convert?" (§7).</summary>
    public const string ConverterCheck = "converter";

    /// <summary>Name of the check that answers §6.6's "state consistent with file tree?".</summary>
    public const string StateCheck = "state vs tree";

    /// <summary>Name of the check that answers "may this repo write to the target space?" (rule §1.4).</summary>
    public const string SpaceLockCheck = "space lock";

    /// <summary>
    /// Builds the report.
    /// </summary>
    /// <param name="paths">Where the inputs came from.</param>
    /// <param name="config">The loaded <c>docume.json</c> (§5.1).</param>
    /// <param name="tree">The loaded wiki tree.</param>
    /// <param name="state">
    /// The loaded <c>_meta/state.json</c> (§5.3), or <c>new DocumeState()</c> when there is no file —
    /// every page then reads as <see cref="StatusSync.Unpublished"/>, which is the truth.
    /// </param>
    /// <param name="environmentChecks">
    /// The checks only the caller can run because they touch the world: credentials, Node, the render
    /// script, the space probe (<see cref="StatusProbes"/>). Appended after the derived ones so the
    /// local half of the report is readable with no network at all.
    /// </param>
    public static StatusReport Build(
        StatusPaths paths,
        DocumeConfig config,
        WikiTree tree,
        DocumeState state,
        IReadOnlyList<StatusCheck>? environmentChecks = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(state);

        var report = PublishPipeline.Plan(config, tree, state);
        var urlBase = PageUrlBase(config.Confluence.BaseUrl, config.Confluence.SpaceKey);

        var pages = report.Pages
            .Select(page => MapPage(page, state, urlBase))
            .ToList();

        var checks = new List<StatusCheck>
        {
            Tree(paths.WikiRoot, report),
            Converter(report),
            StateConsistency(paths, report),
            SpaceLock(config.Confluence, report),
        };

        if (environmentChecks is { Count: > 0 })
        {
            checks.AddRange(environmentChecks);
        }

        return new StatusReport
        {
            ConfigPath = paths.ConfigPath,
            WikiRoot = paths.WikiRoot,
            StatePath = paths.StatePath,
            StateFileExists = paths.StateFileExists,
            SpaceKey = config.Confluence.SpaceKey,
            BaseUrl = config.Confluence.BaseUrl,
            RootPageId = config.Confluence.RootPageId,
            BaselineSha = state.BaselineSha,
            LastPublishedSha = state.LastPublishedSha,
            Pages = pages,
            Failures = report.Failures,
            Orphans = report.OrphanPages,
            Checks = checks,
            NotYetAvailable = Gaps(pages),
        };
    }

    /// <summary>
    /// The observation behind a planned action. <see cref="PagePublishAction.Skip"/> maps to
    /// <see cref="StatusSync.InSync"/> only because status plans without a scope — a scoped plan spells
    /// "held back" as a skip too, which is why <see cref="Build"/> owns the planning.
    /// </summary>
    public static StatusSync SyncOf(PagePublishAction action) => action switch
    {
        PagePublishAction.Create => StatusSync.Unpublished,
        PagePublishAction.Update => StatusSync.Drifted,
        PagePublishAction.UpdateAttachments => StatusSync.AttachmentsChanged,
        PagePublishAction.Move => StatusSync.Moved,
        PagePublishAction.Skip => StatusSync.InSync,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown publish action."),
    };

    private static StatusPage MapPage(PlannedPage page, DocumeState state, string? urlBase)
    {
        state.Pages.TryGetValue(page.Path, out var current);

        var pageId = current?.PageId;
        var approval = current?.Approval;
        var url = pageId is null || urlBase is null ? null : $"{urlBase}/{pageId}";

        // State spells "never published" as version 0; a 0 in a report reads like a real version.
        var version = current is { PublishedVersion: > 0 } ? current.PublishedVersion : (int?)null;

        return new StatusPage(
            page.Path,
            page.Title,
            SyncOf(page.Action),
            pageId,
            url,
            version,
            page.Attachments.Count,
            approval?.Status,
            approval?.ApprovedBy,
            approval?.ApprovedAt,
            approval?.ApprovedVersion,
            current?.Stale ?? false,
            page.Diagnostics.Count);
    }

    private static StatusCheck Tree(string wikiRoot, PublishReport report)
    {
        if (report.Pages.Count == 0 && report.Failures.Count == 0)
        {
            var empty = $"no markdown pages found under {wikiRoot}. Check wiki.root and wiki.exclude in "
                + "docume.json (§5.1).";

            return new StatusCheck(TreeCheck, StatusCheckOutcome.Warning, empty);
        }

        var refused = report.Failures.Count > 0 ? $", {report.Failures.Count} refused" : string.Empty;
        var detail = $"{report.Pages.Count} page(s) loaded from {wikiRoot}{refused}.";

        return new StatusCheck(TreeCheck, StatusCheckOutcome.Ok, detail);
    }

    private static StatusCheck Converter(PublishReport report)
    {
        if (report.Failures.Count > 0)
        {
            var refused = $"{report.Failures.Count} page(s) the converter refuses. No page publishes "
                + "until every page converts (§7) — the pages are listed below.";

            return new StatusCheck(ConverterCheck, StatusCheckOutcome.Problem, refused);
        }

        var degraded = report.DiagnosticCount > 0
            ? $" {report.DiagnosticCount} deliberate degradation(s) reported; `docume convert` groups them."
            : string.Empty;

        return new StatusCheck(ConverterCheck, StatusCheckOutcome.Ok, $"every page converts.{degraded}");
    }

    private static StatusCheck StateConsistency(StatusPaths paths, PublishReport report)
    {
        if (!paths.StateFileExists)
        {
            // Not a warning: a repo that has never published has no state file by definition, and
            // nothing in the tree contradicts it. The reason every row reads "unpublished" is worth
            // saying once, here, rather than leaving a reader to infer it from the table.
            var absent = $"no state file at {paths.StatePath} — nothing has been published from this "
                + "checkout, so every page reads as unpublished.";

            return new StatusCheck(StateCheck, StatusCheckOutcome.Ok, absent);
        }

        if (report.OrphanPages.Count > 0)
        {
            var orphans = $"{report.OrphanPages.Count} state entr(ies) name a markdown file the tree no "
                + "longer has. They are still pages in Confluence; `docume publish --prune` deletes them "
                + "after confirming (§6.2).";

            return new StatusCheck(StateCheck, StatusCheckOutcome.Warning, orphans);
        }

        var detail = $"state and the tree agree on {report.Pages.Count} page(s); no orphans.";

        return new StatusCheck(StateCheck, StatusCheckOutcome.Ok, detail);
    }

    private static StatusCheck SpaceLock(ConfluenceConfig confluence, PublishReport report)
    {
        if (report.WriteRefusal is { } refusal)
        {
            return new StatusCheck(SpaceLockCheck, StatusCheckOutcome.Warning, refusal);
        }

        var space = confluence.SpaceKey ?? "(confluence.spaceKey is not set)";
        var root = confluence.RootPageId is { Length: > 0 } id
            ? $"under page {id}"
            : "at the space root (no confluence.rootPageId)";
        var detail = $"space {space} is not protected; a publish would write {root}.";

        return new StatusCheck(SpaceLockCheck, StatusCheckOutcome.Ok, detail);
    }

    /// <summary>
    /// The dashboard columns (§6.5) this build cannot fill, and why. The feedback entry is
    /// unconditional because nothing here reads the inbox; the approvals entry is derived from the
    /// pages, so it stops being printed the moment a run records an approval.
    /// </summary>
    private static List<string> Gaps(IReadOnlyList<StatusPage> pages)
    {
        var gaps = new List<string>
        {
            // Says what is missing, not that the ingester is missing: `docume sync --comments` has
            // written the inbox since M4. What no build reads back is the inbox itself, and a gap note
            // that names the wrong absence sends a reader to fix something that already works.
            "open feedback per page: `docume sync --comments` (§6.3) ingests comments into the feedback "
            + "inbox, but neither this report nor the dashboard reads that inbox back. The column is "
            + "left out rather than printed as a zero nothing has looked for.",
        };

        // Only worth saying once something is published: on a pre-first-publish repo an empty approval
        // column is obvious, and the note would be noise on every run of a fresh repo.
        if (pages.Any(page => page.PageId is not null) && pages.All(page => page.Approval is null))
        {
            gaps.Add(
                "approvals: no published page carries an approval record, so every approval column is "
                + "empty. `docume sync --labels` (§6.3) is what reads the `approved` label into state — "
                + "until it runs, a page a reviewer HAS approved in Confluence still reads as unapproved "
                + "here.");
        }

        return gaps;
    }

    /// <summary>
    /// The prefix a page URL hangs off (§6.5's "link" column), or <c>null</c> when the config cannot
    /// produce one.
    /// </summary>
    /// <remarks>
    /// <c>/spaces/&lt;key&gt;/pages/&lt;id&gt;</c> without the title slug Confluence appends in a
    /// browser: the id is what resolves the page, the slug is decoration, and building one from a title
    /// would be a second escaping problem for no gain.
    /// </remarks>
    private static string? PageUrlBase(string? baseUrl, string? spaceKey)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(spaceKey)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return $"{uri.ToString().TrimEnd('/')}/spaces/{Uri.EscapeDataString(spaceKey)}/pages";
    }
}
