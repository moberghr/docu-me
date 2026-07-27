using System.Net;
using DocuMe.Core.Confluence;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using Shouldly;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// The <c>--prune</c> write path (PLAN.md §6.2 "Orphans", rule §9.6) against a real local HTTP server
/// (.claude/rules/testing.md §4.2), so no Confluence account is involved in verifying the one verb in
/// the tool that deletes.
/// </summary>
public sealed class PruneExecutorTests
{
    private const string Email = "bot@example.com";
    private const string ApiToken = "very-secret-token";

    private static readonly ConfluenceCredentials Credentials = new(Email, ApiToken);

    /// <summary>The paths the confirmation delegate was shown, so a test can assert what it was asked.</summary>
    private readonly List<IReadOnlyList<string>> _confirmations = [];

    /// <summary>
    /// The happy path: every orphan trashed deepest-first with a bare <c>DELETE</c>, and its state entry
    /// dropped only after Confluence answered.
    /// </summary>
    [Fact]
    public async Task Deletes_every_orphan_deepest_first_and_drops_its_state_entry()
    {
        using var server = WireMockServer.Start();
        StubDelete(server);

        var state = State(
            ("a/README.md", "10", "1"),
            ("a/gone.md", "20", "10"),
            ("kept.md", "30", "1"));

        var plan = PrunePlanner.Plan(state, ["a/README.md", "a/gone.md"]);
        var outcome = await PruneAsync(server, plan, state, confirm: true);

        outcome.Succeeded.ShouldBeTrue();
        outcome.Confirmed.ShouldBeTrue();
        outcome.Deleted.Select(page => page.Path).ShouldBe(["a/gone.md", "a/README.md"]);

        // Child before parent, on the wire and not only in the plan.
        var deletes = server.LogEntries.Select(entry => entry.RequestMessage!).ToArray();
        deletes.Select(request => request.Path).ShouldBe(["/wiki/api/v2/pages/20", "/wiki/api/v2/pages/10"]);
        deletes.ShouldAllBe(request => request.Method == "DELETE");

        outcome.StateChanged.ShouldBeTrue();
        outcome.State.Pages.Keys.ShouldBe(["kept.md"]);

        // The human is asked once, about the whole list: a per-page prompt over 40 orphans is a prompt
        // nobody reads.
        _confirmations.ShouldHaveSingleItem().ShouldBe(["a/gone.md", "a/README.md"]);
    }

    /// <summary>
    /// Declining is an answer, not an error: nothing is deleted, no state entry moves, and the run is
    /// still a success.
    /// </summary>
    [Fact]
    public async Task Saying_no_deletes_nothing_and_still_succeeds()
    {
        using var server = WireMockServer.Start();
        StubDelete(server);

        var state = State(("a/gone.md", "20", "1"));
        var plan = PrunePlanner.Plan(state, ["a/gone.md"]);

        var outcome = await PruneAsync(server, plan, state, confirm: false);

        outcome.Succeeded.ShouldBeTrue();
        outcome.Confirmed.ShouldBeFalse();
        outcome.Deleted.ShouldBeEmpty();
        outcome.StateChanged.ShouldBeFalse();
        outcome.State.Pages.Keys.ShouldBe(["a/gone.md"]);
        server.LogEntries.ShouldBeEmpty();
    }

    /// <summary>
    /// Nothing to delete must not prompt. Asking a human to confirm an empty list trains them to say yes
    /// without reading, which is the opposite of what the confirmation is for.
    /// </summary>
    [Fact]
    public async Task An_empty_plan_never_asks_and_never_calls_confluence()
    {
        using var server = WireMockServer.Start();
        StubDelete(server);

        var state = State(("kept.md", "10", "1"));
        var outcome = await PruneAsync(server, PrunePlanner.Plan(state, []), state, confirm: true);

        outcome.Succeeded.ShouldBeTrue();
        outcome.Confirmed.ShouldBeFalse();
        _confirmations.ShouldBeEmpty();
        server.LogEntries.ShouldBeEmpty();
    }

