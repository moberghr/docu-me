using System.Net;
using DocuMe.Core.Confluence;
using DocuMe.Core.State;

namespace DocuMe.Core.Publishing;

/// <summary>
/// Asks a human whether to delete these pages (PLAN.md §6.2 "Orphans", rule §9.6).
/// </summary>
/// <param name="pagePaths">The orphans about to be trashed, in delete order.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns><c>true</c> to delete them, <c>false</c> to leave every one of them alone.</returns>
/// <remarks>
/// A delegate rather than a Spectre prompt in the executor, matching <see cref="DiagramRenderer"/>: it
/// keeps the whole of <c>--prune</c> testable offline — including the case where the human says no, which
/// is the answer that matters — and leaves the prompt in the CLI where the terminal is.
/// </remarks>
public delegate Task<bool> PruneConfirmation(
    IReadOnlyList<string> pagePaths,
    CancellationToken cancellationToken);

/// <summary>
/// What a <c>--prune</c> did (PLAN.md §6.2 "Orphans").
/// </summary>
/// <param name="State">
/// State with every deleted page's entry dropped. The caller persists it, including after a failure: an
/// entry kept for a page that is now in the trash would plan as an update against a page that is gone.
/// </param>
/// <param name="StateChanged">Whether <paramref name="State"/> differs from what was passed in.</param>
/// <param name="Deleted">The pages trashed, in the order they were deleted.</param>
/// <param name="Failures">Deletes that failed. The first one stops the run — see <see cref="PruneExecutor"/>.</param>
/// <param name="Warnings">
/// Things worth saying that failed nothing: a page already gone from Confluence, a state entry that
/// named no page at all.
/// </param>
/// <param name="StoppedBecause">Why the run stopped before finishing, or <c>null</c>.</param>
/// <param name="Confirmed">
/// Whether the human said yes. <c>false</c> with no failures is a complete, successful run in which
/// nothing was deleted — declining is an answer, not an error.
/// </param>
public sealed record PruneOutcome(
    DocumeState State,
    bool StateChanged,
    IReadOnlyList<PlannedPrune> Deleted,
    IReadOnlyList<PagePublishFailure> Failures,
    IReadOnlyList<string> Warnings,
    string? StoppedBecause,
    bool Confirmed)
{
    /// <summary>True when nothing failed and nothing cut the run short.</summary>
    public bool Succeeded => Failures.Count == 0 && StoppedBecause is null;
}

/// <summary>
/// Executes a <see cref="PrunePlan"/>: confirms with a human, trashes each orphan deepest-first, and
/// drops each entry from state as it goes (PLAN.md §6.2 "Orphans", rule §9.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It re-decides nothing and it asks once.</strong> Which orphans are deletable, and in which
/// order, was settled offline by <see cref="PrunePlanner"/>; the confirmation covers the whole list,
/// because a per-page prompt over 40 orphans is a prompt nobody reads.
/// </para>
/// <para>
/// <strong>A failed delete stops the run.</strong> The order is a dependency chain — children before the
/// pages they hang under — so carrying on past a failure could trash a parent whose child is still there,
/// which is precisely the reparenting the planner refuses to cause. One failure therefore ends the prune
/// with the remaining orphans untouched and still reported by the next run. It is the opposite call from
/// <see cref="PublishExecutor"/>, which reports every failing page in one pass, and for the opposite
/// reason: an upsert that fails leaves the page as it was, while a delete out of order moves pages nobody
/// asked to move.
/// </para>
/// <para>
/// <strong>State is dropped only after Confluence confirms.</strong> A failed delete keeps its entry, so
/// a re-run retries it; a 404 counts as done, because "the page is gone" is the state the step exists to
/// produce.
/// </para>
/// </remarks>
public sealed class PruneExecutor
{
    private readonly ConfluenceClient _client;

    /// <summary>Initializes a new instance of the <see cref="PruneExecutor"/> class.</summary>
    /// <param name="client">The Confluence client, already carrying credentials and the retry pipeline.</param>
    public PruneExecutor(ConfluenceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
    }

    /// <summary>
    /// Executes <paramref name="plan"/> after <paramref name="confirm"/> agrees to it.
    /// </summary>
    /// <param name="plan">The plan, from <see cref="PrunePlanner.Plan"/>.</param>
    /// <param name="state">State as the publish left it. Never mutated: the outcome carries the new value.</param>
    /// <param name="confirm">
    /// The interactive confirmation §6.2 requires. Called exactly once, and only when there is something
    /// to delete.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PruneOutcome> PruneAsync(
        PrunePlan plan,
        DocumeState state,
        PruneConfirmation confirm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(confirm);

