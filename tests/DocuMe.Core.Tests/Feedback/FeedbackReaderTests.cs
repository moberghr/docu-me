using System.Net;
using DocuMe.Core.Confluence;
using DocuMe.Core.Feedback;
using DocuMe.Core.State;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Feedback;

/// <summary>
/// The comment read (PLAN.md §6.3's Comments bullet) against a real local HTTP server
/// (.claude/rules/testing.md §4.2): which pages it asks about, which identities it resolves, and how much
/// Confluence one run costs.
/// </summary>
/// <remarks>
/// The request <em>count</em> is as much the subject here as the observation: an author lookup per comment
/// rather than per author would turn a nightly sync over a wiki into hundreds of requests, and only the
/// request log proves which one happens. The decision half — what becomes an inbox item — is pure and
/// lives in <see cref="FeedbackInboxPlannerTests"/>.
/// </remarks>
public sealed class FeedbackReaderTests
{
    private const string LoansPath = "10-domains/loans/README.md";
    private const string GuidesPath = "20-guides/onboarding.md";
    private const string LoansPageId = "65601";
    private const string GuidesPageId = "65602";
    private const string BotAccount = "557058:docume-bot";
    private const string HumanAccount = "557058:jonas";

    private static readonly ConfluenceCredentials Credentials = new("bot@example.com", "token");

    private static readonly IReadOnlySet<string> NoItems =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The work list is state's page map: two reads per published page, none for a page that has never
    /// been published, and no space search anywhere — a comment is only feedback about a wiki page if
    /// DocuMe published that page.
    /// </summary>
    [Fact]
    public async Task Reads_both_comment_collections_for_every_published_page()
    {
        using var server = WireMockServer.Start();
        StubCurrentUser(server);
        StubUser(server, HumanAccount, "Jónas");
        StubComments(server, LoansPageId, footer: FooterBody("5001", HumanAccount), inline: Empty);
        StubComments(server, GuidesPageId, footer: Empty, inline: InlineBody("6001", HumanAccount));

        using var client = CreateClient(server);
        var read = await FeedbackReader.ReadAsync(
            client,
            State(),
            NoItems,
            TestContext.Current.CancellationToken);

        read.PagesRead.ShouldBe(2);
        read.PagesSkipped.ShouldBe(1);
        read.CommentsRead.ShouldBe(2);

        read.Observation.Pages.Select(page => page.Path).ShouldBe([LoansPath, GuidesPath]);
        read.Observation.Pages[0].Cursor.ShouldBe("2026-08-01T10:00:00.000Z");
        read.Observation.Pages[0].Comments.Single().Kind.ShouldBe("footer");
        read.Observation.Pages[1].Comments.Single().Kind.ShouldBe("inline");
        read.Observation.Pages[1].Comments.Single().QuotedText.ShouldBe("disbursed within 24 hours");

        // Nothing was asked about the page with no pageId.
        Paths(server)
            .ShouldNotContain(path => path.Contains("65603", StringComparison.Ordinal));
    }

    /// <summary>
    /// §6.3's bot rule needs one identity, and the reader fetches it once per run before any comment read.
    /// The account it reports is what the planner compares each comment's author against.
    /// </summary>
    [Fact]
    public async Task Reports_the_account_it_authenticates_as_so_its_own_replies_can_be_skipped()
    {
        using var server = WireMockServer.Start();
        StubCurrentUser(server);
        StubComments(server, LoansPageId, footer: FooterBody("5001", BotAccount), inline: Empty);
        StubComments(server, GuidesPageId, footer: Empty, inline: Empty);

        using var client = CreateClient(server);
        var read = await FeedbackReader.ReadAsync(
            client,
            State(),
            NoItems,
            TestContext.Current.CancellationToken);

        read.Observation.BotAccountId.ShouldBe(BotAccount);

        var plan = FeedbackInboxPlanner.Plan(read.Observation);
        plan.Items.ShouldBeEmpty();
        plan.Skipped.Single().Reason.ShouldBe(FeedbackSkipReason.Bot);

        // The bot's own name came from the current-user read, so its comments cost no lookup at all.
        read.AuthorsResolved.ShouldBe(0);
        UserLookups(server).ShouldBe(0);
    }

    /// <summary>
    /// One lookup per distinct author, not per comment: three comments by one person over two pages cost a
    /// single request, which is what keeps a nightly sync over ~80 pages cheap.
    /// </summary>
    [Fact]
    public async Task Looks_an_author_up_once_however_many_comments_they_wrote()
    {
        using var server = WireMockServer.Start();
        StubCurrentUser(server);
        StubUser(server, HumanAccount, "Jónas");
        StubComments(
            server,
            LoansPageId,
            footer: FooterBody("5001", HumanAccount, "5002"),
            inline: InlineBody("6001", HumanAccount));
        StubComments(server, GuidesPageId, footer: Empty, inline: Empty);

        using var client = CreateClient(server);
        var read = await FeedbackReader.ReadAsync(
            client,
            State(),
            NoItems,
            TestContext.Current.CancellationToken);

        read.CommentsRead.ShouldBe(3);
        read.AuthorsResolved.ShouldBe(1);
        UserLookups(server).ShouldBe(1);

        read.Observation.Pages[0].Comments.ShouldAllBe(comment => comment.AuthorDisplayName == "Jónas");
    }

    /// <summary>
    /// An account Confluence will not name costs one 404 and no more, and the item records the account id
    /// instead of losing the comment (<see cref="FeedbackInboxPlanner"/>).
    /// </summary>
    [Fact]
    public async Task Falls_back_to_the_account_id_when_confluence_will_not_name_the_author()
    {
        using var server = WireMockServer.Start();
        StubCurrentUser(server);
        server
            .Given(Request.Create().WithPath("/wiki/rest/api/user").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound).WithBody("{}"));