    /// <summary>
    /// A page a human already deleted is the state this step exists to produce, so it counts as done — but
    /// it is said out loud, because "already gone" and "deleted by this run" are different facts.
    /// </summary>
    [Fact]
    public async Task A_page_already_gone_from_confluence_counts_as_done()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound).WithBody("{}"));

        var state = State(("a/gone.md", "20", "1"));
        var outcome = await PruneAsync(server, PrunePlanner.Plan(state, ["a/gone.md"]), state, confirm: true);

        outcome.Succeeded.ShouldBeTrue();
        outcome.Deleted.ShouldBeEmpty();
        outcome.State.Pages.ShouldBeEmpty();
        outcome.Warnings.ShouldHaveSingleItem().ShouldContain("already gone");
    }

    /// <summary>
    /// A state entry that names no page has nothing to delete: the stale entry goes and Confluence is
    /// never called, which keeps a bookkeeping fix from looking like a delete.
    /// </summary>
    [Fact]
    public async Task An_entry_with_no_page_id_is_dropped_without_a_request()
    {
        using var server = WireMockServer.Start();
        StubDelete(server);

        var state = State(("a/gone.md", null, null));
        var outcome = await PruneAsync(server, PrunePlanner.Plan(state, ["a/gone.md"]), state, confirm: true);

        outcome.Succeeded.ShouldBeTrue();
        outcome.State.Pages.ShouldBeEmpty();
        outcome.Warnings.ShouldHaveSingleItem().ShouldContain("no pageId");
        server.LogEntries.ShouldBeEmpty();
    }

    /// <summary>
    /// Deleting needs more permission than editing, so a token that published happily can still be refused
    /// here. Never retried, never worked around (rule §1.2), and the message says which permission.
    /// </summary>
    [Fact]
    public async Task A_token_that_cannot_delete_stops_the_prune()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Forbidden));

        var state = State(("a/gone.md", "20", "1"), ("b/gone.md", "30", "1"));
        var plan = PrunePlanner.Plan(state, ["a/gone.md", "b/gone.md"]);

        var outcome = await PruneAsync(server, plan, state, confirm: true);

        outcome.Succeeded.ShouldBeFalse();
        outcome.StoppedBecause.ShouldNotBeNull();
        outcome.StoppedBecause.ShouldContain("permission");
        outcome.Failures.ShouldHaveSingleItem().Path.ShouldBe("a/gone.md");

        // Neither entry is dropped, and the second page was never attempted.
        outcome.State.Pages.Count.ShouldBe(2);
        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// The reason a failure stops the whole prune rather than being collected like a failed publish: the
    /// order is a dependency chain, so carrying on could trash a parent whose child is still there —
    /// exactly the reparenting <see cref="PrunePlanner"/> refuses to cause.
    /// </summary>
    [Fact]
    public async Task A_failed_delete_stops_the_pages_above_it()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages/20").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.BadRequest).WithBody("{}"));
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages/10").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));

        var state = State(("a/README.md", "10", "1"), ("a/gone.md", "20", "10"));
        var plan = PrunePlanner.Plan(state, ["a/README.md", "a/gone.md"]);

        var outcome = await PruneAsync(server, plan, state, confirm: true);

        outcome.Succeeded.ShouldBeFalse();
        outcome.Deleted.ShouldBeEmpty();
        outcome.Failures.ShouldHaveSingleItem().Path.ShouldBe("a/gone.md");
        outcome.StoppedBecause!.ShouldContain("deepest-first");

        // The parent was never asked for, which is the whole point.
        server.LogEntries.Count.ShouldBe(1);
        outcome.State.Pages.Count.ShouldBe(2);
    }

    /// <summary>
    /// A page trashed before a later one failed keeps its state entry dropped: the page is in the trash,
    /// and an entry claiming otherwise would plan as an update against a page that is gone.
    /// </summary>
    [Fact]
    public async Task State_keeps_the_deletes_that_did_happen_before_a_failure()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages/20").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages/10").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.BadRequest).WithBody("{}"));

        var state = State(("a/README.md", "10", "1"), ("a/gone.md", "20", "10"));
        var plan = PrunePlanner.Plan(state, ["a/README.md", "a/gone.md"]);

        var outcome = await PruneAsync(server, plan, state, confirm: true);

        outcome.Succeeded.ShouldBeFalse();
        outcome.Deleted.Select(page => page.Path).ShouldBe(["a/gone.md"]);
        outcome.StateChanged.ShouldBeTrue();
        outcome.State.Pages.Keys.ShouldBe(["a/README.md"]);
    }

    /// <summary>
    /// Ctrl-C between deletes stops the prune the way a failed delete does — an outcome carrying the
    /// state the run had already produced, not an exception that throws it away.
    /// </summary>
    /// <remarks>
    /// The deletes run deepest-first and each drops its state entry as Confluence confirms it, so a
    /// cancellation that escaped <c>PruneAsync</c> would leave <c>state.json</c> still listing pages that
    /// are in the trash. The next run then plans them as updates, finds them gone, and recreates the very
    /// orphans this command was told to remove.
    /// </remarks>
    [Fact]
    public async Task Cancelling_stops_the_prune_and_returns_the_state_it_had_reached()
    {
        using var server = WireMockServer.Start();
        StubDelete(server);

        using var cancellation = new CancellationTokenSource();

        var state = State(("a/README.md", "10", "1"), ("a/gone.md", "20", "10"));
        var plan = PrunePlanner.Plan(state, ["a/README.md", "a/gone.md"]);

        // Answering the prompt and then cancelling is the shape Ctrl-C takes here: the confirmation is the
        // last thing before the first delete, so nothing has been trashed yet.
        var outcome = await PruneAsync(
            server,
            plan,
            state,
            confirm: true,
            cancellation: cancellation,
            cancelAtConfirmation: true);

        outcome.Succeeded.ShouldBeFalse();
        outcome.StoppedBecause.ShouldNotBeNull();
        outcome.StoppedBecause.ShouldContain("cancelled");
        outcome.Confirmed.ShouldBeTrue();

        // Nothing was deleted, so nothing may be dropped from state — and no DELETE was sent.
        outcome.Deleted.ShouldBeEmpty();
        outcome.StateChanged.ShouldBeFalse();
        outcome.State.Pages.Keys.ShouldBe(["a/README.md", "a/gone.md"], ignoreOrder: true);
        server.LogEntries.ShouldBeEmpty();
    }

    /// <summary>
    /// Cancelling once the deletes are under way keeps the entries the run had already dropped: those
    /// pages are gone, and state claiming otherwise would plan an update against a page in the trash.
    /// </summary>
    /// <remarks>
    /// Whether the token trips the in-flight <c>DELETE</c> or is seen at the next page's turn is timing the
    /// test does not pin, and does not need to: both are cancellation, both come back as an outcome, and
    /// the entries dropped before either are the same. What is asserted is what holds on both paths.
    /// </remarks>
    [Fact]
    public async Task Cancelling_mid_delete_keeps_the_entries_already_dropped()
    {
        using var cancellation = new CancellationTokenSource();
        using var server = WireMockServer.Start();
        StubDelete(server);
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages/30").UsingDelete())
            .AtPriority(-1)
            .RespondWith(Response.Create().WithCallback(_ =>
            {
                cancellation.Cancel();

                return new ResponseMessage { StatusCode = (int)HttpStatusCode.NoContent };
            }));

        // a/aaa.md carries no pageId, so it is dropped from state before any request is sent — the
        // bookkeeping this run had already done by the time the cancellation arrives. It sorts ahead of
        // the page whose delete cancels, which is what puts it on the done side of the interruption.
        var state = State(
            ("a/README.md", "10", "1"),
            ("a/aaa.md", null, "10"),
            ("a/bbb.md", "30", "10"));

        var plan = PrunePlanner.Plan(state, ["a/README.md", "a/aaa.md", "a/bbb.md"]);
        var outcome = await PruneAsync(server, plan, state, confirm: true, cancellation: cancellation);

        outcome.Succeeded.ShouldBeFalse();
        outcome.StoppedBecause.ShouldNotBeNull();
        outcome.StoppedBecause.ShouldContain("cancelled");

        outcome.StateChanged.ShouldBeTrue();
        outcome.State.Pages.ShouldNotContainKey("a/aaa.md");
        outcome.State.Pages.ShouldContainKey("a/README.md");
    }

    private static void StubDelete(WireMockServer server) =>
        server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));

    private static DocumeState State(params (string Path, string? PageId, string? Parent)[] pages) =>
        new()
        {
            Pages = pages.ToDictionary(
                page => page.Path,
                page => new PageState { PageId = page.PageId, ParentPageId = page.Parent },
                StringComparer.Ordinal),
        };

    private async Task<PruneOutcome> PruneAsync(
        WireMockServer server,
        PrunePlan plan,
        DocumeState state,
        bool confirm,
        CancellationTokenSource? cancellation = null,
        bool cancelAtConfirmation = false)
    {
        var options = new ConfluenceClientOptions
        {
            BaseUrl = new Uri($"{server.Url}/wiki"),
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.FromMilliseconds(1),
            Timeout = TimeSpan.FromSeconds(30),
        };

        using var client = ConfluenceClient.Create(options, Credentials);

        return await new PruneExecutor(client).PruneAsync(
            plan,
            state,
            async (paths, _) =>
            {
                _confirmations.Add(paths);

                if (cancelAtConfirmation && cancellation is not null)
                {
                    await cancellation.CancelAsync();
                }

                return confirm;
            },
            cancellation?.Token ?? TestContext.Current.CancellationToken);
    }
}
