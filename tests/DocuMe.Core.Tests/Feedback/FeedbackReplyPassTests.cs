using System.Net;
using System.Text.Json;
using DocuMe.Core.Confluence;
using DocuMe.Core.Feedback;
using DocuMe.Core.State;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Feedback;

/// <summary>
/// The reply pass end to end (PLAN.md §9 step 5) against a real local HTTP server and real files: what
/// it reads, in what order it writes, and what it leaves behind when Confluence says no.
/// </summary>
/// <remarks>
/// The <em>order</em> of the writes is as much the subject here as their content. Post, stamp, close is
/// the sequence that makes a crashed run cost a missing resolve rather than a duplicate reply, and only
/// the request log plus the file on disk can show which one happens. The decision half is pure and lives
/// in <see cref="FeedbackReplyPlannerTests"/>.
/// </remarks>
public sealed class FeedbackReplyPassTests : IDisposable
{
    private const string LoansPath = "10-domains/loans/README.md";
    private const string GuidesPath = "20-guides/onboarding.md";
    private const string LoansPageId = "65601";
    private const string GuidesPageId = "65602";

    private static readonly ConfluenceCredentials Credentials = new("bot@example.com", "token");

    private static readonly DateTimeOffset RepliedAt =
        new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("docume-reply-tests").FullName;

    private string Inbox => Path.Combine(_root, "inbox");

    private string Archive => Path.Combine(_root, "archive");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// The whole loop closing: a triaged item in the archive, a reply posted under its inline comment,
    /// the comment resolved at the next version, and the item stamped so it is never answered again.
    /// </summary>
    [Fact]
    public async Task Answers_a_triaged_item_resolves_its_comment_and_stamps_it()
    {
        WriteItem(Archive, "loans-6001.json", Item("6001", LoansPath, FeedbackStatus.Fixed));

        using var server = WireMockServer.Start();
        StubComments(server, LoansPageId, Empty, InlineBody("6001", version: 3));
        StubReply(server);
        StubResolve(server, "6001");

        var result = await RunAsync(server);

        result.Posted.ShouldBe(1);
        result.Resolved.ShouldBe(1);
        result.Failures.ShouldBeEmpty();

        var reply = Payload(server, "/wiki/api/v2/inline-comments");
        reply.GetProperty("parentCommentId").GetString().ShouldBe("6001");

        var body = reply.GetProperty("body").GetProperty("storage").GetProperty("value").GetString();
        body.ShouldNotBeNull();
        body.ShouldContain("Fixed in the latest version");

        Payload(server, "/wiki/api/v2/inline-comments/6001")
            .GetProperty("version").GetProperty("number").GetInt32().ShouldBe(4);

        Stamp(Archive, "loans-6001.json").ShouldBe("2026-08-03T09:00:00.000Z");
    }

    /// <summary>
    /// The reply is posted before the comment is closed. Closing first would risk retiring a reviewer's
    /// question unanswered if the reply then failed, so the order is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task Posts_the_reply_before_it_closes_the_comment()
    {
        WriteItem(Archive, "loans-6001.json", Item("6001", LoansPath, FeedbackStatus.Fixed));

        using var server = WireMockServer.Start();
        StubComments(server, LoansPageId, Empty, InlineBody("6001", version: 3));
        StubReply(server);
        StubResolve(server, "6001");

        await RunAsync(server);

        var writes = Writes(server);
        writes.ShouldBe(["/wiki/api/v2/inline-comments", "/wiki/api/v2/inline-comments/6001"]);
    }

    /// <summary>
    /// The stamp is what makes the pass idempotent: run it twice and the second run posts nothing, which
    /// is the difference between closing the loop and re-thanking a reviewer on every cron.
    /// </summary>
    [Fact]
    public async Task Posts_nothing_the_second_time_it_runs()
    {
        WriteItem(Inbox, "loans-5001.json", Item("5001", LoansPath, FeedbackStatus.Fixed));

        using var server = WireMockServer.Start();
        StubComments(server, LoansPageId, FooterBody("5001"), Empty);
        StubReply(server);

        (await RunAsync(server)).Posted.ShouldBe(1);

        var second = await RunAsync(server);

        second.Posted.ShouldBe(0);
        Writes(server).Count.ShouldBe(1);
    }