        StubComments(server, LoansPageId, footer: FooterBody("5001", HumanAccount, "5002"), inline: Empty);
        StubComments(server, GuidesPageId, footer: Empty, inline: Empty);

        using var client = CreateClient(server);
        var read = await FeedbackReader.ReadAsync(
            client,
            State(),
            NoItems,
            TestContext.Current.CancellationToken);

        UserLookups(server).ShouldBe(1);

        var plan = FeedbackInboxPlanner.Plan(read.Observation);
        plan.Items.Count.ShouldBe(2);
        plan.Items.ShouldAllBe(item => item.Item.Author == HumanAccount);
    }

    /// <summary>
    /// A wiki nothing has been published from costs the current-user read and nothing else: there is no
    /// page for a comment to be on.
    /// </summary>
    [Fact]
    public async Task Reads_no_comments_when_no_page_has_been_published()
    {
        using var server = WireMockServer.Start();
        StubCurrentUser(server);

        using var client = CreateClient(server);
        var read = await FeedbackReader.ReadAsync(
            client,
            new DocumeState
            {
                Pages = new Dictionary<string, PageState>(StringComparer.Ordinal) { [LoansPath] = new() },
            },
            NoItems,
            TestContext.Current.CancellationToken);

        read.PagesRead.ShouldBe(0);
        read.PagesSkipped.ShouldBe(1);
        server.LogEntries.Count.ShouldBe(1);
    }

    /// <summary>
    /// Rule §1.2: an expired token stops the run rather than producing an empty read that would look like
    /// "nobody commented".
    /// </summary>
    [Fact]
    public async Task Stops_dead_when_the_token_is_rejected()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/wiki/rest/api/user/current").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Forbidden).WithBody("{}"));

        using var client = CreateClient(server);

        await Should.ThrowAsync<ConfluenceAuthenticationException>(
            () => FeedbackReader.ReadAsync(client, State(), NoItems, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Two published pages — one with a cursor, one without — and a third state has never published.
    /// </summary>
    private static DocumeState State() => new()
    {
        Pages = new Dictionary<string, PageState>(StringComparer.Ordinal)
        {
            [LoansPath] = new()
            {
                PageId = LoansPageId,
                Title = "Loans Domain",
                FeedbackCursor = "2026-08-01T10:00:00.000Z",
            },
            [GuidesPath] = new() { PageId = GuidesPageId, Title = "Onboarding" },
            ["30-drafts/unpublished.md"] = new(),
        },
    };

    /// <summary>How many author lookups the run cost — the number this suite is really about.</summary>
    private static int UserLookups(WireMockServer server) => server.LogEntries.Count(entry =>
        string.Equals(entry.RequestMessage?.Path, "/wiki/rest/api/user", StringComparison.Ordinal));

    private static IEnumerable<string> Paths(WireMockServer server)
        => server.LogEntries.Select(entry => entry.RequestMessage?.Path ?? string.Empty);

    private static void StubCurrentUser(WireMockServer server) => server
        .Given(Request.Create().WithPath("/wiki/rest/api/user/current").UsingGet())
        .RespondWith(Json($$"""
            { "accountId": "{{BotAccount}}", "displayName": "DocuMe" }
            """));

    private static void StubUser(WireMockServer server, string accountId, string displayName) => server
        .Given(Request.Create().WithPath("/wiki/rest/api/user").UsingGet())
        .RespondWith(Json($$"""
            { "accountId": "{{accountId}}", "displayName": "{{displayName}}" }
            """));

    private static void StubComments(WireMockServer server, string pageId, string footer, string inline)
    {
        server
            .Given(Request.Create().WithPath($"/wiki/api/v2/pages/{pageId}/footer-comments").UsingGet())
            .RespondWith(Json(footer));

        server
            .Given(Request.Create().WithPath($"/wiki/api/v2/pages/{pageId}/inline-comments").UsingGet())
            .RespondWith(Json(inline));
    }

    private static string Empty => """{ "results": [], "_links": {} }""";

    /// <summary>One or two footer comments by <paramref name="authorId"/>, with bodies.</summary>
    private static string FooterBody(string id, string authorId, string? secondId = null)
    {
        var second = secondId is null
            ? string.Empty
            : $$"""
                ,
                { "id": "{{secondId}}", "status": "current", "pageId": "{{LoansPageId}}",
                  "version": { "number": 1, "createdAt": "2026-08-03T09:00:00.000Z", "authorId": "{{authorId}}" },
                  "body": { "storage": { "representation": "storage", "value": "<p>And another.</p>" } } }
                """;

        return $$"""
            {
              "results": [
                { "id": "{{id}}", "status": "current", "pageId": "{{LoansPageId}}",
                  "version": { "number": 1, "createdAt": "2026-08-02T14:11:00.000Z", "authorId": "{{authorId}}" },
                  "body": { "storage": { "representation": "storage",
                            "value": "<p>Disbursement is instant.</p>" } } }{{second}}
              ],
              "_links": {}
            }
            """;
    }

    private static string InlineBody(string id, string authorId) =>
        $$"""
        {
          "results": [
            { "id": "{{id}}", "status": "current", "pageId": "{{GuidesPageId}}", "resolutionStatus": "open",
              "version": { "number": 1, "createdAt": "2026-08-02T14:11:00.000Z", "authorId": "{{authorId}}" },
              "body": { "storage": { "representation": "storage", "value": "<p>This is wrong.</p>" } },
              "properties": { "inlineOriginalSelection": "disbursed within 24 hours" } }
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
