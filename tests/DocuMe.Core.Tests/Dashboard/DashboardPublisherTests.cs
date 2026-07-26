using System.Net;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Dashboard;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Dashboard;

/// <summary>
/// The dashboard upsert (PLAN.md §6.5) against a real local HTTP server (.claude/rules/testing.md §4.2).
/// </summary>
/// <remarks>
/// Pinned here rather than left to a smoke run because two commands share it: <c>dashboard</c> publishes
/// the page and <c>drift --mark</c> refreshes it (§6.4). The behavior worth asserting is the request
/// <em>count</em> as much as the outcome — the whole point of the unchanged branch is that no version is
/// spent, and only the request log proves that.
/// </remarks>
public sealed class DashboardPublisherTests
{
    private const string SpaceKey = "DOCUMESBX";
    private const string SpaceId = "98304";
    private const string PageId = "300001";
    private const string Title = "Documentation Status";
    private const string RootPageId = "131074";

    private const string PagesPath = "/wiki/api/v2/pages";
    private const string SpacesPath = "/wiki/api/v2/spaces";

    /// <summary>
    /// The query parameter that decides whether Confluence sends a page body at all
    /// (<c>ConfluenceClient.BodyFormat</c>). The skip-if-unchanged branch is only reachable when the read
    /// carries it.
    /// </summary>
    private const string BodyFormatParam = "body-format";
    private const string BodyFormatValue = "storage";

    private static readonly ConfluenceCredentials Credentials = new("bot@example.com", "token");

    [Fact]
    public async Task Creates_the_page_when_no_page_carries_the_title()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubTitleSearch(server, existing: null);
        server
            .Given(Request.Create().WithPath(PagesPath).UsingPost())
            .RespondWith(Json("""
                {"id":"300001","title":"Documentation Status","spaceId":"98304","version":{"number":1}}
                """));

        using var client = CreateClient(server);
        var result = await DashboardPublisher.UpsertAsync(
            client,
            Confluence(),
            SpaceKey,
            Title,
            Body("first run", "2026-08-02 07:00 UTC"),
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(DashboardUpsertOutcome.Created);
        result.PageId.ShouldBe(PageId);
        result.Version.ShouldBe(1);

        // §6.5's page hangs off the configured root like any other, so a fresh space gets a filed page
        // rather than one loose at the space root.
        RequestBody(server, "POST").ShouldContain(RootPageId);
    }