    /// <summary>
    /// A footer comment gets its reply on the footer endpoint and no resolve attempt at all — the
    /// collection has no resolution state to set.
    /// </summary>
    [Fact]
    public async Task Replies_to_a_footer_comment_without_a_resolve()
    {
        WriteItem(Inbox, "loans-5001.json", Item("5001", LoansPath, FeedbackStatus.Question));

        using var server = WireMockServer.Start();
        StubComments(server, LoansPageId, FooterBody("5001"), Empty);
        StubReply(server);

        var result = await RunAsync(server);

        result.Posted.ShouldBe(1);
        result.Resolved.ShouldBe(0);
        Writes(server).ShouldBe(["/wiki/api/v2/footer-comments"]);
    }

    /// <summary>
    /// Only the pages that still owe a reply are read. An archive full of answered items must not turn a
    /// nightly reply pass into a full comment sweep of the wiki.
    /// </summary>
    [Fact]
    public async Task Reads_only_the_pages_that_still_owe_a_reply()
    {
        WriteItem(Inbox, "loans-5001.json", Item("5001", LoansPath, FeedbackStatus.Fixed));
        WriteItem(
            Archive,
            "guides-5002.json",
            Item("5002", GuidesPath, FeedbackStatus.Fixed) with { RepliedAt = "2026-08-01T09:00:00.000Z" });

        using var server = WireMockServer.Start();
        StubComments(server, LoansPageId, FooterBody("5001"), Empty);
        StubReply(server);

        var result = await RunAsync(server);

        result.Posted.ShouldBe(1);
        Paths(server).ShouldNotContain(path => path.Contains(GuidesPageId, StringComparison.Ordinal));
    }

    /// <summary>
    /// A reply Confluence rejects leaves the item unstamped, so the next run tries again rather than the
    /// reviewer's comment quietly reading as answered. The rest of the plan still runs.
    /// </summary>
    [Fact]
    public async Task Leaves_an_item_unstamped_when_its_reply_is_rejected()
    {
        WriteItem(Inbox, "loans-5001.json", Item("5001", LoansPath, FeedbackStatus.Fixed));

        using var server = WireMockServer.Start();
        StubComments(server, LoansPageId, FooterBody("5001"), Empty);
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/footer-comments").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound).WithBody("{}"));

        var result = await RunAsync(server);

        result.Posted.ShouldBe(0);
        result.Failures.Single().Replied.ShouldBeFalse();
        result.Failures.Single().CommentId.ShouldBe("5001");

        Stamp(Inbox, "loans-5001.json").ShouldBeNull();
    }

    /// <summary>
    /// A failed <em>close</em> is the opposite case: the reply landed, so the stamp stays. Re-running to
    /// retry the close would post the reply a second time, which is worse than an inline comment a human
    /// has to tick.
    /// </summary>
    [Fact]
    public async Task Keeps_the_stamp_when_the_reply_landed_but_the_close_failed()
    {
        WriteItem(Inbox, "loans-6001.json", Item("6001", LoansPath, FeedbackStatus.Fixed));

        using var server = WireMockServer.Start();
        StubComments(server, LoansPageId, Empty, InlineBody("6001", version: 3));
        StubReply(server);
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/inline-comments/6001").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Conflict).WithBody("{}"));

        var result = await RunAsync(server);

        result.Posted.ShouldBe(1);
        result.Resolved.ShouldBe(0);
        result.Failures.Single().Replied.ShouldBeTrue();

        Stamp(Inbox, "loans-6001.json").ShouldBe("2026-08-03T09:00:00.000Z");
    }

