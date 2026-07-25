using System.Diagnostics;
using System.Net;
using System.Text;
using DocuMe.Core.Confluence;
using Shouldly;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Confluence;

/// <summary>
/// The Confluence client against a real local HTTP server (.claude/rules/testing.md §4.2). A stubbed
/// <c>HttpMessageHandler</c> would be quicker but would bypass the resilience handler, and the
/// behavior most worth pinning here — 401/403 stop dead, 429/5xx back off — lives in exactly that
/// handler.
/// </summary>
public sealed class ConfluenceClientTests
{
    private const string Email = "bot@example.com";
    private const string ApiToken = "very-secret-token";
    private const string SpaceId = "98304";

    private static readonly ConfluenceCredentials Credentials = new(Email, ApiToken);

    [Fact]
    public async Task Finds_a_space_by_its_key()
    {
        using var server = WireMockServer.Start();
        var body = SpacesBody;
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var space = await client.FindSpaceByKeyAsync("DOCUMESBX", TestContext.Current.CancellationToken);

        space.ShouldNotBeNull();
        space.Id.ShouldBe(SpaceId);
        space.Key.ShouldBe("DOCUMESBX");
        space.Name.ShouldBe("DocuMe Sandbox");

        var request = LastRequest(server);
        request.Path.ShouldBe(ApiPath("spaces"));
        request.Url.ShouldContain("keys=DOCUMESBX");
    }

    /// <summary>
    /// The base URL humans write has no trailing slash, which plain <see cref="Uri"/> composition
    /// would treat as a file name and drop — moving every call from <c>/wiki/api/v2</c> to
    /// <c>/api/v2</c>. Both spellings must land on the same endpoint.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Addresses_the_same_endpoint_whether_or_not_the_base_url_ends_in_a_slash(bool trailingSlash)
    {
        using var server = WireMockServer.Start();
        var body = SpacesBody;
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server, trailingSlash: trailingSlash);
        var space = await client.FindSpaceByKeyAsync("DOCUMESBX", TestContext.Current.CancellationToken);

