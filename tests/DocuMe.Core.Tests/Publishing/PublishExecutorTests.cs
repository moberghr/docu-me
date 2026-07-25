using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Markdown;
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
/// The publish write path (PLAN.md §6.2 steps 5-8) against a real local HTTP server
/// (.claude/rules/testing.md §4.2) and a fake renderer, so no Node process and no Confluence account
/// are involved.
/// </summary>
/// <remarks>
/// Every run here starts from <see cref="PublishPipeline.Plan"/> over a real fixture wiki rather than a
/// hand-built report: the pairing that matters is plan → execute → state → next plan, and a hand-built
/// plan would let the two halves drift apart while the tests stayed green.
/// </remarks>
public sealed class PublishExecutorTests : IDisposable
{
    private const string Email = "bot@example.com";
    private const string ApiToken = "very-secret-token";
    private const string SpaceKey = "DOCUMESBX";
    private const string SpaceId = "98304";
    private const string RootPageId = "131074";
    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg"><rect width="4" height="4"/></svg>""";
    private const string LogoAttachment = "images_logo.png";

    private static readonly ConfluenceCredentials Credentials = new(Email, ApiToken);

    private static readonly byte[] LogoBytes = [1, 2, 3, 4];

    private readonly string _dir = Directory.CreateTempSubdirectory("docume-executor-tests").FullName;

    /// <summary>How many times the fake renderer was asked to render, per distinct source.</summary>
    private readonly List<string> _rendered = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PublishExecutorTests"/> class with the fixture wiki
    /// a first publish sees: a home page with an image, and a child page with an image and a diagram.
    /// </summary>
    public PublishExecutorTests()
    {
        const string home = """
            # Home

            See the [Setup Guide](guides/setup.md) and the ![logo](images/logo.png).
            """;

        Write("README.md", home);
        Write("guides/setup.md", SetupPage);
        File.WriteAllBytes(Materialize("images/logo.png"), LogoBytes);
    }

    private static string SetupPage =>
        """
        ---
        title: Setup Guide
        ---

        # Setup

        ```mermaid
        graph TD
          A --> B
        ```

        ![logo](../images/logo.png)
        """;

    private static string DiagramAttachment => MermaidAttachmentName.ForSource("graph TD\n  A --> B");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Creates_every_page_parents_first_and_records_what_it_published()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var outcome = await ExecuteAsync(server, new DocumeState(), repoSha: "c0ffee");

        outcome.Succeeded.ShouldBeTrue();
        outcome.StoppedBecause.ShouldBeNull();
        outcome.CreatedCount.ShouldBe(2);
        outcome.UpdatedCount.ShouldBe(0);

        // README uploads the logo; setup.md uploads the logo and its rendered diagram.
        outcome.UploadedAttachmentCount.ShouldBe(3);

        var creates = Requests(server, "POST", "/wiki/api/v2/pages");
        creates.Count.ShouldBe(2);

        // Order is the plan's order, which is tree order, which puts a parent before its children.
        var home = Payload(creates[0]);
        home.GetProperty("title").GetString().ShouldBe("Home");
        home.GetProperty("parentId").GetString().ShouldBe(RootPageId);

        var setup = Payload(creates[1]);
        setup.GetProperty("title").GetString().ShouldBe("Setup Guide");
        setup.GetProperty("parentId").GetString().ShouldBe(PageId(outcome, "README.md"));