    /// <summary>
    /// Rule §1.2: an expired token stops the run instead of being replayed across every remaining item,
    /// and the report says how far it got.
    /// </summary>
    [Fact]
    public async Task Stops_the_whole_run_when_the_token_is_rejected()
    {
        WriteItem(Inbox, "a-5001.json", Item("5001", LoansPath, FeedbackStatus.Fixed));
        WriteItem(Inbox, "b-5002.json", Item("5002", LoansPath, FeedbackStatus.Fixed));

        using var server = WireMockServer.Start();
        StubComments(server, LoansPageId, FooterBody("5001", "5002"), Empty);
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/footer-comments").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Forbidden).WithBody("{}"));

        var result = await RunAsync(server);

        result.Posted.ShouldBe(0);
        result.Failures.Count.ShouldBe(1);
        result.StoppedBecause.ShouldNotBeNull();

        // The second item was never attempted, which is the whole point of stopping.
        Writes(server).Count.ShouldBe(1);
    }

    /// <summary>
    /// An item whose comment somebody deleted is reported and left alone: unstamped, because nothing was
    /// said to anybody, and unanswered, because there is nobody left to answer.
    /// </summary>
    [Fact]
    public async Task Reports_an_item_whose_comment_is_gone_and_writes_nothing()
    {
        WriteItem(Inbox, "loans-5001.json", Item("5001", LoansPath, FeedbackStatus.Fixed));

        using var server = WireMockServer.Start();
        StubComments(server, LoansPageId, Empty, Empty);

        var read = await FeedbackReplyReader.ReadAsync(
            CreateClient(server),
            State(),
            [Inbox, Archive],
            TestContext.Current.CancellationToken);

        var plan = FeedbackReplyPlanner.Plan(read.Observation);

        plan.HasChanges.ShouldBeFalse();
        plan.Skipped.Single().Reason.ShouldBe(FeedbackReplySkipReason.CommentGone);
        Stamp(Inbox, "loans-5001.json").ShouldBeNull();
    }

    /// <summary>
    /// An item about a page state has no <c>pageId</c> for is reported as such rather than as a deleted
    /// comment, and costs no request.
    /// </summary>
    [Fact]
    public async Task Reports_an_item_about_a_page_that_was_never_published()
    {
        WriteItem(Inbox, "draft-5001.json", Item("5001", "30-drafts/unpublished.md", FeedbackStatus.Fixed));

        using var server = WireMockServer.Start();

        var read = await FeedbackReplyReader.ReadAsync(
            CreateClient(server),
            State(),
            [Inbox, Archive],
            TestContext.Current.CancellationToken);

        read.PagesUnpublished.ShouldBe(1);
        server.LogEntries.ShouldBeEmpty();

        FeedbackReplyPlanner.Plan(read.Observation)
            .Skipped.Single().Reason.ShouldBe(FeedbackReplySkipReason.PageNotPublished);
    }

    private async Task<FeedbackReplyResult> RunAsync(WireMockServer server)
    {
        using var client = CreateClient(server);

        var read = await FeedbackReplyReader.ReadAsync(
            client,
            State(),
            [Inbox, Archive],
            TestContext.Current.CancellationToken);

        var plan = FeedbackReplyPlanner.Plan(read.Observation);

        return await FeedbackReplyExecutor.ExecuteAsync(
            client,
            plan,
            RepliedAt,
            TestContext.Current.CancellationToken);
    }

    private void WriteItem(string directory, string name, FeedbackItem item)
    {
        Directory.CreateDirectory(directory);
        FeedbackInbox.Write(directory, new FeedbackIngestPlan([new(item.Page!, name, item)], [], []));
    }

