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
        const string handEdit = "<p>NOTE from a reviewer: keep this paragraph, we edited it here.</p>";

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

        const string survived = "The write echoes back part of the body it read, so a hand edit in "
            + "Confluence now survives a republish and the page has two sources of truth (rule §9.1).";

        var body = RequestBody(server, "PUT");
        body.ShouldNotContain("NOTE from a reviewer", Case.Sensitive, survived);

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

    private static void StubTitleSearch(WireMockServer server, string? existing, int version = 1)
    {
        var results = existing is null
            ? "[]"
            : "[{\"id\":\"" + PageId + "\",\"title\":\"" + Title + "\",\"spaceId\":\"" + SpaceId
                + "\",\"parentId\":\"" + RootPageId + "\",\"version\":{\"number\":" + version
                + "},\"body\":{\"storage\":{\"value\":" + JsonSerializer.Serialize(existing)
                + ",\"representation\":\"storage\"}}}]";

        server
            .Given(Request.Create().WithPath(PagesPath).UsingGet())
            .RespondWith(Json("{\"results\":" + results + ",\"_links\":{}}"));
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
