using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
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
    private const string PageId = "65601";
    private const string FilePart = "file";

    /// <summary>Flattened the way PLAN.md §7 names an attachment: underscores, and longer than a filename.</summary>
    private const string DiagramName = "docs_architecture_data_flow.svg";

    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg"><rect width="4" height="4"/></svg>""";

    private static readonly ConfluenceCredentials Credentials = new(Email, ApiToken);

    /// <summary>A field rather than an inline array, which CA1861 rejects as an argument.</summary>
    private static readonly string[] ApprovedLabel = ["approved"];

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

    [Fact]
    public async Task Creates_a_page_and_returns_the_id_and_version_Confluence_assigned()
    {
        using var server = WireMockServer.Start();
        var body = CreatedPageBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingPost())
            .RespondWith(Json(body));

        var draft = new ConfluencePageDraft(SpaceId, "Costs & billing", "<p>New</p>", ParentId: "131074");

        using var client = CreateClient(server);
        var page = await client.CreatePageAsync(draft, TestContext.Current.CancellationToken);

        page.Id.ShouldBe("77821");
        page.Version.ShouldBe(1);

        var payload = Payload(server);
        payload.GetProperty("spaceId").GetString().ShouldBe(SpaceId);
        payload.GetProperty("status").GetString().ShouldBe("current");
        payload.GetProperty("title").GetString().ShouldBe("Costs & billing");
        payload.GetProperty("parentId").GetString().ShouldBe("131074");

        var storage = payload.GetProperty("body").GetProperty("storage");
        storage.GetProperty("representation").GetString().ShouldBe("storage");
        storage.GetProperty("value").GetString().ShouldBe("<p>New</p>");
    }

    /// <summary>
    /// An absent <c>parentId</c> is documented as "put it under the space homepage"; a
    /// <c>"parentId": null</c> is a value the endpoint documents no handling for at all. The
    /// difference is whether the serializer omits nulls, which is easy to lose in a refactor.
    /// </summary>
    [Fact]
    public async Task A_draft_with_no_parent_omits_the_field_rather_than_sending_null()
    {
        using var server = WireMockServer.Start();
        var body = CreatedPageBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingPost())
            .RespondWith(Json(body));

        var draft = new ConfluencePageDraft(SpaceId, "Costs & billing", "<p>New</p>");

        using var client = CreateClient(server);
        _ = await client.CreatePageAsync(draft, TestContext.Current.CancellationToken);

        Payload(server).TryGetProperty("parentId", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Updates_a_page_with_the_version_after_the_one_it_read()
    {
        using var server = WireMockServer.Start();
        var body = UpdatedPageBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages/65601")).UsingPut())
            .RespondWith(Json(body));

        var revision = new ConfluencePageRevision(
            "65601",
            "Domain model",
            "<p>Fresh</p>",
            CurrentVersion: 7,
            ParentId: "131074");

        using var client = CreateClient(server);
        var page = await client.UpdatePageAsync(revision, TestContext.Current.CancellationToken);

        page.Version.ShouldBe(8);

        var payload = Payload(server);
        payload.GetProperty("id").GetString().ShouldBe("65601");
        payload.GetProperty("status").GetString().ShouldBe("current");
        payload.GetProperty("title").GetString().ShouldBe("Domain model");
        payload.GetProperty("parentId").GetString().ShouldBe("131074");
        payload.GetProperty("body").GetProperty("storage").GetProperty("value").GetString().ShouldBe("<p>Fresh</p>");

        // The caller passes the version it read; the +1 is the client's job, in one place.
        payload.GetProperty("version").GetProperty("number").GetInt32().ShouldBe(8);

        // The schema accepts spaceId but its own note says it cannot move a page between spaces, and
        // an unset version message must not arrive as null.
        payload.TryGetProperty("spaceId", out _).ShouldBeFalse();
        payload.GetProperty("version").TryGetProperty("message", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Sends_the_version_message_when_the_caller_supplied_one()
    {
        using var server = WireMockServer.Start();
        var body = UpdatedPageBody;
        server
            .Given(Request.Create().WithPath(ApiPath("pages/65601")).UsingPut())
            .RespondWith(Json(body));

        var revision = new ConfluencePageRevision(
            "65601",
            "Domain model",
            "<p>Fresh</p>",
            CurrentVersion: 7,
            VersionMessage: "docume publish 98c6df8");

        using var client = CreateClient(server);
        _ = await client.UpdatePageAsync(revision, TestContext.Current.CancellationToken);

        Payload(server)
            .GetProperty("version")
            .GetProperty("message")
            .GetString()
            .ShouldBe("docume publish 98c6df8");
    }

    /// <summary>
    /// The case a bulk publish has to survive: a human edited the page between DocuMe's read and its
    /// write. It must name the page and not push again — see
    /// <see cref="ConfluenceConflictException"/> for why re-reading the version is the wrong default.
    /// </summary>
    [Fact]
    public async Task A_version_conflict_names_the_page_and_does_not_write_again()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath("pages/65601")).UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.Conflict)
                .WithBody("Version must be incremented when updating a page"));

        var revision = new ConfluencePageRevision("65601", "Domain model", "<p>Fresh</p>", CurrentVersion: 7);

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceConflictException>(
            () => client.UpdatePageAsync(revision, TestContext.Current.CancellationToken));

        exception.Operation.ShouldContain("65601");
        exception.Message.ShouldContain("version 8");
        exception.Message.ShouldContain("Version must be incremented");
        exception.Message.ShouldContain("Re-run publish");
        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// Where the read path's title guard deliberately routes a near-miss: rather than updating a page
    /// it never identified, DocuMe creates, and Confluence's per-space title uniqueness rejects it.
    /// The report has to carry the title, because the request path is just <c>api/v2/pages</c>.
    /// </summary>
    [Fact]
    public async Task A_rejected_create_is_reported_with_the_title_it_tried()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithBody("""{"errors":[{"title":"A page with this title already exists"}]}"""));

        var draft = new ConfluencePageDraft(SpaceId, "Costs & billing", "<p>New</p>");

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceApiException>(
            () => client.CreatePageAsync(draft, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        exception.Operation.ShouldNotBeNull();
        exception.Operation!.ShouldContain("Costs & billing");
        exception.Message.ShouldContain("already exists");
        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// A rate limit is a rejection, not a partial apply, so replaying the create is correct — and it
    /// is what an ~80-page bulk publish depends on (PLAN.md §13 S5). What this pins is that the body
    /// survives the replay: a streamed content would be consumed by the first attempt and the second
    /// would silently send nothing.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_create_is_replayed_with_the_same_body()
    {
        using var server = WireMockServer.Start();
        var body = CreatedPageBody;
        const string scenario = "create-rate-limited";
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingPost())
            .InScenario(scenario)
            .WillSetStateTo("allowed")
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.TooManyRequests));
        server
            .Given(Request.Create().WithPath(ApiPath("pages")).UsingPost())
            .InScenario(scenario)
            .WhenStateIs("allowed")
            .RespondWith(Json(body));

        var draft = new ConfluencePageDraft(SpaceId, "Costs & billing", "<p>New</p>");

        using var client = CreateClient(server);
        var page = await client.CreatePageAsync(draft, TestContext.Current.CancellationToken);

        page.Id.ShouldBe("77821");
        server.LogEntries.Count.ShouldBe(2);
        Payload(server, index: 1).GetProperty("body").GetProperty("storage").GetProperty("value")
            .GetString()
            .ShouldBe("<p>New</p>");
    }

    [Fact]
    public async Task An_authentication_failure_on_a_write_stops_dead_too()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath("pages/65601")).UsingPut())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Unauthorized));

        var revision = new ConfluencePageRevision("65601", "Domain model", "<p>Fresh</p>", CurrentVersion: 7);

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceAuthenticationException>(
            () => client.UpdatePageAsync(revision, TestContext.Current.CancellationToken));

        exception.Message.ShouldNotContain(ApiToken);
        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// A version below 1 cannot have come from a page Confluence holds, so it is a caller bug — and
    /// it is caught before anything reaches the wire, because sending version 1 over an existing page
    /// is how a publish would blank someone's work.
    /// </summary>
    [Fact]
    public async Task A_version_Confluence_could_never_have_returned_is_refused_before_the_request()
    {
        using var server = WireMockServer.Start();
        var revision = new ConfluencePageRevision("65601", "Domain model", "<p>Fresh</p>", CurrentVersion: 0);

        using var client = CreateClient(server);
        _ = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.UpdatePageAsync(revision, TestContext.Current.CancellationToken));

        server.LogEntries.Count.ShouldBe(0);
    }

    /// <summary>
    /// The multipart shape v1 requires: the file under a part literally named <c>file</c> carrying the
    /// attachment name, the mandatory <c>minorEdit</c>, and the XSRF opt-out header without which
    /// Confluence blocks a <c>multipart/form-data</c> request outright.
    /// </summary>
    [Fact]
    public async Task Uploads_an_attachment_as_multipart_with_the_xsrf_header()
    {
        using var server = WireMockServer.Start();
        var body = AttachmentBody;
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPut())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var attachment = await client.UploadAttachmentAsync(
            new ConfluenceAttachmentUpload(PageId, DiagramName, Encoding.UTF8.GetBytes(Svg), "image/svg+xml"),
            TestContext.Current.CancellationToken);

        attachment.Id.ShouldBe("att77830");
        attachment.Title.ShouldBe(DiagramName);
        attachment.Version.ShouldBe(2);

        var request = LastRequest(server);
        request.Method.ShouldBe("PUT");
        request.Headers!["X-Atlassian-Token"].Single().ShouldBe("nocheck");

        // .NET writes the part name as a bare token (name=file, not name="file") and emits the file
        // name twice — filename= plus the RFC 5987 filename*=utf-8''… — which is what Confluence has
        // to agree with about the stored attachment name (sandbox item 16).
        var multipart = MultipartBody(server);
        multipart.ShouldContain($"name={FilePart}");
        multipart.ShouldContain($"filename={DiagramName}");
        multipart.ShouldContain("Content-Type: image/svg+xml");
        multipart.ShouldContain(Svg);
        multipart.ShouldContain("name=minorEdit");
        multipart.ShouldContain("true");
    }

    /// <summary>
    /// v1 offers both verbs on this path and they are not interchangeable: <c>POST</c> is create-only
    /// and answers 400 when the content already has an attachment with that filename, while <c>PUT</c>
    /// stores a new version. Because §6.2 uploads only the attachments whose hash changed, the name
    /// almost always exists already — so the create-only verb would fail exactly the uploads that
    /// matter. This stub answers the way the real API does, so picking the wrong verb fails here.
    /// </summary>
    [Fact]
    public async Task Uploads_with_the_verb_that_replaces_an_existing_name_rather_than_the_one_that_rejects_it()
    {
        using var server = WireMockServer.Start();
        var body = AttachmentBody;
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithBody("""{"message":"Cannot add a new attachment with same file name as an existing attachment"}"""));
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPut())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var attachment = await client.UploadAttachmentAsync(
            new ConfluenceAttachmentUpload(PageId, DiagramName, Encoding.UTF8.GetBytes(Svg), "image/svg+xml"),
            TestContext.Current.CancellationToken);

        attachment.Version.ShouldBe(2);
        LastRequest(server).Method.ShouldBe("PUT");
    }

    /// <summary>
    /// The v1 calls hang off the same <c>/wiki/</c> base address as the v2 ones, by a relative path
    /// that starts <c>rest/api</c> instead of <c>api/v2</c>. Worth pinning separately: a base URL
    /// without a trailing slash silently relocated every request once already.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composes_the_v1_path_off_the_same_base_as_the_v2_calls(bool trailingSlash)
    {
        using var server = WireMockServer.Start();
        var body = AttachmentBody;
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPut())
            .RespondWith(Json(body));

        using var client = CreateClient(server, trailingSlash: trailingSlash);
        _ = await client.UploadAttachmentAsync(
            new ConfluenceAttachmentUpload(PageId, DiagramName, Encoding.UTF8.GetBytes(Svg), "image/svg+xml"),
            TestContext.Current.CancellationToken);

        LastRequest(server).Path.ShouldBe(AttachmentPath);
    }

    /// <summary>
    /// Same rule as the page write's null-omission: an absent comment means "do not set one", which is
    /// a missing part rather than an empty one.
    /// </summary>
    [Fact]
    public async Task An_absent_attachment_comment_is_omitted_rather_than_sent_empty()
    {
        using var server = WireMockServer.Start();
        var body = AttachmentBody;
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPut())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        _ = await client.UploadAttachmentAsync(
            new ConfluenceAttachmentUpload(PageId, DiagramName, Encoding.UTF8.GetBytes(Svg), "image/svg+xml"),
            TestContext.Current.CancellationToken);

        MultipartBody(server).ShouldNotContain("name=comment");
    }

    [Fact]
    public async Task An_attachment_comment_is_sent_when_supplied()
    {
        using var server = WireMockServer.Start();
        var body = AttachmentBody;
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPut())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        _ = await client.UploadAttachmentAsync(
            new ConfluenceAttachmentUpload(
                PageId,
                DiagramName,
                Encoding.UTF8.GetBytes(Svg),
                "image/svg+xml",
                Comment: "Rendered by docume"),
            TestContext.Current.CancellationToken);

        var multipart = MultipartBody(server);
        multipart.ShouldContain("name=comment");
        multipart.ShouldContain("Rendered by docume");
    }

    /// <summary>
    /// The multipart counterpart of the create's replay test, and the one that needed proving rather
    /// than assuming: a <see cref="StreamContent"/> part would be drained by the first attempt and the
    /// replay would upload an empty file over a working attachment. Buffered parts survive.
    /// </summary>
    [Fact]
    public async Task A_rate_limited_upload_is_replayed_with_the_same_bytes()
    {
        using var server = WireMockServer.Start();
        var body = AttachmentBody;
        const string scenario = "upload-rate-limited";
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPut())
            .InScenario(scenario)
            .WillSetStateTo("allowed")
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.TooManyRequests));
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPut())
            .InScenario(scenario)
            .WhenStateIs("allowed")
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var attachment = await client.UploadAttachmentAsync(
            new ConfluenceAttachmentUpload(PageId, DiagramName, Encoding.UTF8.GetBytes(Svg), "image/svg+xml"),
            TestContext.Current.CancellationToken);

        attachment.Id.ShouldBe("att77830");
        server.LogEntries.Count.ShouldBe(2);

        var replayed = MultipartBody(server, index: 1);
        replayed.ShouldContain(Svg);
        replayed.ShouldContain(DiagramName);
    }

    /// <summary>
    /// Zero bytes means whatever produced the file failed. Uploading it would not error — it would
    /// store a new version of a working attachment containing nothing, so it never reaches the wire.
    /// </summary>
    [Fact]
    public async Task An_empty_attachment_never_reaches_the_wire()
    {
        using var server = WireMockServer.Start();
        var upload = new ConfluenceAttachmentUpload(PageId, DiagramName, ReadOnlyMemory<byte>.Empty, "image/svg+xml");

        using var client = CreateClient(server);
        _ = await Should.ThrowAsync<ArgumentException>(
            () => client.UploadAttachmentAsync(upload, TestContext.Current.CancellationToken));

        server.LogEntries.Count.ShouldBe(0);
    }

    [Fact]
    public async Task An_authentication_failure_on_an_upload_stops_dead_too()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPut())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Forbidden));

        var upload = new ConfluenceAttachmentUpload(
            PageId,
            DiagramName,
            Encoding.UTF8.GetBytes(Svg),
            "image/svg+xml");

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceAuthenticationException>(
            () => client.UploadAttachmentAsync(upload, TestContext.Current.CancellationToken));

        exception.Message.ShouldNotContain(ApiToken);
        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// A 200 carrying no attachment is not "nothing to do" — DocuMe just uploaded a file. Reading it
    /// as success would record a published diagram that is not there.
    /// </summary>
    [Fact]
    public async Task An_upload_Confluence_accepted_without_returning_the_attachment_fails_loud()
    {
        using var server = WireMockServer.Start();
        var body = EmptyResultsBody;
        server
            .Given(Request.Create().WithPath(AttachmentPath).UsingPut())
            .RespondWith(Json(body));

        var upload = new ConfluenceAttachmentUpload(
            PageId,
            DiagramName,
            Encoding.UTF8.GetBytes(Svg),
            "image/svg+xml");

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceProtocolException>(
            () => client.UploadAttachmentAsync(upload, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(DiagramName);
    }

    /// <summary>
    /// The v1 label body is an array of <c>{prefix, name}</c>, both required. <c>global</c> is the
    /// prefix an ordinary human-visible label lives under — the one a reviewer sees and clicks.
    /// </summary>
    [Fact]
    public async Task Adds_labels_as_an_array_with_the_global_prefix()
    {
        using var server = WireMockServer.Start();
        var body = LabelsBody;
        server
            .Given(Request.Create().WithPath(LabelPath).UsingPost())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var labels = await client.AddLabelsAsync(PageId, ApprovedLabel, TestContext.Current.CancellationToken);

        labels.Count.ShouldBe(1);
        labels[0].Name.ShouldBe("approved");
        labels[0].Prefix.ShouldBe("global");

        var request = LastRequest(server);
        request.Headers!["X-Atlassian-Token"].Single().ShouldBe("nocheck");

        var payload = Payload(server);
        payload.ValueKind.ShouldBe(JsonValueKind.Array);
        payload[0].GetProperty("prefix").GetString().ShouldBe("global");
        payload[0].GetProperty("name").GetString().ShouldBe("approved");
    }

    [Fact]
    public async Task Adding_no_labels_never_reaches_the_wire()
    {
        using var server = WireMockServer.Start();

        using var client = CreateClient(server);
        _ = await Should.ThrowAsync<ArgumentException>(
            () => client.AddLabelsAsync(PageId, [], TestContext.Current.CancellationToken));

        server.LogEntries.Count.ShouldBe(0);
    }

    /// <summary>
    /// Approval invalidation (PLAN.md §8). The <c>?name=</c> spelling is deliberate: the
    /// <c>/label/{label}</c> path form cannot express a label containing a slash, and DocuMe's label
    /// names are consumer-configurable.
    /// </summary>
    [Fact]
    public async Task Removes_a_label_by_query_parameter_and_accepts_an_empty_204()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(LabelPath).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));

        using var client = CreateClient(server);
        await client.RemoveLabelAsync(PageId, "approved", TestContext.Current.CancellationToken);

        var request = LastRequest(server);
        request.Method.ShouldBe("DELETE");
        request.Query!["name"].Single().ShouldBe("approved");
    }

    /// <summary>
    /// A label name is consumer-configured, so it can carry characters that need escaping. Asserting
    /// the decoded query value rather than the raw URL keeps this a test of the wire, not of WireMock.
    /// </summary>
    [Fact]
    public async Task A_label_name_needing_escaping_survives_the_query_string()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(LabelPath).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));

        using var client = CreateClient(server);
        await client.RemoveLabelAsync(PageId, "needs review&more", TestContext.Current.CancellationToken);

        LastRequest(server).Query!["name"].Single().ShouldBe("needs review&more");
    }

    /// <summary>
    /// A label Confluence refuses arrives as flat prose on a 400, so the message has to name what
    /// DocuMe was doing for the failure to be actionable.
    /// </summary>
    [Fact]
    public async Task A_rejected_label_names_the_page_and_quotes_confluence()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(LabelPath).UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithBody("""{"message":"The label contains invalid characters"}"""));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceApiException>(
            () => client.AddLabelsAsync(PageId, ApprovedLabel, TestContext.Current.CancellationToken));

        exception.Operation.ShouldNotBeNull();
        exception.Operation!.ShouldContain("approved");
        exception.Operation.ShouldContain(PageId);
        exception.Message.ShouldContain("invalid characters");
        server.LogEntries.Count.ShouldBe(1);
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

    /// <summary>
    /// The v1 root, which shares the <c>/wiki/</c> base with v2 and differs only after it.
    /// </summary>
    private static string LegacyPath(string endpoint) => $"/wiki/rest/api/content/{PageId}/{endpoint}";

    private static string AttachmentPath => LegacyPath("child/attachment");

    private static string LabelPath => LegacyPath("label");

    private static IRequestMessage LastRequest(WireMockServer server)
    {
        var request = server.LogEntries.Single().RequestMessage;
        request.ShouldNotBeNull();

        return request!;
    }

    /// <summary>
    /// The JSON body of one logged request, cloned so it outlives the <see cref="JsonDocument"/>.
    /// Asserting on the parsed payload rather than the raw string keeps property order and whitespace
    /// out of the test.
    /// </summary>
    private static JsonElement Payload(WireMockServer server, int index = 0)
    {
        var request = server.LogEntries[index].RequestMessage;
        request.ShouldNotBeNull();

        var body = request!.Body;
        body.ShouldNotBeNull();

        using var document = JsonDocument.Parse(body!);

        return document.RootElement.Clone();
    }

    /// <summary>
    /// The raw multipart body of one logged request. Read as bytes rather than text because that is
    /// what a file upload is; decoding it here keeps the assertions readable, and the fixtures are
    /// UTF-8 by construction.
    /// </summary>
    private static string MultipartBody(WireMockServer server, int index = 0)
    {
        var request = server.LogEntries[index].RequestMessage;
        request.ShouldNotBeNull();

        var bytes = request!.BodyAsBytes;
        bytes.ShouldNotBeNull();

        return Encoding.UTF8.GetString(bytes!);
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

    private static string CreatedPageBody =>
        """
        {
          "id": "77821",
          "status": "current",
          "title": "Costs & billing",
          "spaceId": "98304",
          "parentId": "131074",
          "version": { "number": 1 },
          "body": { "storage": { "value": "<p>New</p>", "representation": "storage" } }
        }
        """;

    private static string UpdatedPageBody =>
        """
        {
          "id": "65601",
          "status": "current",
          "title": "Domain model",
          "spaceId": "98304",
          "parentId": "131074",
          "version": { "number": 8 },
          "body": { "storage": { "value": "<p>Fresh</p>", "representation": "storage" } }
        }
        """;

    /// <summary>
    /// The v1 <c>ContentArray</c> shape: the same <c>results</c> envelope as v2, plus the paging
    /// members DocuMe does not read. Version 2 because the interesting upload is a re-upload.
    /// </summary>
    private static string AttachmentBody =>
        """
        {
          "results": [
            {
              "id": "att77830",
              "type": "attachment",
              "status": "current",
              "title": "docs_architecture_data_flow.svg",
              "version": { "number": 2 }
            }
          ],
          "start": 0,
          "limit": 200,
          "size": 1,
          "_links": {}
        }
        """;

    private static string LabelsBody =>
        """
        {
          "results": [
            { "prefix": "global", "name": "approved", "id": "10001", "label": "approved" }
          ],
          "start": 0,
          "limit": 200,
          "size": 1,
          "_links": {}
        }
        """;

    private static string EmptyResultsBody => """{ "results": [], "_links": {} }""";

    private static string NoResultsBody => """{ "_links": {} }""";
}