        space.ShouldNotBeNull();
        LastRequest(server).Path.ShouldBe(ApiPath("spaces"));
    }

    [Fact]
    public async Task Sends_the_credentials_as_basic_auth()
    {
        using var server = WireMockServer.Start();
        var body = SpacesBody;
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        _ = await client.FindSpaceByKeyAsync("DOCUMESBX", TestContext.Current.CancellationToken);

        var authorization = LastRequest(server).Headers!["Authorization"].Single();
        authorization.ShouldStartWith("Basic ");

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..]));
        decoded.ShouldBe($"{Email}:{ApiToken}");
    }

    [Fact]
    public async Task A_space_key_nothing_matches_reads_as_absent()
    {
        using var server = WireMockServer.Start();
        var body = EmptyResultsBody;
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var space = await client.FindSpaceByKeyAsync("NOPE", TestContext.Current.CancellationToken);

        space.ShouldBeNull();
    }

    [Fact]
    public async Task Finds_the_current_page_with_an_exact_title_and_asks_only_for_current_pages()
    {
        using var server = WireMockServer.Start();
        var body = PagesBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var page = await client.FindPageByTitleAsync(
            SpaceId,
            "Domain model",
            includeBody: true,
            TestContext.Current.CancellationToken);

        page.ShouldNotBeNull();
        page.Id.ShouldBe("65601");
        page.Title.ShouldBe("Domain model");
        page.SpaceId.ShouldBe(SpaceId);
        page.ParentId.ShouldBe("131074");
        page.Version.ShouldBe(7);
        page.Storage.ShouldBe("<p>Hello</p>");

        var request = LastRequest(server);
        request.Url.ShouldContain($"space-id={SpaceId}");
        request.Url.ShouldContain("status=current");
        request.Url.ShouldContain("body-format=storage");

        // Asserted through the parsed query rather than the raw URL: WireMock reports Url already
        // decoded, so a substring check there would be testing WireMock, not what we sent.
        request.Query!["title"].Single().ShouldBe("Domain model");
    }

    /// <summary>
    /// A title carrying a query delimiter is the case that would silently truncate if the client
    /// ever stopped escaping: <c>title=Costs &amp; billing</c> arrives as <c>title=Costs </c> plus a
    /// stray parameter, and the page lookup then misses a page that exists.
    /// </summary>
    [Fact]
    public async Task A_title_holding_a_query_delimiter_survives_the_round_trip()
    {
        using var server = WireMockServer.Start();
        var body = EmptyResultsBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        _ = await client.FindPageByTitleAsync(
            SpaceId,
            "Costs & billing",
            includeBody: false,
            TestContext.Current.CancellationToken);

        LastRequest(server).Query!["title"].Single().ShouldBe("Costs & billing");
    }

    [Fact]
    public async Task Does_not_ask_for_a_body_it_was_not_asked_for()
    {
        using var server = WireMockServer.Start();
        var body = PagesBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        _ = await client.FindPageByTitleAsync(
            SpaceId,
            "Domain model",
            includeBody: false,
            TestContext.Current.CancellationToken);

        LastRequest(server).Url.ShouldNotContain("body-format");
    }

    /// <summary>
    /// Atlassian's schema documents the <c>title</c> parameter only as "filter the results to pages
    /// based on their title", so the client re-checks the title it got back. A loose server-side
    /// match must never turn into DocuMe updating the wrong page.
    /// </summary>
    [Fact]
    public async Task A_page_whose_title_only_nearly_matches_is_not_a_hit()
    {
        using var server = WireMockServer.Start();
        var body = PagesBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var page = await client.FindPageByTitleAsync(
            SpaceId,
            "Domain",
            includeBody: false,
            TestContext.Current.CancellationToken);

        page.ShouldBeNull();
    }

    [Fact]
    public async Task A_page_id_Confluence_no_longer_has_reads_as_absent()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath("pages/65601")).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));

        using var client = CreateClient(server);
        var page = await client.FindPageByIdAsync(
            "65601",
            includeBody: false,
            TestContext.Current.CancellationToken);

        page.ShouldBeNull();
    }

    [Fact]
    public async Task Reads_a_page_by_id()
    {
        using var server = WireMockServer.Start();
        var body = SinglePageBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages/65601")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var page = await client.FindPageByIdAsync(
            "65601",
            includeBody: true,
            TestContext.Current.CancellationToken);

        page.ShouldNotBeNull();
        page.Version.ShouldBe(7);
        page.Storage.ShouldBe("<p>Hello</p>");
        LastRequest(server).Url.ShouldContain("body-format=storage");
    }

    /// <summary>
    /// The rule this whole slice exists to get right (.claude/rules/security.md §1.2): an auth
    /// failure is a hard stop, and the request count proves nothing was retried behind it.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task An_authentication_failure_stops_dead_without_a_second_attempt(int statusCode)
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(statusCode));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceAuthenticationException>(
            () => client.FindSpaceByKeyAsync("DOCUMESBX", TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe((HttpStatusCode)statusCode);
        exception.Message.ShouldContain(ConfluenceCredentials.TokenVariable);
        exception.Message.ShouldNotContain(ApiToken);
        server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_rate_limit_waits_as_long_as_the_server_asked_for()
    {
        using var server = WireMockServer.Start();
        var body = SpacesBody;
        const string scenario = "rate-limited";
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .InScenario(scenario)
            .WillSetStateTo("allowed")
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.TooManyRequests)
                .WithHeader("Retry-After", "1"));
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .InScenario(scenario)
            .WhenStateIs("allowed")
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var stopwatch = Stopwatch.StartNew();
        var space = await client.FindSpaceByKeyAsync("DOCUMESBX", TestContext.Current.CancellationToken);
        stopwatch.Stop();

        space.ShouldNotBeNull();
        server.LogEntries.Count.ShouldBe(2);

        // The configured backoff is a millisecond, so anything near a second can only have come
        // from honoring Retry-After.
        stopwatch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public async Task A_server_error_that_recovers_is_retried()
    {
        using var server = WireMockServer.Start();
        var body = SpacesBody;
        const string scenario = "recovering";
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .InScenario(scenario)
            .WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.InternalServerError));
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .InScenario(scenario)
            .WhenStateIs("recovered")
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var space = await client.FindSpaceByKeyAsync("DOCUMESBX", TestContext.Current.CancellationToken);

        space.ShouldNotBeNull();
        server.LogEntries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_server_error_that_never_recovers_reports_the_status_after_every_attempt()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.InternalServerError)
                .WithBody("upstream is unwell"));

        const int retries = 2;
        using var client = CreateClient(server, maxRetryAttempts: retries);
        var exception = await Should.ThrowAsync<ConfluenceApiException>(
            () => client.FindSpaceByKeyAsync("DOCUMESBX", TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        exception.Message.ShouldContain("upstream is unwell");
        server.LogEntries.Count.ShouldBe(retries + 1);
    }

    [Fact]
    public async Task A_body_that_is_not_json_fails_loud()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath("spaces")).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithBody("<html><body>Log in to continue</body></html>"));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceProtocolException>(
            () => client.FindSpaceByKeyAsync("DOCUMESBX", TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("Log in to continue");
    }

    /// <summary>
    /// A response without <c>results</c> must not read as "no such page": that answer sends the
    /// publish pipeline into creating a duplicate of a page that already exists.
    /// </summary>
    [Fact]
    public async Task A_success_body_without_a_results_array_fails_loud()
    {
        using var server = WireMockServer.Start();
        var body = NoResultsBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceProtocolException>(
            () => client.FindPageByTitleAsync(
                SpaceId,
                "Domain model",
                includeBody: false,
                TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("results");
    }

    [Fact]
    public async Task A_page_without_a_version_fails_loud()
    {
        using var server = WireMockServer.Start();
        var body = VersionlessPageBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceProtocolException>(
            () => client.FindPageByTitleAsync(
                SpaceId,
                "Domain model",
                includeBody: false,
                TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("version.number");
    }

    private static ConfluenceClient CreateClient(
        WireMockServer server,
        int maxRetryAttempts = 2,
        bool trailingSlash = false)
    {
        var suffix = trailingSlash ? "wiki/" : "wiki";
        var options = new ConfluenceClientOptions
        {
            BaseUrl = new Uri($"{server.Url}/{suffix}"),
            MaxRetryAttempts = maxRetryAttempts,
            RetryDelay = TimeSpan.FromMilliseconds(1),
            Timeout = TimeSpan.FromSeconds(30),
        };

        return ConfluenceClient.Create(options, Credentials);
    }

    private static string ApiPath(string endpoint) => $"/wiki/api/v2/{endpoint}";

    private static IRequestMessage LastRequest(WireMockServer server)
    {
        var request = server.LogEntries.Single().RequestMessage;
        request.ShouldNotBeNull();

        return request!;
    }

    private static IResponseBuilder Json(string body)
        => Response.Create()
            .WithStatusCode(HttpStatusCode.OK)
            .WithHeader("Content-Type", "application/json")
            .WithBody(body);

    private static string SpacesBody =>
        """
        {
          "results": [
            { "id": "98304", "key": "DOCUMESBX", "name": "DocuMe Sandbox", "type": "global", "status": "current" }
          ],
          "_links": { "base": "https://example.atlassian.net/wiki" }
        }
        """;

    private static string PagesBody =>
        """
        {
          "results": [
            {
              "id": "65601",
              "status": "current",
              "title": "Domain model",
              "spaceId": "98304",
              "parentId": "131074",
              "version": { "number": 7, "createdAt": "2026-07-20T10:00:00.000Z" },
              "body": { "storage": { "value": "<p>Hello</p>", "representation": "storage" } }
            }
          ],
          "_links": {}
        }
        """;

    private static string SinglePageBody =>
        """
        {
          "id": "65601",
          "status": "current",
          "title": "Domain model",
          "spaceId": "98304",
          "parentId": "131074",
          "version": { "number": 7 },
          "body": { "storage": { "value": "<p>Hello</p>", "representation": "storage" } }
        }
        """;

    private static string VersionlessPageBody =>
        """
        {
          "results": [
            { "id": "65601", "status": "current", "title": "Domain model", "spaceId": "98304" }
          ],
          "_links": {}
        }
        """;

    private static string EmptyResultsBody => """{ "results": [], "_links": {} }""";

    private static string NoResultsBody => """{ "_links": {} }""";
}