        // The banner belongs on the published body and not in the hash (§8, rule §9.2).
        var body = home.GetProperty("body").GetProperty("storage").GetProperty("value").GetString();
        body.ShouldNotBeNull();
        body.ShouldContain("<ac:structured-macro ac:name=\"info\">");
    }

    [Fact]
    public async Task Records_the_real_rendered_diagram_hash_rather_than_the_plan_placeholder()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var outcome = await ExecuteAsync(server, new DocumeState(), repoSha: "c0ffee");

        var page = outcome.State.Pages["guides/setup.md"];
        page.Attachments[DiagramAttachment].ShouldBe(ContentHash.OfBytes(Encoding.UTF8.GetBytes(Svg)));
        page.Attachments[LogoAttachment].ShouldBe(ContentHash.OfBytes(LogoBytes));
        page.ParentPageId.ShouldBe(PageId(outcome, "README.md"));
        page.PublishedVersion.ShouldBe(1);
        page.ContentHash.ShouldNotBeNull();

        // §6.2 step 8: the commit the wiki is published at, stamped only on a clean run.
        outcome.State.LastPublishedSha.ShouldBe("c0ffee");
    }

    /// <summary>
    /// The guard reports a refusal instead of throwing, so the write path is the thing that has to
    /// honor it (<see cref="PublishGuard"/>, CLAUDE.md §0.1, rule §1.4).
    /// </summary>
    [Fact]
    public async Task Refuses_to_send_a_single_request_when_the_target_space_is_protected()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var config = Config() with
        {
            Confluence = Config().Confluence with { ProtectedSpaces = [SpaceKey] },
        };

        var outcome = await ExecuteAsync(server, new DocumeState(), config: config);

        outcome.Succeeded.ShouldBeFalse();
        outcome.StoppedBecause.ShouldNotBeNull();
        outcome.StoppedBecause.ShouldContain("protectedSpaces");
        outcome.StateChanged.ShouldBeFalse();
        server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_to_publish_anything_when_one_page_does_not_convert()
    {
        Write("legacy.md", "# Legacy\n\n```plantuml\n@startuml\n@enduml\n```\n");

        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);

        var outcome = await ExecuteAsync(server, new DocumeState());

        outcome.StoppedBecause.ShouldNotBeNull();
        outcome.StoppedBecause.ShouldContain("converter refuses");
        server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sends_nothing_when_a_second_run_finds_nothing_changed()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var first = await ExecuteAsync(server, new DocumeState());
        server.ResetLogEntries();

        var second = await ExecuteAsync(server, first.State);

        second.Succeeded.ShouldBeTrue();
        second.Pages.ShouldBeEmpty();
        second.StateChanged.ShouldBeFalse();
        server.LogEntries.ShouldBeEmpty();
    }

    /// <summary>
    /// The optimistic-lock value comes from Confluence at write time, not from state: a human's browser
    /// edit moves the version on, and republishing over hand edits is the design (rule §9.1) while
    /// publishing at a stale version number is just a 409.
    /// </summary>
    [Fact]
    public async Task Updates_a_changed_page_at_the_version_confluence_holds_now()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var first = await ExecuteAsync(server, new DocumeState());
        var pageId = PageId(first, "README.md");

        Write("README.md", "# Home\n\nRewritten, with no image at all.\n");
        server.ResetLogEntries();
        StubRead(server, version: 7);
        StubUpdate(server);

        var second = await ExecuteAsync(server, first.State);

        second.Succeeded.ShouldBeTrue();
        second.UpdatedCount.ShouldBe(1);
        second.UploadedAttachmentCount.ShouldBe(0);

        var updates = Requests(server, "PUT", $"/wiki/api/v2/pages/{pageId}");
        var payload = Payload(updates.Single());
        payload.GetProperty("version").GetProperty("number").GetInt32().ShouldBe(8);
        payload.GetProperty("id").GetString().ShouldBe(pageId);

        second.State.Pages["README.md"].PublishedVersion.ShouldBe(8);
    }

    [Fact]
    public async Task Removes_the_approved_label_when_a_republish_changes_the_content()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var first = await ExecuteAsync(server, new DocumeState());
        var pageId = PageId(first, "README.md");
        var approved = Approve(first.State, "README.md");

        Write("README.md", "# Home\n\nRewritten after approval.\n");
        server.ResetLogEntries();
        StubRead(server, version: 3);
        StubUpdate(server);
        StubLabelRemoval(server);

        var second = await ExecuteAsync(server, approved);

        second.ApprovalsRevokedCount.ShouldBe(1);

        var deletes = Requests(server, "DELETE", $"/wiki/rest/api/content/{pageId}/label");
        deletes.Single().Query!["name"].Single().ShouldBe("approved");

        var approval = second.State.Pages["README.md"].Approval;
        approval.ShouldNotBeNull();
        approval.Status.ShouldBe(ApprovalStatus.NeedsReview);
        approval.History.Count.ShouldBe(1);
        approval.History[0].By.ShouldBe("mirko");
    }

    /// <summary>
    /// One Node process per distinct diagram source (§6.2 step 3), even though the attachment itself
    /// has to be uploaded to every page that shows it — Confluence attachments are per page.
    /// </summary>
    [Fact]
    public async Task Renders_a_diagram_once_and_uploads_it_to_every_page_that_uses_it()
    {
        Write("guides/deploy.md", SetupPage.Replace("title: Setup Guide", "title: Deploy Guide", StringComparison.Ordinal));

        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var outcome = await ExecuteAsync(server, new DocumeState());

        outcome.Succeeded.ShouldBeTrue();
        _rendered.Count.ShouldBe(1);

        outcome.State.Pages["guides/deploy.md"].Attachments.ShouldContainKey(DiagramAttachment);
        outcome.State.Pages["guides/setup.md"].Attachments.ShouldContainKey(DiagramAttachment);
        outcome.Pages
            .Count(page => page.UploadedAttachments.Contains(DiagramAttachment))
            .ShouldBe(2);
    }

    [Fact]
    public async Task Creates_a_page_again_when_it_has_vanished_from_confluence_and_re_uploads_its_attachments()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var first = await ExecuteAsync(server, new DocumeState());
        var gone = PageId(first, "README.md");

        Write("README.md", "# Home\n\nStill has the ![logo](images/logo.png).\n");
        server.ResetLogEntries();
        StubMissingRead(server);

        var second = await ExecuteAsync(server, first.State);

        second.Succeeded.ShouldBeTrue();
        var result = second.Pages.Single(page => string.Equals(page.Path, "README.md", StringComparison.Ordinal));
        result.Recreated.ShouldBeTrue();
        result.PageId.ShouldNotBe(gone);

        // The new page carries none of the old one's attachments, so the changed subset is not enough.
        result.UploadedAttachments.ShouldBe([LogoAttachment]);
        second.Warnings.ShouldContain(warning => warning.Contains("docume sync", StringComparison.Ordinal));
        second.State.Pages["README.md"].PageId.ShouldBe(result.PageId);
    }

    /// <summary>An expired token is never retried and never worked around (rule §1.2).</summary>
    [Fact]
    public async Task Stops_the_whole_run_when_confluence_rejects_the_credentials()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Unauthorized).WithBody("{}"));

        var outcome = await ExecuteAsync(server, new DocumeState(), repoSha: "c0ffee");

        outcome.Succeeded.ShouldBeFalse();
        outcome.StoppedBecause.ShouldNotBeNull();
        outcome.StoppedBecause.ShouldContain("credentials");
        outcome.Failures.Count.ShouldBe(1);
        outcome.Pages.ShouldBeEmpty();

        // One attempt, not one per page and not one per retry.
        Requests(server, "POST", "/wiki/api/v2/pages").Count.ShouldBe(1);
        outcome.State.LastPublishedSha.ShouldBeNull();
    }

    [Fact]
    public async Task Reports_a_page_whose_diagram_will_not_render_and_publishes_the_rest()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var outcome = await ExecuteAsync(
            server,
            new DocumeState(),
            repoSha: "c0ffee",
            renderer: (_, _) => throw new MermaidRenderException("mermaid said no", MermaidRenderFault.Diagram));

        outcome.Succeeded.ShouldBeFalse();
        outcome.StoppedBecause.ShouldBeNull();
        outcome.Failures.Single().Path.ShouldBe("guides/setup.md");
        outcome.Failures.Single().Message.ShouldContain("mermaid said no");

        // The page that converted is published and recorded; the failed one leaves no state entry.
        outcome.Pages.Single().Path.ShouldBe("README.md");
        outcome.State.Pages.ShouldContainKey("README.md");
        outcome.State.Pages.ShouldNotContainKey("guides/setup.md");

        // A failed page means the wiki is not published at this commit, so the stamp is withheld.
        outcome.State.LastPublishedSha.ShouldBeNull();
    }

    /// <summary>
    /// Adding a directory index reparents its siblings without touching a byte of their markdown, so
    /// they plan as skips and cannot be moved by a run that writes no body for them. Named, not
    /// silently left behind.
    /// </summary>
    [Fact]
    public async Task Warns_about_a_page_the_tree_now_puts_under_a_different_parent()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var first = await ExecuteAsync(server, new DocumeState());

        Write("guides/README.md", "# Guides\n\nIndex page added after the fact.\n");
        server.ResetLogEntries();

        var second = await ExecuteAsync(server, first.State);

        second.Succeeded.ShouldBeTrue();
        second.Warnings.ShouldContain(warning =>
            warning.Contains("guides/setup.md", StringComparison.Ordinal)
            && warning.Contains("--force", StringComparison.Ordinal));
    }

    /// <summary>
    /// A scoped run's one structural trap: the page asked for hangs under a page that has never been
    /// published, and the scope excludes the parent. Filing the child anywhere else would put it where the
    /// tree does not say, so it fails — naming the scope, because blaming the parent for a failure it did
    /// not have would send the reader looking for a bug.
    /// </summary>
    [Fact]
    public async Task Says_the_parent_is_out_of_scope_rather_than_blaming_it()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var outcome = await ExecuteAsync(
            server, new DocumeState(), scope: PublishScope.ForPages(["guides/setup.md"]));

        outcome.Succeeded.ShouldBeFalse();

        var failure = outcome.Failures.Single();
        failure.Path.ShouldBe("guides/setup.md");
        failure.Message.ShouldContain("scope");

        // Nothing was created: not the excluded parent, and not the child that needed it.
        Requests(server, "POST", "/wiki/api/v2/pages").ShouldBeEmpty();
        outcome.StateChanged.ShouldBeFalse();
    }

    /// <summary>
    /// The parent-drift warning tells a reader to re-run with <c>--force</c>. For a page the scope
    /// deliberately held back that advice is wrong — the scope, not the missing body, is why the page
    /// stayed put, and the report already names it as excluded.
    /// </summary>
    [Fact]
    public async Task Does_not_nag_about_a_reparented_page_the_scope_excluded()
    {
        using var server = WireMockServer.Start();
        StubSpace(server);
        StubCreate(server);
        StubAttachmentUpload(server);

        var first = await ExecuteAsync(server, new DocumeState());

        // The new index reparents guides/setup.md, and its body changes too — so a full run would update
        // it and carry the new parent along. This run's scope covers only the index.
        Write("guides/README.md", "# Guides\n\nIndex page added after the fact.\n");
        Write("guides/setup.md", SetupPage + "\n\nOne more line.\n");
        server.ResetLogEntries();

        var second = await ExecuteAsync(
            server, first.State, scope: PublishScope.ForPages(["guides/README.md"]));

        second.Succeeded.ShouldBeTrue();
        second.Pages.Single().Path.ShouldBe("guides/README.md");
        second.Warnings.ShouldNotContain(warning =>
            warning.Contains("guides/setup.md", StringComparison.Ordinal));
    }

    /// <summary>
    /// A wrong base URL or a Confluence outage must read as a sentence, not a stack trace — and must
    /// still return the state accumulated so far. Caught by the first CLI smoke run of the write path.
    /// </summary>
    [Fact]
    public async Task Stops_with_a_message_rather_than_a_stack_trace_when_confluence_cannot_be_reached()
    {
        using var server = WireMockServer.Start();

        // Port 1 answers nothing anywhere, so this is the transport failing, not an endpoint refusing.
        var outcome = await ExecuteAsync(server, new DocumeState(), baseUrl: new Uri("http://127.0.0.1:1/wiki"));

        outcome.Succeeded.ShouldBeFalse();
        outcome.StoppedBecause.ShouldNotBeNull();
        outcome.StoppedBecause.ShouldContain("could not be reached");
        outcome.StateChanged.ShouldBeFalse();
    }

    private static DocumeConfig Config() => new()
    {
        Confluence = new ConfluenceConfig
        {
            BaseUrl = "https://example.atlassian.net/wiki",
            SpaceKey = SpaceKey,
            RootPageId = RootPageId,
        },
    };

    private static DocumeState Approve(DocumeState state, string path)
    {
        var page = state.Pages[path] with
        {
            Approval = new ApprovalState
            {
                Status = ApprovalStatus.Approved,
                ApprovedBy = "mirko",
                ApprovedAt = "2026-07-25T09:00:00Z",
                ApprovedVersion = 1,
            },
        };

        var pages = new Dictionary<string, PageState>(state.Pages, StringComparer.Ordinal) { [path] = page };

        return state with { Pages = pages };
    }

    private static string PageId(PublishOutcome outcome, string path) =>
        outcome.State.Pages[path].PageId!;

    private static List<IRequestMessage> Requests(WireMockServer server, string method, string pathPrefix) =>
        server.LogEntries
            .Select(entry => entry.RequestMessage)
            .Where(request => request is not null
                && string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase)
                && request.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
            .Select(request => request!)
            .ToList();

    private static JsonElement Payload(IRequestMessage request)
    {
        request.Body.ShouldNotBeNull();
        using var document = JsonDocument.Parse(request.Body!);

        return document.RootElement.Clone();
    }

    private static void StubSpace(WireMockServer server) =>
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/spaces").UsingGet())
            .RespondWith(Json($$"""
                {
                  "results": [{ "id": "{{SpaceId}}", "key": "{{SpaceKey}}", "name": "DocuMe Sandbox" }],
                  "_links": {}
                }
                """));

    /// <summary>
    /// Answers a create with a fresh page id, so a child's <c>parentId</c> proves the run threaded the
    /// id its parent's create produced rather than a value it knew in advance.
    /// </summary>
    private static void StubCreate(WireMockServer server)
    {
        var next = 1000;

        server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingPost())
            .RespondWith(Json(request =>
            {
                var id = Interlocked.Increment(ref next).ToString(CultureInfo.InvariantCulture);
                var title = Payload(request).GetProperty("title").GetString();

                return Page(id, title!, version: 1);
            }));
    }

    private static void StubRead(WireMockServer server, int version) =>
        server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingGet())
            .RespondWith(Json(request => Page(LastSegment(request.Path), "whatever Confluence holds", version)));

    private static void StubMissingRead(WireMockServer server) =>
        server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound).WithBody("{}"));

    private static void StubUpdate(WireMockServer server) =>
        server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingPut())
            .RespondWith(Json(request =>
            {
                var payload = Payload(request);

                return Page(
                    payload.GetProperty("id").GetString()!,
                    payload.GetProperty("title").GetString()!,
                    payload.GetProperty("version").GetProperty("number").GetInt32());
            }));

    private static void StubAttachmentUpload(WireMockServer server) =>
        server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/wiki/rest/api/content/*/child/attachment"))
                .UsingPut())
            .RespondWith(Json("""
                {
                  "results": [
                    { "id": "att1", "type": "attachment", "status": "current", "title": "uploaded",
                      "version": { "number": 1 } }
                  ],
                  "_links": {}
                }
                """));

    private static void StubLabelRemoval(WireMockServer server) =>
        server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/rest/api/content/*/label")).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));

    private static string Page(string id, string title, int version) =>
        $$"""
        {
          "id": "{{id}}",
          "status": "current",
          "title": {{JsonSerializer.Serialize(title)}},
          "spaceId": "{{SpaceId}}",
          "version": { "number": {{version.ToString(CultureInfo.InvariantCulture)}} }
        }
        """;

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static IResponseBuilder Json(string body) => Response.Create()
        .WithStatusCode(HttpStatusCode.OK)
        .WithHeader("Content-Type", "application/json")
        .WithBody(body);

    private static IResponseBuilder Json(Func<IRequestMessage, string> body) => Response.Create()
        .WithStatusCode(HttpStatusCode.OK)
        .WithHeader("Content-Type", "application/json")
        .WithBody(body);

    private async Task<PublishOutcome> ExecuteAsync(
        WireMockServer server,
        DocumeState state,
        DocumeConfig? config = null,
        string? repoSha = null,
        DiagramRenderer? renderer = null,
        Uri? baseUrl = null,
        PublishScope? scope = null)
    {
        config ??= Config();

        var report = PublishPipeline.Plan(
            config,
            WikiTree.Load(_dir),
            state,
            new PublishOptions { GeneratedOn = new DateOnly(2026, 7, 25), Scope = scope });

        var options = new ConfluenceClientOptions
        {
            BaseUrl = baseUrl ?? new Uri($"{server.Url}/wiki"),
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.FromMilliseconds(1),
            Timeout = TimeSpan.FromSeconds(30),
        };

        using var client = ConfluenceClient.Create(options, Credentials);
        var executor = new PublishExecutor(client, _dir, renderer ?? Render);

        return await executor.ExecuteAsync(
            config,
            report,
            state,
            new PublishExecutionOptions { RepoSha = repoSha },
            TestContext.Current.CancellationToken);
    }

    private Task<MermaidDiagram> Render(string source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _rendered.Add(source);

        return Task.FromResult(new MermaidDiagram(MermaidAttachmentName.ForSource(source), Svg, "120", "80"));
    }

    private void Write(string relativePath, string content) =>
        File.WriteAllText(Materialize(relativePath), content);

    private string Materialize(string relativePath)
    {
        var full = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        return full;
    }
}
