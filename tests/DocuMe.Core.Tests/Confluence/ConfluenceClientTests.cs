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

    /// <summary>The page a move is relative to: a new parent, or a sibling to sit beside.</summary>
    private const string TargetPageId = "77889";

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

    /// <summary>
    /// The read the child-order post-pass diffs against (PLAN.md §6.2). The order the endpoint answers
    /// with is the order the caller gets: <c>childPosition</c> is read but never sorted on, because it is
    /// null on migrated pages and Confluence's own tree falls back to alphabetical for those.
    /// </summary>
    [Fact]
    public async Task Lists_a_pages_children_in_the_order_confluence_answers_with()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ChildrenPath).UsingGet())
            .RespondWith(Json(LastChildrenBody));

        using var client = CreateClient(server);
        var children = await client.GetChildPagesAsync(PageId, TestContext.Current.CancellationToken);

        children.Select(child => child.Id).ShouldBe(["333", "111"]);
        children[0].Title.ShouldBe("Guides");
        children[0].ChildPosition.ShouldBe(7);

        // A page migrated from Server has no position at all, which must read as absent rather than 0 —
        // position 0 is the top of the list, and the difference would be a wrong order, not a missing one.
        children[1].ChildPosition.ShouldBeNull();
    }

    /// <summary>
    /// v2 paginates with an opaque cursor, and a parent with more children than one page holds is
    /// ordinary in an ~80-page wiki. The cursor is lifted out of <c>_links.next</c> and re-sent, rather
    /// than the URL being followed as given: <c>next</c> carries the site's own <c>/wiki/</c> base
    /// segment, which composing it against the client's base address would duplicate.
    /// </summary>
    [Fact]
    public async Task Follows_the_cursor_until_confluence_stops_offering_one()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ChildrenPath).UsingGet())
            .RespondWith(Json(request => request.Query!.ContainsKey("cursor")
                ? LastChildrenBody
                : FirstChildrenBody));

        using var client = CreateClient(server);
        var children = await client.GetChildPagesAsync(PageId, TestContext.Current.CancellationToken);

        children.Select(child => child.Id).ShouldBe(["222", "333", "111"]);
        server.LogEntries.Count.ShouldBe(2);

        // The cursor arrives percent-encoded inside next and has to reach Confluence decoded-then-encoded
        // once, not twice: a base64 cursor's '==' padding is exactly what a double-encode mangles.
        var followed = server.LogEntries[1].RequestMessage;
        followed.ShouldNotBeNull();
        followed.Query!["cursor"].Single().ShouldBe("cGFnZT0y==");
    }

    /// <summary>
    /// No <c>limit</c> is sent. The v2 schema documents the parameter but neither its default nor its
    /// maximum, and a guessed value over the cap would be a 400 on a read the post-pass cannot do
    /// without; pagination covers the same ground with no guess in it.
    /// </summary>
    [Fact]
    public async Task Asks_for_children_without_guessing_a_page_size()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ChildrenPath).UsingGet())
            .RespondWith(Json(LastChildrenBody));

        using var client = CreateClient(server);
        _ = await client.GetChildPagesAsync(PageId, TestContext.Current.CancellationToken);

        LastRequest(server).Query!.ShouldBeEmpty();
    }

    /// <summary>
    /// Unlike a page read, a missing parent is not <c>null</c> here: the post-pass only asks about a
    /// parent it just wrote, so a 404 means the tree moved under the run and is worth saying.
    /// </summary>
    [Fact]
    public async Task A_parent_that_is_gone_fails_rather_than_reading_as_childless()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ChildrenPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound).WithBody("{}"));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceApiException>(
            () => client.GetChildPagesAsync(PageId, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A server that keeps handing back a cursor would otherwise loop forever inside one publish. The
    /// backstop is deliberately far above any real page tree, so hitting it means the pagination is
    /// broken rather than the wiki being large.
    /// </summary>
    [Fact]
    public async Task Stops_reading_children_when_the_cursor_never_runs_out()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ChildrenPath).UsingGet())
            .RespondWith(Json(FirstChildrenBody));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceProtocolException>(
            () => client.GetChildPagesAsync(PageId, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("more children than a page tree has");
    }

    /// <summary>
    /// The open-comment guard's read (PLAN.md §6.2 step 6). Resolution state is carried verbatim, and a
    /// comment that arrives without one reads as unresolved rather than closed.
    /// </summary>
    [Fact]
    public async Task Lists_a_pages_inline_comments_with_the_resolution_state_confluence_reports()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(InlineCommentsPath).UsingGet())
            .RespondWith(Json(LastCommentsBody));

        using var client = CreateClient(server);
        var comments = await client.GetInlineCommentsAsync(PageId, TestContext.Current.CancellationToken);

        comments.Select(comment => comment.Id).ShouldBe(["4002", "4003"]);
        comments[0].ResolutionStatus.ShouldBe("resolved");
        comments[0].IsResolved.ShouldBeTrue();
        comments[0].WebUiLink.ShouldBe("/spaces/DOCUMESBX/pages/65601/Domain+model?focusedCommentId=4002");

        // Atlassian's published schema for this endpoint does not document resolutionStatus at all, so a
        // response without one is a case that has to have an answer: not resolved, never "closed".
        comments[1].ResolutionStatus.ShouldBeNull();
        comments[1].IsResolved.ShouldBeFalse();
    }

    /// <summary>
    /// No query at all: no guessed <c>limit</c>, and — the one that matters — no server-side resolution
    /// filter. An Atlassian developer-community report has <c>resolution_status=open</c> answering comments
    /// whose own <c>resolutionStatus</c> reads <c>resolved</c>, so the filtering happens here, where it can
    /// be trusted.
    /// </summary>
    [Fact]
    public async Task Asks_for_every_comment_rather_than_trusting_the_resolution_filter()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(InlineCommentsPath).UsingGet())
            .RespondWith(Json(LastCommentsBody));

        using var client = CreateClient(server);
        var comments = await client.GetInlineCommentsAsync(PageId, TestContext.Current.CancellationToken);

        LastRequest(server).Query!.ShouldBeEmpty();

        // The resolved one is returned, not dropped: deciding what counts is the guard's job.
        comments.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Follows_the_cursor_through_a_page_with_more_comments_than_one_response_holds()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(InlineCommentsPath).UsingGet())
            .RespondWith(Json(request => request.Query!.ContainsKey("cursor")
                ? LastCommentsBody
                : FirstCommentsBody));

        using var client = CreateClient(server);
        var comments = await client.GetInlineCommentsAsync(PageId, TestContext.Current.CancellationToken);

        comments.Select(comment => comment.Id).ShouldBe(["4001", "4002", "4003"]);
        server.LogEntries.Count.ShouldBe(2);
    }

    /// <summary>
    /// A page that is gone fails rather than reading as "no comments": the guard only asks about a page it
    /// just read, and a 404 answered as an empty list would publish over the comments it was checking for.
    /// </summary>
    [Fact]
    public async Task A_page_that_is_gone_fails_rather_than_reading_as_having_no_comments()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(InlineCommentsPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound).WithBody("{}"));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceApiException>(
            () => client.GetInlineCommentsAsync(PageId, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Ingestion's footer read (PLAN.md §6.3's Comments bullet): the thread at the bottom of the page,
    /// with its text. Unlike the guard's read above, this one asks for a body — and nothing but the body
    /// format, so the resolution filter and the sort order stay this side's decisions.
    /// </summary>
    [Fact]
    public async Task Reads_the_footer_thread_with_its_text()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(FooterCommentsPath).UsingGet())
            .RespondWith(Json(FooterCommentsBody));

        using var client = CreateClient(server);
        var comments = await client.GetFooterCommentsAsync(PageId, TestContext.Current.CancellationToken);

        LastRequest(server).Query!["body-format"].ShouldBe(["storage"]);
        LastRequest(server).Query!.Keys.ShouldBe(["body-format"]);

        comments.Count.ShouldBe(1);
        comments[0].Id.ShouldBe("5001");
        comments[0].Kind.ShouldBe(ConfluenceCommentKind.Footer);
        comments[0].AuthorAccountId.ShouldBe("557058:jonas");
        comments[0].CreatedAt.ShouldBe("2026-08-02T14:11:00.000Z");
        comments[0].Body.ShouldBe("<p>Disbursement is instant since the Straumur integration.</p>");
        comments[0].WebUiLink.ShouldBe("/spaces/DOCUMESBX/pages/65601/Domain+model?focusedCommentId=5001");

        // A footer comment is anchored to nothing, and the endpoint carries no properties block at all.
        comments[0].QuotedText.ShouldBeNull();
        comments[0].IsResolved.ShouldBeFalse();
    }

    /// <summary>
    /// Ingestion's inline read: the same shape plus the two members only inline comments have — the
    /// resolution status, and the page text the comment is anchored to (§5.4's <c>quotedText</c>).
    /// </summary>
    [Fact]
    public async Task Reads_inline_comments_with_their_text_and_what_they_are_anchored_to()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(InlineCommentsPath).UsingGet())
            .RespondWith(Json(InlineCommentBodiesBody));

        using var client = CreateClient(server);
        var comments = await client.GetInlineCommentsWithBodiesAsync(
            PageId,
            TestContext.Current.CancellationToken);

        comments.Select(comment => comment.Id).ShouldBe(["6001", "6002"]);
        comments[0].Kind.ShouldBe(ConfluenceCommentKind.Inline);
        comments[0].QuotedText.ShouldBe("Loans are disbursed within 24 hours");
        comments[0].Body.ShouldBe("<p>This is wrong.</p>");
        comments[0].IsResolved.ShouldBeFalse();

        // Resolved comments are returned like any other: what "resolved" means is decided on this side.
        comments[1].ResolutionStatus.ShouldBe("resolved");
        comments[1].IsResolved.ShouldBeTrue();
    }

    /// <summary>
    /// The body format survives pagination. It is the first read on an endpoint that already carries a
    /// query, so the cursor has to be appended rather than replace it — a second page fetched without
    /// <c>body-format</c> would silently arrive with no comment text in it.
    /// </summary>
    [Fact]
    public async Task Keeps_asking_for_the_body_when_it_follows_the_cursor()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(FooterCommentsPath).UsingGet())
            .RespondWith(Json(request => request.Query!.ContainsKey("cursor")
                ? FooterCommentsBody
                : FirstFooterCommentsBody));

        using var client = CreateClient(server);
        var comments = await client.GetFooterCommentsAsync(PageId, TestContext.Current.CancellationToken);

        comments.Select(comment => comment.Id).ShouldBe(["5000", "5001"]);

        var second = server.LogEntries[1].RequestMessage;
        second.ShouldNotBeNull();
        second!.Query!["body-format"].ShouldBe(["storage"]);
        second.Query!["cursor"].ShouldBe(["Y29tbWVudD0y"]);
    }

    /// <summary>
    /// The account the client authenticates as — the one identity ingestion needs, to skip DocuMe's own
    /// replies (§6.3). v1, because v2 has no user endpoints.
    /// </summary>
    [Fact]
    public async Task Reads_the_account_it_authenticates_as()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/wiki/rest/api/user/current").UsingGet())
            .RespondWith(Json(CurrentUserBody));

        using var client = CreateClient(server);
        var user = await client.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        user.AccountId.ShouldBe("557058:docume-bot");
        user.DisplayName.ShouldBe("DocuMe");
    }

    /// <summary>An account id turned into a name — §5.4's <c>author</c>.</summary>
    [Fact]
    public async Task Reads_an_account_by_id()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/wiki/rest/api/user").UsingGet())
            .RespondWith(Json(UserBody));

        using var client = CreateClient(server);
        var user = await client.FindUserAsync("557058:jonas", TestContext.Current.CancellationToken);

        LastRequest(server).Query!["accountId"].ShouldBe(["557058:jonas"]);
        user!.DisplayName.ShouldBe("Jónas");
    }

    /// <summary>
    /// A deactivated, deleted or invisible account answers 404, and that is not a failure: ingestion
    /// records the account id instead. Losing a reviewer's comment because their display name was
    /// unavailable would be the wrong trade.
    /// </summary>
    [Fact]
    public async Task Answers_null_for_an_account_confluence_will_not_name()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/wiki/rest/api/user").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound).WithBody("{}"));

        using var client = CreateClient(server);

        (await client.FindUserAsync("557058:gone", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>
    /// A 401 on the user lookup is still a hard stop (rule §1.2): an expired token is not a missing user,
    /// and blind-retrying or shrugging it off would turn a credential problem into silently anonymous
    /// feedback.
    /// </summary>
    [Fact]
    public async Task Stops_dead_when_the_user_lookup_is_unauthorized()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/wiki/rest/api/user").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Unauthorized).WithBody("{}"));

        using var client = CreateClient(server);

        await Should.ThrowAsync<ConfluenceAuthenticationException>(
            () => client.FindUserAsync("557058:jonas", TestContext.Current.CancellationToken));

        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// The reparent (PLAN.md §6.2): <c>append</c> files the page under the target. The whole request is
    /// its URL, which is the point of using this endpoint over a v2 body rewrite.
    /// </summary>
    [Fact]
    public async Task Moves_a_page_under_a_new_parent_with_the_v1_append_verb()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(MovePath("append")).UsingPut())
            .RespondWith(Json($$"""{"pageId":"{{PageId}}"}"""));

        using var client = CreateClient(server);
        var moved = await client.MovePageAsync(
            new ConfluencePageMove(PageId, ConfluencePageMovePosition.Append, TargetPageId),
            TestContext.Current.CancellationToken);

        moved.ShouldBe(PageId);

        var request = LastRequest(server);
        request.Method.ShouldBe("PUT");
        request.Path.ShouldBe(MovePath("append"));
        request.Headers!["X-Atlassian-Token"].Single().ShouldBe("nocheck");
    }

    /// <summary>
    /// The three positions v1 documents. <c>append</c> reparents; <c>before</c>/<c>after</c> reorder
    /// siblings, which is what §6.2's child-page ordering post-pass is built from.
    /// </summary>
    [Theory]
    [InlineData(ConfluencePageMovePosition.Before, "before")]
    [InlineData(ConfluencePageMovePosition.After, "after")]
    [InlineData(ConfluencePageMovePosition.Append, "append")]
    public async Task Spells_each_position_as_its_own_path_segment(
        ConfluencePageMovePosition position,
        string segment)
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(MovePath(segment)).UsingPut())
            .RespondWith(Json($$"""{"pageId":"{{PageId}}"}"""));

        using var client = CreateClient(server);
        _ = await client.MovePageAsync(
            new ConfluencePageMove(PageId, position, TargetPageId),
            TestContext.Current.CancellationToken);

        LastRequest(server).Path.ShouldBe(MovePath(segment));
    }

    /// <summary>
    /// The finding the move design rests on (§8, rule §9.2): this endpoint carries no request body and
    /// so no version number, which is what keeps a reparent from spending a page version the way a v2
    /// body rewrite would. If Atlassian ever starts wanting a version here, this is the test that says
    /// so rather than a silently churned page history.
    /// </summary>
    [Fact]
    public async Task A_move_sends_no_body_and_so_no_version_number()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(MovePath("append")).UsingPut())
            .RespondWith(Json($$"""{"pageId":"{{PageId}}"}"""));

        using var client = CreateClient(server);
        _ = await client.MovePageAsync(
            new ConfluencePageMove(PageId, ConfluencePageMovePosition.Append, TargetPageId),
            TestContext.Current.CancellationToken);

        var request = LastRequest(server);
        request.Body.ShouldBeNullOrEmpty();
        request.Query!.ShouldBeEmpty();
    }

    /// <summary>
    /// A page whose target vanished and a page that vanished itself arrive as the same 404, so the
    /// message has to name both ids for the caller to tell a re-plan from a recreate.
    /// </summary>
    [Fact]
    public async Task A_missing_page_or_target_surfaces_as_a_404_naming_both()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(MovePath("append")).UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.NotFound)
                .WithBody("""{"message":"No content found with id"}"""));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceApiException>(
            () => client.MovePageAsync(
                new ConfluencePageMove(PageId, ConfluencePageMovePosition.Append, TargetPageId),
                TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var operation = exception.Operation;
        operation.ShouldNotBeNull();
        operation.ShouldContain(PageId);
        operation.ShouldContain(TargetPageId);
        operation.ShouldContain("under");
        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// Moving needs edit permission on both pages, so a token that published happily can still be
    /// refused — and a refusal must stop the run rather than be retried (rule §1.2).
    /// </summary>
    [Fact]
    public async Task A_token_that_cannot_move_stops_rather_than_retrying()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(MovePath("append")).UsingPut())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Forbidden));

        using var client = CreateClient(server);
        _ = await Should.ThrowAsync<ConfluenceAuthenticationException>(
            () => client.MovePageAsync(
                new ConfluencePageMove(PageId, ConfluencePageMovePosition.Append, TargetPageId),
                TestContext.Current.CancellationToken));

        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// A page resolved as its own parent is a caller bug with no documented Confluence behavior, so it
    /// fails before the wire rather than being sent and interpreted.
    /// </summary>
    [Fact]
    public async Task Moving_a_page_relative_to_itself_never_reaches_the_wire()
    {
        using var server = WireMockServer.Start();

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ArgumentException>(
            () => client.MovePageAsync(
                new ConfluencePageMove(PageId, ConfluencePageMovePosition.Append, PageId),
                TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(PageId);
        server.LogEntries.Count.ShouldBe(0);
    }

    /// <summary>
    /// A 200 answering something other than the documented <c>{"pageId": …}</c> means the endpoint
    /// changed under us, which is worth failing loudly over rather than reporting a move that may not
    /// have happened.
    /// </summary>
    [Fact]
    public async Task A_move_response_without_a_page_id_fails_loud()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(MovePath("append")).UsingPut())
            .RespondWith(Json("""{"id":"65601"}"""));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceProtocolException>(
            () => client.MovePageAsync(
                new ConfluencePageMove(PageId, ConfluencePageMovePosition.Append, TargetPageId),
                TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("pageId");
    }

    /// <summary>
    /// The delete half of <c>--prune</c> (PLAN.md §6.2 "Orphans"). Trash, not purge: a bare
    /// <c>DELETE</c> is recoverable from the space trash, and <c>?purge=true</c> is the one thing this
    /// client must never send, because a machine that permanently deletes pages has no undo.
    /// </summary>
    [Fact]
    public async Task Deletes_a_page_to_the_trash_and_never_purges()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath($"pages/{PageId}")).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));

        using var client = CreateClient(server);
        await client.DeletePageAsync(PageId, TestContext.Current.CancellationToken);

        var request = LastRequest(server);
        request.Method.ShouldBe("DELETE");
        request.Path.ShouldBe(ApiPath($"pages/{PageId}"));
        request.Query!.ShouldBeEmpty();
    }

    /// <summary>
    /// A page that is already gone is reported rather than swallowed, matching
    /// <c>RemoveLabelAsync</c>: "already gone" is the state a prune wants, but only the caller knows
    /// that, and a 404 from a mistyped id means something else entirely.
    /// </summary>
    [Fact]
    public async Task A_page_that_is_already_gone_surfaces_as_a_404()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath($"pages/{PageId}")).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound).WithBody("{}"));

        using var client = CreateClient(server);
        var exception = await Should.ThrowAsync<ConfluenceApiException>(
            () => client.DeletePageAsync(PageId, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        exception.Operation!.ShouldContain(PageId);
    }

    /// <summary>
    /// Deleting needs more permission in Confluence than editing, so a token that published happily can
    /// still be refused here — and it must stop the run rather than be retried (rule §1.2).
    /// </summary>
    [Fact]
    public async Task A_token_that_cannot_delete_stops_rather_than_retrying()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(ApiPath($"pages/{PageId}")).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Forbidden));

        using var client = CreateClient(server);
        _ = await Should.ThrowAsync<ConfluenceAuthenticationException>(
            () => client.DeletePageAsync(PageId, TestContext.Current.CancellationToken));

        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// The label search behind <c>sync --labels</c> (PLAN.md §6.3). One CQL request answers "which pages
    /// carry this label" for the whole space; v2 would need one request per page.
    /// </summary>
    [Fact]
    public async Task Searches_a_space_for_the_pages_carrying_a_label()
    {
        using var server = WireMockServer.Start();
        var body = SingleSearchBody;
        server
            .Given(Request.Create().WithPath(SearchPath).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var pages = await client.SearchPagesByLabelAsync(
            "DOCUMESBX",
            "approved",
            TestContext.Current.CancellationToken);

        var page = pages.ShouldHaveSingleItem();
        page.Id.ShouldBe("200001");
        page.Title.ShouldBe("Home");

        // The version comes from expand=version, which is what saves a second request per labelled page.
        page.Version.ShouldBe(5);

        var request = LastRequest(server);
        request.Query!["cql"].Single().ShouldBe("""space = "DOCUMESBX" and label = "approved" and type = page""");
        request.Query["expand"].Single().ShouldBe("version");

        // v1 pages by offset, so unlike the v2 reads a page size has to be sent — the next start is only
        // knowable if this side chose the step.
        request.Query["limit"].Single().ShouldBe("50");
        request.Query["start"].Single().ShouldBe("0");
    }

    /// <summary>
    /// No <c>body-format</c> and no body expansion: rule §9.1 forbids reading Confluence page bodies back
    /// as a content source, and a reconcile needs an id and a version.
    /// </summary>
    [Fact]
    public async Task Never_asks_a_label_search_for_page_bodies()
    {
        using var server = WireMockServer.Start();
        var body = SingleSearchBody;
        server
            .Given(Request.Create().WithPath(SearchPath).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        _ = await client.SearchPagesByLabelAsync("DOCUMESBX", "approved", TestContext.Current.CancellationToken);

        var query = LastRequest(server).Query!;
        query.ShouldNotContainKey("body-format");
        query["expand"].Single().ShouldNotContain("body");
    }

    /// <summary>
    /// v1's pagination is offsets, not v2's cursor, and the stop condition is <c>_links.next</c> going
    /// away rather than a short page: CQL search filters by permission after it pages, so a full result
    /// set can answer fewer rows than the limit and still have more behind it. Stopping short would lose
    /// approvals, which would read as a reviewer revoking one.
    /// </summary>
    [Fact]
    public async Task Follows_search_offsets_until_confluence_stops_offering_a_next_page()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(SearchPath).UsingGet())
            .RespondWith(Json(request =>
                string.Equals(request.Query!["start"].Single(), "0", StringComparison.Ordinal)
                    ? FirstSearchBody
                    : LastSearchBody));

        using var client = CreateClient(server);
        var pages = await client.SearchPagesByLabelAsync(
            "DOCUMESBX",
            "approved",
            TestContext.Current.CancellationToken);

        pages.Select(page => page.Id).ShouldBe(["200001", "200002"]);
        server.LogEntries.Count.ShouldBe(2);

        var followed = server.LogEntries[1].RequestMessage;
        followed.ShouldNotBeNull();
        followed.Query!["start"].Single().ShouldBe("50");
    }

    /// <summary>
    /// A hit whose response carried no version is not a protocol failure: <c>expand</c> is best-effort,
    /// and the caller reads the page by id instead. A failed sync teaches nobody anything.
    /// </summary>
    [Fact]
    public async Task A_search_hit_without_a_version_comes_back_with_a_null_one()
    {
        using var server = WireMockServer.Start();
        var body = VersionlessSearchBody;
        server
            .Given(Request.Create().WithPath(SearchPath).UsingGet())
            .RespondWith(Json(body));

        using var client = CreateClient(server);
        var pages = await client.SearchPagesByLabelAsync(
            "DOCUMESBX",
            "approved",
            TestContext.Current.CancellationToken);

        pages.ShouldHaveSingleItem().Version.ShouldBeNull();
    }

    /// <summary>
    /// Label names are consumer-configured (<c>docume.json → labels</c>, §5.1), so a quote in one would
    /// change the query rather than be searched for. Refused before the request, not escaped.
    /// </summary>
    [Fact]
    public async Task A_label_that_would_break_the_query_is_refused_without_a_request()
    {
        using var server = WireMockServer.Start();

        using var client = CreateClient(server);
        _ = await Should.ThrowAsync<ArgumentException>(() => client.SearchPagesByLabelAsync(
            "DOCUMESBX",
            """approved" or type = blogpost""",
            TestContext.Current.CancellationToken));

        server.LogEntries.ShouldBeEmpty();
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

    private static string MovePath(string position) => LegacyPath($"move/{position}/{TargetPageId}");

    private static string ChildrenPath => ApiPath($"pages/{PageId}/children");

    /// <summary>
    /// A first page of children that offers another. The cursor carries base64 padding, percent-encoded
    /// the way Confluence writes it into <c>_links.next</c>.
    /// </summary>
    private static string FirstChildrenBody =>
        $$"""
        {
          "results": [
            { "id": "222", "status": "current", "title": "Domains", "type": "page",
              "spaceId": "{{SpaceId}}", "childPosition": 3 }
          ],
          "_links": { "next": "/wiki/api/v2/pages/{{PageId}}/children?cursor=cGFnZT0y%3D%3D&limit=25" }
        }
        """;

    /// <summary>The last page: results, and a <c>_links</c> block with no <c>next</c> in it.</summary>
    private static string LastChildrenBody =>
        $$"""
        {
          "results": [
            { "id": "333", "status": "current", "title": "Guides", "type": "page",
              "spaceId": "{{SpaceId}}", "childPosition": 7 },
            { "id": "111", "status": "current", "title": "Migrated page", "type": "page",
              "spaceId": "{{SpaceId}}", "childPosition": null }
          ],
          "_links": { "base": "https://example.atlassian.net/wiki" }
        }
        """;

    private static string InlineCommentsPath => ApiPath($"pages/{PageId}/inline-comments");

    /// <summary>A first page of inline comments that offers another.</summary>
    private static string FirstCommentsBody =>
        $$"""
        {
          "results": [
            { "id": "4001", "status": "current", "title": "Re: Domain model", "pageId": "{{PageId}}",
              "resolutionStatus": "open",
              "_links": { "webui": "/spaces/DOCUMESBX/pages/{{PageId}}/Domain+model?focusedCommentId=4001" } }
          ],
          "_links": { "next": "/wiki/api/v2/pages/{{PageId}}/inline-comments?cursor=Y29tbWVudD0y" }
        }
        """;

    /// <summary>
    /// The last page: a resolved comment, and one that carries no <c>resolutionStatus</c> at all — the
    /// shape Atlassian's published schema for this endpoint actually documents.
    /// </summary>
    private static string LastCommentsBody =>
        $$"""
        {
          "results": [
            { "id": "4002", "status": "current", "title": "Re: Domain model", "pageId": "{{PageId}}",
              "resolutionStatus": "resolved",
              "_links": { "webui": "/spaces/DOCUMESBX/pages/{{PageId}}/Domain+model?focusedCommentId=4002" } },
            { "id": "4003", "status": "current", "title": "Re: Domain model", "pageId": "{{PageId}}" }
          ],
          "_links": { "base": "https://example.atlassian.net/wiki" }
        }
        """;

    private static string FooterCommentsPath => ApiPath($"pages/{PageId}/footer-comments");

    /// <summary>
    /// One footer comment with its text, shaped like the v2 <c>PageCommentModel</c>: the author and the
    /// creation time live on the comment's <em>version</em>, which is the only place Confluence puts them.
    /// </summary>
    private static string FooterCommentsBody =>
        $$"""
        {
          "results": [
            { "id": "5001", "status": "current", "title": "Re: Domain model", "pageId": "{{PageId}}",
              "version": { "number": 1, "createdAt": "2026-08-02T14:11:00.000Z", "minorEdit": false,
                           "authorId": "557058:jonas" },
              "body": { "storage": { "representation": "storage",
                        "value": "<p>Disbursement is instant since the Straumur integration.</p>" } },
              "_links": { "webui": "/spaces/DOCUMESBX/pages/{{PageId}}/Domain+model?focusedCommentId=5001" } }
          ],
          "_links": { "base": "https://example.atlassian.net/wiki" }
        }
        """;

    /// <summary>A first page of footer comments that offers another.</summary>
    private static string FirstFooterCommentsBody =>
        $$"""
        {
          "results": [
            { "id": "5000", "status": "current", "title": "Re: Domain model", "pageId": "{{PageId}}",
              "version": { "number": 1, "createdAt": "2026-08-01T09:00:00.000Z", "authorId": "557058:jonas" },
              "body": { "storage": { "representation": "storage", "value": "<p>First.</p>" } } }
          ],
          "_links": { "next": "/wiki/api/v2/pages/{{PageId}}/footer-comments?body-format=storage&cursor=Y29tbWVudD0y" }
        }
        """;

    /// <summary>
    /// Two inline comments with bodies: one open with the text it is anchored to, one resolved. The
    /// anchored text is <c>properties.inlineOriginalSelection</c>, which is §5.4's <c>quotedText</c>.
    /// </summary>
    private static string InlineCommentBodiesBody =>
        $$"""
        {
          "results": [
            { "id": "6001", "status": "current", "title": "Re: Domain model", "pageId": "{{PageId}}",
              "resolutionStatus": "open",
              "version": { "number": 1, "createdAt": "2026-08-02T14:11:00.000Z", "authorId": "557058:jonas" },
              "body": { "storage": { "representation": "storage", "value": "<p>This is wrong.</p>" } },
              "properties": { "inlineMarkerRef": "abc123",
                              "inlineOriginalSelection": "Loans are disbursed within 24 hours" } },
            { "id": "6002", "status": "current", "title": "Re: Domain model", "pageId": "{{PageId}}",
              "resolutionStatus": "resolved",
              "version": { "number": 1, "createdAt": "2026-08-02T15:00:00.000Z", "authorId": "557058:jonas" },
              "body": { "storage": { "representation": "storage", "value": "<p>Handled.</p>" } } }
          ],
          "_links": { "base": "https://example.atlassian.net/wiki" }
        }
        """;

    /// <summary>The v1 user shape. <c>email</c> is answered and deliberately not mapped.</summary>
    private static string CurrentUserBody =>
        """
        {
          "type": "known", "accountId": "557058:docume-bot", "accountType": "atlassian",
          "email": "bot@example.com", "publicName": "DocuMe", "displayName": "DocuMe"
        }
        """;

    private static string UserBody =>
        """
        {
          "type": "known", "accountId": "557058:jonas", "accountType": "atlassian",
          "email": "jonas@example.com", "publicName": "Jónas", "displayName": "Jónas"
        }
        """;

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

    /// <summary>A response that depends on the request — one endpoint answering two pages of results.</summary>
    private static IResponseBuilder Json(Func<IRequestMessage, string> body)
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

    /// <summary>
    /// The v1 CQL search root. Not under <c>content/{id}</c> like the other v1 paths, so it is spelled out
    /// rather than built from <see cref="LegacyPath"/>.
    /// </summary>
    private static string SearchPath => "/wiki/rest/api/content/search";

    /// <summary>
    /// One page of hits and no more, with v1's own <c>start</c>/<c>limit</c>/<c>size</c> members alongside
    /// the <c>results</c> array the v2 reads share.
    /// </summary>
    private static string SingleSearchBody =>
        """
        {
          "results": [
            { "id": "200001", "type": "page", "status": "current", "title": "Home",
              "version": { "number": 5 } }
          ],
          "start": 0, "limit": 50, "size": 1,
          "_links": { "base": "https://example.atlassian.net/wiki" }
        }
        """;

    /// <summary>The same first page, but offering another.</summary>
    private static string FirstSearchBody =>
        """
        {
          "results": [
            { "id": "200001", "type": "page", "status": "current", "title": "Home",
              "version": { "number": 5 } }
          ],
          "start": 0, "limit": 50, "size": 1,
          "_links": { "next": "/wiki/rest/api/content/search?cql=label%3Dapproved&limit=50&start=50" }
        }
        """;

    /// <summary>The last page: hits, and a <c>_links</c> block with no <c>next</c> in it.</summary>
    private static string LastSearchBody =>
        """
        {
          "results": [
            { "id": "200002", "type": "page", "status": "current", "title": "Guides",
              "version": { "number": 2 } }
          ],
          "start": 50, "limit": 50, "size": 1,
          "_links": { "base": "https://example.atlassian.net/wiki" }
        }
        """;

    /// <summary>A hit the response carried no <c>version</c> for, which the caller resolves by id.</summary>
    private static string VersionlessSearchBody =>
        """
        {
          "results": [
            { "id": "200001", "type": "page", "status": "current", "title": "Home" }
          ],
          "start": 0, "limit": 50, "size": 1,
          "_links": {}
        }
        """;

    private static string EmptyResultsBody => """{ "results": [], "_links": {} }""";

    private static string NoResultsBody => """{ "_links": {} }""";
}