        var original = state;
        var deleted = new List<PlannedPrune>();
        var failures = new List<PagePublishFailure>();
        var warnings = new List<string>();

        PruneOutcome Outcome(string? stoppedBecause, bool confirmed) => new(
            state,
            !ReferenceEquals(state, original),
            deleted,
            failures,
            warnings,
            stoppedBecause,
            confirmed);

        if (plan.IsEmpty)
        {
            // Nothing to delete is not a refusal, and prompting for it would train the operator to say
            // yes without reading.
            return Outcome(null, confirmed: false);
        }

        var paths = plan.Pages.Select(page => page.Path).ToArray();
        if (!await confirm(paths, cancellationToken).ConfigureAwait(false))
        {
            return Outcome(null, confirmed: false);
        }

        foreach (var page in plan.Pages)
        {
            // Returned, not thrown, for the reason every other stop here returns: the entries already
            // dropped describe pages that are in the trash, and state has to keep that.
            if (cancellationToken.IsCancellationRequested)
            {
                return Outcome(Cancelled(page.Path, deleted.Count), confirmed: true);
            }

            if (page.PageId is not { Length: > 0 } pageId)
            {
                warnings.Add(
                    $"{page.Path} had no pageId in state, so there was nothing to delete in Confluence; "
                    + "the stale entry was dropped.");
                state = StateUpdates.RemovePage(state, page.Path);
                continue;
            }

            try
            {
                await _client.DeletePageAsync(pageId, cancellationToken).ConfigureAwait(false);
            }
            catch (ConfluenceApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Someone deleted it by hand. That is the state this step produces, so it counts as done
                // and the entry still has to go.
                warnings.Add(
                    $"{page.Path} was already gone from Confluence (page {pageId}); its state entry was "
                    + "dropped.");
                state = StateUpdates.RemovePage(state, page.Path);
                continue;
            }
            catch (ConfluenceAuthenticationException ex)
            {
                // Never retried, never worked around (rule §1.2). A token that can read and edit but not
                // delete lands here too, which is worth saying plainly.
                failures.Add(new PagePublishFailure(page.Path, ex.Message));

                return Outcome(
                    $"Confluence refused the credentials on the delete, so the prune stopped at "
                    + $"'{page.Path}' with {deleted.Count} page(s) deleted. Deleting a page needs more "
                    + "permission than editing one; check the account can delete in this space.",
                    confirmed: true);
            }
            catch (ConfluenceException ex)
            {
                failures.Add(new PagePublishFailure(page.Path, ex.Message));

                return Outcome(Stopped(page.Path, deleted.Count), confirmed: true);
            }
            catch (HttpRequestException ex)
            {
                failures.Add(new PagePublishFailure(page.Path, Unreachable(ex)));

                return Outcome(Stopped(page.Path, deleted.Count), confirmed: true);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(new PagePublishFailure(page.Path, TimedOut(ex)));

                return Outcome(Stopped(page.Path, deleted.Count), confirmed: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Outcome(Cancelled(page.Path, deleted.Count), confirmed: true);
            }

            deleted.Add(page);
            state = StateUpdates.RemovePage(state, page.Path);
        }

        return Outcome(null, confirmed: true);
    }

    /// <summary>
    /// What a Ctrl-C says. A delete that was in flight when the token tripped may or may not have reached
    /// Confluence, which is worth saying plainly: it is the one page whose fate this outcome cannot state.
    /// </summary>
    private static string Cancelled(string path, int deletedCount) =>
        $"the prune was cancelled at '{path}' with {deletedCount} page(s) deleted. Those deletes are "
        + "recorded in state; a delete already in flight may still have completed, and the next run "
        + "reports whatever is still there.";

    private static string Stopped(string path, int deletedCount) =>
        $"the prune stopped at '{path}' with {deletedCount} page(s) deleted. Deletes run deepest-first, "
        + "so the orphans after it include the pages this one hangs under, and trashing a parent whose "
        + "child is still there would move that child. They were left alone and the next run reports them "
        + "again.";

    private static string Unreachable(Exception ex) =>
        $"Confluence could not be reached: {ex.Message} The request was already retried with backoff, so "
        + "check confluence.baseUrl and the network rather than re-running immediately.";

    private static string TimedOut(Exception ex) =>
        $"Confluence did not answer before the client timeout ran out: {ex.Message}";
}