    [Fact]
    public async Task Spends_no_version_when_only_the_provenance_line_differs()
    {
        // The cron case, and the reason §6.5's "full overwrite each run" has one documented deviation:
        // §6.4 refreshes this page on every `drift --mark`, and a version per run would bury the page's
        // real history under no-op revisions.
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubTitleSearch(server, existing: Body("same data", "2026-08-01 06:00 UTC"), version: 4);

        using var client = CreateClient(server);
        var result = await DashboardPublisher.UpsertAsync(
            client,
            Confluence(),
            SpaceKey,
            Title,
            Body("same data", "2026-08-02 07:00 UTC"),
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(DashboardUpsertOutcome.Unchanged);
        result.PageId.ShouldBe(PageId);
        result.Version.ShouldBe(4);

        // The assertion that matters: nothing was written, not merely that the outcome says so.
        server.LogEntries.ShouldAllBe(entry => entry.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task Asks_for_the_body_its_skip_compares_against()
    {
        // The precondition every other assertion in this class rests on, and the one that used to be
        // untestable here: the stub matched on path alone, so it served a body whether or not the caller
        // requested one. Flipping this call's `includeBody` to false left all 1317 tests green while
        // shipping a dashboard that compares its render against nothing, never skips, and spends a page
        // version on every `drift --mark` forever — the exact churn §6.5's one documented deviation exists
        // to prevent.
        //
        // RemoteBodyReadTests guards the other direction (rule §9.1: no *second* caller may read a body).
        // Its scan counts the token `includeBody`, not the value, so `includeBody: false` still reads as
        // one opt-in in the one allowed file. Presence is executable where absence is not, so it is
        // asserted on the wire here rather than by a source scan there.
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubTitleSearch(server, existing: Body("same data", "2026-08-01 06:00 UTC"), version: 4);

        // The write is stubbed although a passing run never reaches it. Without it, a regression makes
        // this test die on an unstubbed-PUT 404 before the assertion below runs, and the failure names a
        // 404 instead of the missing query parameter — cover that reads as a cascade rather than a cause.
        StubUpdate(server, version: 5);

        using var client = CreateClient(server);
        var result = await DashboardPublisher.UpsertAsync(
            client,
            Confluence(),
            SpaceKey,
            Title,
            Body("same data", "2026-08-02 07:00 UTC"),
            TestContext.Current.CancellationToken);

        const string unasked = "The dashboard's title lookup did not ask Confluence for the page body. "
            + "The read exists only so an unchanged page can be left alone; without body-format=storage "
            + "the API answers without one, every render compares unequal, and the page takes a no-op "
            + "version per run.";

        TitleSearchUrl(server).ShouldContain($"{BodyFormatParam}={BodyFormatValue}", customMessage: unasked);

        // And the skip really did fire off the body that arrived, so the query above is load-bearing
        // rather than a string that happens to be present.
        result.Outcome.ShouldBe(DashboardUpsertOutcome.Unchanged, unasked);
    }

    [Fact]
    public async Task Writes_a_version_when_the_data_above_the_provenance_line_moved()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubTitleSearch(server, existing: Body("one page stale", "2026-08-01 06:00 UTC"), version: 4);
        StubUpdate(server, version: 5);

        using var client = CreateClient(server);
        var result = await DashboardPublisher.UpsertAsync(
            client,
            Confluence(),
            SpaceKey,
            Title,
            Body("two pages stale", "2026-08-02 07:00 UTC"),
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(DashboardUpsertOutcome.Updated);
        result.Version.ShouldBe(5);

        // v5 = the read's v4 + 1, and the message says which command wrote it: a page's history is what a
        // reviewer reads to tell a machine refresh from a human edit.
        var body = RequestBody(server, "PUT");
        body.ShouldContain("\"number\":5");
        body.ShouldContain(DashboardPublisher.VersionMessage);
    }

    [Fact]
    public async Task The_write_carries_the_render_it_was_given_not_the_body_it_read()
    {
        // Rule §9.1's executable half (see Confluence.RemoteBodyReadTests): this is the product's only
        // page-body read, so it is the only place a hand edit in Confluence could survive a republish. The
        // stored body is poisoned with a sentence no renderer produces; a write that merges, appends or
        // falls back to what it read carries the sentence, and the check above — version number and
        // message — would not notice.
        const string sentinel = "NOTE from a reviewer";
        const string handEdit = $"<p>{sentinel}: keep this paragraph, we edited it here.</p>";

        using var server = WireMockServer.Start();
        StubSpace(server);
        StubTitleSearch(server, existing: Body("one page stale", "2026-08-01 06:00 UTC") + handEdit, version: 4);
        StubUpdate(server, version: 5);

        using var client = CreateClient(server);
        var rendered = Body("two pages stale", "2026-08-02 07:00 UTC");
        var result = await DashboardPublisher.UpsertAsync(
            client,
            Confluence(),
            SpaceKey,
            Title,
            rendered,
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(DashboardUpsertOutcome.Updated);

        // The poison has to have been served, or everything below passes vacuously — and `Updated` does
        // not prove it: a lookup that answers with no body at all is Updated too
        // (A_body_Confluence_did_not_return_counts_as_changed). This is the executable half of §9.1 for
        // the dashboard, so a silent stub drift here would retire the rule's proof without failing
        // anything.
        const string unserved = "The fake Confluence never answered the title lookup with the hand edit, "
            + "so this test is exercising nothing. Fix the stub before trusting it.";

        ServedTitleSearch(server).ShouldContain(sentinel, customMessage: unserved);

        const string survived = "The write echoes back part of the body it read, so a hand edit in "
            + "Confluence now survives a republish and the page has two sources of truth (rule §9.1).";

        var body = RequestBody(server, "PUT");
        body.ShouldNotContain(sentinel, Case.Sensitive, survived);

        // Not merely "the sentence is gone": the whole body is the render, so nothing else leaked either.
        JsonDocument.Parse(body).RootElement
            .GetProperty("body").GetProperty("storage").GetProperty("value").GetString()
            .ShouldBe(rendered);
    }

    [Fact]
    public async Task A_body_Confluence_did_not_return_counts_as_changed()
    {
        // A page a human rewrote by hand, or one the API answered without a body: overwritten, which is
        // what §6.5's "machine-owned" means. Never skipped on a guess.
        using var server = WireMockServer.Start();
        StubSpace(server);
        server
            .Given(Request.Create().WithPath(PagesPath).UsingGet())
            .RespondWith(Json("""
                {"results":[{"id":"300001","title":"Documentation Status","spaceId":"98304",
                "version":{"number":9}}],"_links":{}}
                """));
        StubUpdate(server, version: 10);

        using var client = CreateClient(server);
        var result = await DashboardPublisher.UpsertAsync(
            client,
            Confluence(),
            SpaceKey,
            Title,
            Body("data", "2026-08-02 07:00 UTC"),
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(DashboardUpsertOutcome.Updated);
        result.Version.ShouldBe(10);
    }

    [Fact]
    public async Task A_space_key_that_resolves_to_nothing_is_reported_rather_than_thrown()
    {
        // `drift --mark` has already written labels by the time it refreshes, so it decides for itself
        // whether a missing space ends the run.
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(SpacesPath).UsingGet())
            .RespondWith(Json("""{"results":[],"_links":{}}"""));

        using var client = CreateClient(server);
        var result = await DashboardPublisher.UpsertAsync(
            client,
            Confluence(),
            SpaceKey,
            Title,
            Body("data", "2026-08-02 07:00 UTC"),
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(DashboardUpsertOutcome.SpaceNotFound);
        result.PageId.ShouldBeNull();

        // Nothing was looked for and nothing written: the space lookup is the whole run.
        server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_configured_space_id_spends_no_request_confirming_itself()
    {
        using var server = WireMockServer.Start();
        StubTitleSearch(server, existing: Body("data", "2026-08-02 07:00 UTC"), version: 2);

        using var client = CreateClient(server);
        var result = await DashboardPublisher.UpsertAsync(
            client,
            Confluence(spaceId: SpaceId),
            SpaceKey,
            Title,
            Body("data", "2026-08-02 07:00 UTC"),
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(DashboardUpsertOutcome.Unchanged);

        // One request, the title search. The config is committed and reviewed, so confirming its spaceId
        // would cost a request per run to learn nothing.
        var request = server.LogEntries.Single().RequestMessage;
        request.ShouldNotBeNull();
        request!.Path.ShouldBe(PagesPath);
    }

    /// <summary>A body shaped like a real one: data, then the provenance line that carries the instant.</summary>
    private static string Body(string rows, string generatedAt) =>
        $"<p>{rows}</p><p><em>Generated by DocuMe at {generatedAt}</em></p>";

    private static ConfluenceConfig Confluence(string? spaceId = null) => new()
    {
        BaseUrl = "https://example.atlassian.net/wiki",
        SpaceKey = SpaceKey,
        SpaceId = spaceId,
        RootPageId = RootPageId,
    };

    private static void StubSpace(WireMockServer server) => server
        .Given(Request.Create().WithPath(SpacesPath).UsingGet())
        .RespondWith(Json("""
            {"results":[{"id":"98304","key":"DOCUMESBX","name":"DocuMe Sandbox"}],"_links":{}}
            """));

    /// <summary>
    /// The title lookup, answered the way Confluence answers it: the body comes back only when the
    /// request asked for it with <c>body-format=storage</c>.
    /// </summary>
    /// <remarks>
    /// A stub matching on path alone would serve the body whether or not the caller requested it, and the
    /// skip-if-unchanged branch below would then pass while shipping a build that never skips anything —
    /// measured, and green across all 1317 tests. The two registrations are what make the request's query
    /// string load-bearing rather than decorative; <see cref="Asks_for_the_body_its_skip_compares_against"/>
    /// names the cause so the rest fail as a cascade rather than as the whole story.
    /// </remarks>
    private static void StubTitleSearch(WireMockServer server, string? existing, int version = 1)
    {
        var withoutBody = "{\"id\":\"" + PageId + "\",\"title\":\"" + Title + "\",\"spaceId\":\"" + SpaceId
            + "\",\"parentId\":\"" + RootPageId + "\",\"version\":{\"number\":" + version + "}";

        var results = existing is null
            ? "[]"
            : withoutBody + ",\"body\":{\"storage\":{\"value\":" + JsonSerializer.Serialize(existing)
                + ",\"representation\":\"storage\"}}}";

        if (existing is not null)
        {
            results = "[" + results + "]";
        }

        server
            .Given(Request.Create()
                .WithPath(PagesPath)
                .UsingGet()
                .WithParam(BodyFormatParam, BodyFormatValue))
            .AtPriority(1)
            .RespondWith(Json("{\"results\":" + results + ",\"_links\":{}}"));

        // The same page, answered to a caller that did not ask for a body. Confluence omits the key
        // entirely rather than sending it empty.
        var bodyless = existing is null ? "[]" : "[" + withoutBody + "}]";

        server
            .Given(Request.Create().WithPath(PagesPath).UsingGet())
            .AtPriority(2)
            .RespondWith(Json("{\"results\":" + bodyless + ",\"_links\":{}}"));
    }

    private static void StubUpdate(WireMockServer server, int version) => server
        .Given(Request.Create().WithPath($"{PagesPath}/{PageId}").UsingPut())
        .RespondWith(Json(
            "{\"id\":\"" + PageId + "\",\"title\":\"" + Title + "\",\"spaceId\":\"" + SpaceId
            + "\",\"version\":{\"number\":" + version + "}}"));

    private static string RequestBody(WireMockServer server, string method)
    {
        var entry = server.LogEntries.Single(log =>
            string.Equals(log.RequestMessage!.Method, method, StringComparison.Ordinal));
        var body = entry.RequestMessage!.Body;
        body.ShouldNotBeNull();

        return body!;
    }

    /// <summary>
    /// What the fake Confluence actually answered the title lookup with, response side rather than
    /// request side. Lets a test that poisons that response prove the poison was served instead of
    /// assuming it — the same guard <c>Cli.CliConfluenceTests.Served</c> gives the page path.
    /// </summary>
    private static string ServedTitleSearch(WireMockServer server) =>
        string.Join(
            Environment.NewLine,
            server.LogEntries
                .Where(log => string.Equals(log.RequestMessage?.Path, PagesPath, StringComparison.Ordinal)
                    && string.Equals(log.RequestMessage?.Method, "GET", StringComparison.OrdinalIgnoreCase))
                .Select(log => log.ResponseMessage?.BodyData?.BodyAsString ?? string.Empty));

    /// <summary>
    /// The full URL of the title lookup as the client actually sent it, query string included — request
    /// side, where <see cref="ServedTitleSearch"/> is response side.
    /// </summary>
    private static string TitleSearchUrl(WireMockServer server) => server.LogEntries
        .Select(log => log.RequestMessage)
        .Single(request => string.Equals(request?.Path, PagesPath, StringComparison.Ordinal)
            && string.Equals(request?.Method, "GET", StringComparison.OrdinalIgnoreCase))!
        .Url;

    private static ConfluenceClient CreateClient(WireMockServer server) => ConfluenceClient.Create(
        new ConfluenceClientOptions
        {
            BaseUrl = new Uri($"{server.Url}/wiki"),
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.FromMilliseconds(1),
            Timeout = TimeSpan.FromSeconds(30),
        },
        Credentials);

    private static IResponseBuilder Json(string body) => Response.Create()
        .WithStatusCode(HttpStatusCode.OK)
        .WithHeader("Content-Type", "application/json")
        .WithBody(body);
}