    private string? Stamp(string directory, string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name)));

        return document.RootElement.TryGetProperty("repliedAt", out var replied)
            ? replied.GetString()
            : null;
    }

    private static FeedbackItem Item(string commentId, string page, string status) => new()
    {
        Id = FeedbackItemId.ForConfluenceComment(commentId),
        Page = page,
        Kind = FeedbackKind.Footer,
        Author = "Jónas",
        CreatedAt = "2026-08-02T14:11:00.000Z",
        Body = "<p>A claim to verify.</p>",
        Status = status,
    };

    private static DocumeState State() => new()
    {
        Pages = new Dictionary<string, PageState>(StringComparer.Ordinal)
        {
            [LoansPath] = new() { PageId = LoansPageId, Title = "Loans Domain" },
            [GuidesPath] = new() { PageId = GuidesPageId, Title = "Onboarding" },
            ["30-drafts/unpublished.md"] = new(),
        },
    };

    /// <summary>The paths of the requests that changed something, in the order they were sent.</summary>
    private static List<string> Writes(WireMockServer server) => server.LogEntries
        .Where(entry => entry.RequestMessage?.Method is "POST" or "PUT")
        .Select(entry => entry.RequestMessage!.Path)
        .ToList();

    private static IEnumerable<string> Paths(WireMockServer server)
        => server.LogEntries.Select(entry => entry.RequestMessage?.Path ?? string.Empty);

    private static JsonElement Payload(WireMockServer server, string path)
    {
        var request = server.LogEntries
            .Select(entry => entry.RequestMessage)
            .Last(message => string.Equals(message?.Path, path, StringComparison.Ordinal));

        using var document = JsonDocument.Parse(request!.Body!);

        return document.RootElement.Clone();
    }

    private static void StubComments(WireMockServer server, string pageId, string footer, string inline)
    {
        server
            .Given(Request.Create().WithPath($"/wiki/api/v2/pages/{pageId}/footer-comments").UsingGet())
            .RespondWith(Json(footer));

        server
            .Given(Request.Create().WithPath($"/wiki/api/v2/pages/{pageId}/inline-comments").UsingGet())
            .RespondWith(Json(inline));
    }

    private static void StubReply(WireMockServer server)
    {
        const string body = """
            { "id": "7001", "status": "current", "pageId": "65601",
              "version": { "number": 1, "createdAt": "2026-08-03T09:00:00.000Z" },
              "body": { "storage": { "representation": "storage", "value": "<p>Thanks.</p>" } } }
            """;

        server
            .Given(Request.Create().WithPath("/wiki/api/v2/footer-comments").UsingPost())
            .RespondWith(Json(body));

        server
            .Given(Request.Create().WithPath("/wiki/api/v2/inline-comments").UsingPost())
            .RespondWith(Json(body));
    }

    private static void StubResolve(WireMockServer server, string commentId) => server
        .Given(Request.Create().WithPath($"/wiki/api/v2/inline-comments/{commentId}").UsingPut())
        .RespondWith(Json($$"""
            { "id": "{{commentId}}", "status": "current", "pageId": "65601", "resolutionStatus": "resolved",
              "version": { "number": 4, "createdAt": "2026-08-03T09:00:00.000Z" } }
            """));

    private static string Empty => """{ "results": [], "_links": {} }""";

    private static string FooterBody(string id, string? secondId = null)
    {
        var second = secondId is null
            ? string.Empty
            : $$"""
                ,
                { "id": "{{secondId}}", "status": "current", "pageId": "{{LoansPageId}}",
                  "version": { "number": 1, "createdAt": "2026-08-02T15:00:00.000Z" },
                  "body": { "storage": { "representation": "storage", "value": "<p>And another.</p>" } } }
                """;

        return $$"""
            {
              "results": [
                { "id": "{{id}}", "status": "current", "pageId": "{{LoansPageId}}",
                  "version": { "number": 1, "createdAt": "2026-08-02T14:11:00.000Z" },
                  "body": { "storage": { "representation": "storage", "value": "<p>Wrong.</p>" } } }{{second}}
              ],
              "_links": {}
            }
            """;
    }

    private static string InlineBody(string id, int version) =>
        $$"""
        {
          "results": [
            { "id": "{{id}}", "status": "current", "pageId": "{{LoansPageId}}", "resolutionStatus": "open",
              "version": { "number": {{version}}, "createdAt": "2026-08-02T14:11:00.000Z" },
              "body": { "storage": { "representation": "storage", "value": "<p>Wrong.</p>" } } }
          ],
          "_links": {}
        }
        """;

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
