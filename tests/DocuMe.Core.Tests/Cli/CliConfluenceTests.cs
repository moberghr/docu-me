using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DocuMe.Core.State;
using Shouldly;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Cli;

/// <summary>
/// The three Confluence-facing commands — `publish`, `sync` and `dashboard` — run as a process against
/// a local HTTP server (.claude/rules/testing.md §4.2), which is the only way to reach their command
/// halves: the option binding, the exit code, the state file each one leaves behind and the report it
/// prints. Everything under them is WireMock-tested at the Core level already; none of that covers
/// what happens when the CLI composes it.
/// </summary>
/// <remarks>
/// The repo each test works in is scaffolded by `docume init` pointed at this server's own address,
/// so the base URL travels the real route — command line → docume.json → ConfigLoader → the client —
/// rather than being handed to a constructor. Nothing here can reach Confluence: the address is a
/// loopback port WireMock opened for this test.
/// </remarks>
public sealed class CliConfluenceTests : IDisposable
{
    private const string SpaceKey = "SBX";
    private const string SpaceId = "98304";

    /// <summary>The title `docume init`'s scaffolded README carries, from its H1.</summary>
    private const string HomeTitle = "Documentation";

    /// <summary>The dashboard title in the scaffolded docume.json (§6.5).</summary>
    private const string DashboardTitle = "Documentation Status";

    /// <summary>The one page `docume init` scaffolds, as the state file keys it.</summary>
    private const string HomePath = "README.md";

    private readonly WireMockServer _server = WireMockServer.Start();

    private readonly string _root = Directory.CreateTempSubdirectory("docume-cli-confluence").FullName;

    /// <summary>Every page id the fake Confluence invented, in the order it handed them out.</summary>
    private readonly List<string> _created = [];

    private int _nextPageId = 700000;

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Publish_writes_the_page_id_confluence_handed_back_into_the_state_file()
    {
        var work = Scaffolded(nameof(Publish_writes_the_page_id_confluence_handed_back_into_the_state_file));

        StubSpace();
        StubCreate();

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);
        run.Flowed.ShouldContain("PUBLISHED", customMessage: run.Diagnostics);

        _created.Count.ShouldBe(1, $"The run did not create exactly one page.{Environment.NewLine}{run.Diagnostics}");

        var page = State(work).Pages[HomePath];

        // Against the id the server invented, not a literal: this is the whole point of driving the
        // command as a process. A pageId that does not come back out of the response is a page the next
        // run creates a second time, and Confluence rejects the duplicate title (§6.2 step 8).
        var because = "state.json does not hold the id Confluence answered the create with."
            + Environment.NewLine + run.Diagnostics;

        page.PageId.ShouldBe(_created[0], because);

        page.Title.ShouldBe(HomeTitle, run.Diagnostics);
        page.PublishedVersion.ShouldBe(1, run.Diagnostics);
    }

    /// <summary>
    /// <c>--dry-run</c> is what every scaffolded workflow and every reviewer leans on before a real
    /// publish, and the promise is absolute: nothing is written and nothing is asked. Asserted as zero
    /// requests rather than zero writes, because a read is a promise broken too — it needs the token the
    /// dry run is supposed to make unnecessary.
    /// </summary>
    [Fact]
    public void A_dry_run_asks_confluence_for_nothing()
    {
        var work = Scaffolded(nameof(A_dry_run_asks_confluence_for_nothing));

        // Deliberately nothing stubbed: any request at all answers 404 here, so a regression that
        // reached out would fail the run as well as show up in the log below.
        var before = File.ReadAllBytes(StatePath(work));
        var run = Invoke(work, "publish", "--dry-run", "--tree");

        run.Code.ShouldBe(0, run.Diagnostics);
        run.Flowed.ShouldContain("Nothing was written", customMessage: run.Diagnostics);

        var asked = Seen()
            .Select(request => $"{request.Method} {request.Path}")
            .ToList();

        var because = $"`publish --dry-run` sent Confluence {asked.Count} request(s): "
            + $"[{string.Join(", ", asked)}].{Environment.NewLine}{run.Diagnostics}";

        asked.ShouldBeEmpty(because);

        File.ReadAllBytes(StatePath(work)).ShouldBe(before, "`publish --dry-run` rewrote the state file.");
    }

    /// <summary>
    /// A publish Confluence refuses has to exit non-zero. The scaffolded <c>docs-publish.yml</c> reads
    /// nothing but this code, so a refused write that exited 0 is a docs job that goes green having
    /// published nothing.
    /// </summary>
    [Fact]
    public void Publish_exits_nonzero_when_confluence_refuses_the_create()
    {
        var work = Scaffolded(nameof(Publish_exits_nonzero_when_confluence_refuses_the_create));

        StubSpace();

        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "errors": [{ "title": "A page with this title already exists" }] }"""));

        var run = Invoke(work, "publish");

        run.Code.ShouldNotBe(0, run.Diagnostics);

        // The refused page has to be counted as failed rather than dropped out of both columns, and the
        // verdict has to admit nothing was written: "PARTIALLY PUBLISHED — 0 page(s) published, 1 failed".
        run.Flowed.ShouldContain("0 page(s) published", customMessage: run.Diagnostics);
        run.Flowed.ShouldContain("1 failed", customMessage: run.Diagnostics);

        // Named, not counted: a report that says "1 failed" without saying which page is a report
        // nobody can act on.
        run.Flowed.ShouldContain(HomePath, customMessage: run.Diagnostics);

        // The page never got an id, so it must not be recorded as published — the next run has to plan
        // the create again.
        var because = $"A page Confluence refused was written into state.{Environment.NewLine}{run.Diagnostics}";

        State(work).Pages.ContainsKey(HomePath).ShouldBeFalse(because);
    }

    /// <summary>
    /// Rule §1.2 / PLAN.md §6: 401 is a hard stop, never a retry. Asserted at the process level because
    /// the retry pipeline is assembled in <c>ConfluenceHttp</c> and only the CLI wires it to a real
    /// socket — a bad token retried across a bulk publish is how an account gets locked out.
    /// </summary>
    [Fact]
    public void An_expired_token_stops_the_publish_after_a_single_request()
    {
        var work = Scaffolded(nameof(An_expired_token_stops_the_publish_after_a_single_request));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/*")).UsingAnyMethod())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.Unauthorized)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "message": "Unauthorized" }"""));

        var run = Invoke(work, "publish");

        run.Code.ShouldNotBe(0, run.Diagnostics);

        var attempts = Seen().Count;
        var because = $"A 401 was answered with {attempts} request(s), not 1. Rule §1.2: auth failures "
            + $"stop the run instead of retrying.{Environment.NewLine}{run.Diagnostics}";

        attempts.ShouldBe(1, because);

        // The message has to name the variable to fix, because the person reading it is at a terminal
        // with two environment variables and no idea which one Confluence objected to.
        run.Flowed.ShouldContain("DOCUME_CONFLUENCE_TOKEN", customMessage: run.Diagnostics);
    }

    /// <summary>
    /// A page Confluence refuses must not cost the pages that published: the id a create earned is the
    /// only record that the page exists, and losing it makes the next run create a duplicate title
    /// Confluence then rejects (§6.2 step 8). Only a process can check this — it is the state file on
    /// disk after a run that also failed.
    /// </summary>
    [Fact]
    public void A_refused_page_does_not_cost_the_page_that_published()
    {
        var work = Scaffolded(nameof(A_refused_page_does_not_cost_the_page_that_published));

        const string guide = "guides/setup.md";
        Write(work, guide, "---\ntitle: Setup Guide\n---\n\n# Setup\n\nHow to set the thing up.\n");

        StubSpace();
        StubCreateExcept("Setup Guide");

        var run = Invoke(work, "publish");

        run.Code.ShouldNotBe(0, run.Diagnostics);

        var pages = State(work).Pages;
        var because = $"The page that published was not recorded, so the next run creates it again and "
            + $"Confluence rejects the duplicate title.{Environment.NewLine}{run.Diagnostics}";

        pages.ShouldContainKey(HomePath, because);
        pages[HomePath].PageId.ShouldBe(_created.ShouldHaveSingleItem(), because);

        // And the refused one is still absent, so the next run plans it as a create rather than an update
        // against an id it never received.
        pages.ShouldNotContainKey(guide, run.Diagnostics);
    }

    /// <summary>
    /// Rule §0.3 / §1.1: the token never reaches a log. Checked across the three shapes a run takes —
    /// one that succeeds, one Confluence refuses, and one where the token itself is the problem — because
    /// the last is where an error message is most tempted to quote it back.
    /// </summary>
    [Fact]
    public void The_api_token_never_reaches_either_stream()
    {
        var work = Scaffolded(nameof(The_api_token_never_reaches_either_stream));

        StubSpace();
        StubCreate();
        StubLabelSearch(approved: [], stale: []);

        var runs = new List<CliRun>
        {
            Invoke(work, "publish"),
            Invoke(work, "sync", "--labels"),
            Invoke(work, "status"),
        };

        // Same server, re-stubbed to reject everything: the auth failure path prints the longest
        // credential-shaped prose in the tool.
        _server.Reset();
        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/*")).UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Unauthorized).WithBody("{}"));

        runs.Add(Invoke(work, "publish", "--force"));

        // The header form as well as the raw one: ConfluenceCredentials exposes no token property, so a
        // careless log statement reaches for BasicAuthParameter, and base64 is not redaction.
        var basicAuth = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{DocumeCli.Email}:{DocumeCli.ApiToken}"));

        foreach (var run in runs)
        {
            var streams = $"{run.Output}{run.Error}";
            var because = $"`docume {run.Arguments}` printed the token — rule §1.1: it never goes to a "
                + $"log.{Environment.NewLine}{run.Diagnostics}";

            streams.ShouldNotContain(DocumeCli.ApiToken, customMessage: because);
            streams.ShouldNotContain(basicAuth, customMessage: because);
        }
    }

    /// <summary>
    /// Rule §9.6 / §6.2: orphan deletion asks first and never runs in CI. The precondition is a terminal
    /// that can prompt, and whether one exists is a property of the process — in-process this check reads
    /// whatever the test host's console happens to be.
    /// </summary>
    [Fact]
    public void Prune_refuses_without_a_terminal_and_publishes_nothing()
    {
        var work = Scaffolded(nameof(Prune_refuses_without_a_terminal_and_publishes_nothing));

        StubSpace();
        StubCreate();

        // stdout and stderr are redirected by the harness, which is exactly the shape of a CI runner.
        var run = Invoke(work, "publish", "--prune");

        run.Code.ShouldNotBe(0, run.Diagnostics);
        run.Flowed.ShouldContain("--prune", customMessage: run.Diagnostics);

        // Refused before the write, not after it: a run that published and then discovered it could not
        // prompt would leave the operator re-reading a report to work out what did happen.
        var asked = Seen().Select(request => $"{request.Method} {request.Path}").ToList();
        var because = $"`publish --prune` reached Confluence before refusing: "
            + $"[{string.Join(", ", asked)}].{Environment.NewLine}{run.Diagnostics}";

        asked.ShouldBeEmpty(because);
    }

    /// <summary>
    /// The M3 acceptance path with the human's hands replaced by a stub: a page carrying the `approved`
    /// label in Confluence becomes an approval in <c>state.json</c> (§8, §6.3).
    /// </summary>
    [Fact]
    public void Sync_labels_records_the_approval_the_space_carries()
    {
        var work = Scaffolded(nameof(Sync_labels_records_the_approval_the_space_carries));

        StubSpace();
        StubCreate();

        Invoke(work, "publish").Code.ShouldBe(0, "The fixture's own publish failed.");

        var pageId = _created.ShouldHaveSingleItem();

        StubLabelSearch(approved: [pageId], stale: []);

        var run = Invoke(work, "sync", "--labels");

        run.Code.ShouldBe(0, run.Diagnostics);

        var approval = State(work).Pages[HomePath].Approval;
        var because = "`sync --labels` did not record the approved label." + Environment.NewLine
            + run.Diagnostics;

        approval.ShouldNotBeNull(because);
        approval.Status.ShouldBe("approved", run.Diagnostics);

        // Confluence Cloud exposes no label author on any endpoint the tool can reach (spike S3), so the
        // audit trail says so rather than naming the account DocuMe authenticates as.
        approval.ApprovedBy.ShouldBe("unknown", run.Diagnostics);
    }

    /// <summary>
    /// <c>docume dashboard</c> (§6.5) end to end through the command: the title comes out of
    /// <c>docume.json</c>, the page is looked up before it is created, and the report says which.
    /// </summary>
    [Fact]
    public void Dashboard_creates_the_status_page_under_the_configured_title()
    {
        var work = Scaffolded(nameof(Dashboard_creates_the_status_page_under_the_configured_title));

        StubSpace();
        StubLabelSearch(approved: [], stale: []);
        StubNoPageWithTitle();
        StubCreate();

        var run = Invoke(work, "dashboard");

        run.Code.ShouldBe(0, run.Diagnostics);
        run.Flowed.ShouldContain(DashboardTitle, customMessage: run.Diagnostics);

        var titles = Requests("POST", "/wiki/api/v2/pages")
            .Select(request => Payload(request).GetProperty("title").GetString())
            .ToList();

        var because = "The dashboard page was not created under the title docume.json configures."
            + Environment.NewLine + run.Diagnostics;

        titles.ShouldBe([DashboardTitle], because);
    }

    /// <summary>
    /// The dry run of the command that writes a page to Confluence and nothing to the repo: if it wrote
    /// anyway, there would be no state file left behind to notice it by.
    /// </summary>
    [Fact]
    public void A_dashboard_dry_run_reads_the_space_but_writes_no_page()
    {
        var work = Scaffolded(nameof(A_dashboard_dry_run_reads_the_space_but_writes_no_page));

        StubSpace();
        StubLabelSearch(approved: [], stale: []);
        StubNoPageWithTitle();
        StubCreate();

        var run = Invoke(work, "dashboard", "--dry-run");

        run.Code.ShouldBe(0, run.Diagnostics);

        var writes = Seen()
            .Where(request => !string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            .Select(request => $"{request.Method} {request.Path}")
            .ToList();

        var because = $"`dashboard --dry-run` wrote to Confluence: [{string.Join(", ", writes)}]."
            + Environment.NewLine + run.Diagnostics;

        writes.ShouldBeEmpty(because);
    }

    private static DocumeState State(string work) => StateStore.Load(StatePath(work));

    /// <summary>Adds a page to the scaffolded wiki, wiki-root-relative.</summary>
    private static void Write(string work, string path, string markdown)
    {
        var full = Path.Combine(work, "docs", "wiki", path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, markdown);
    }

    private static string StatePath(string work) =>
        Path.Combine(work, "docs", "wiki", "_meta", "state.json");

    private static JsonElement Payload(IRequestMessage request)
    {
        request.Body.ShouldNotBeNull();
        using var document = JsonDocument.Parse(request.Body!);

        return document.RootElement.Clone();
    }

    private static CliRun Invoke(string workingDirectory, params string[] args) =>
        DocumeCli.Invoke(workingDirectory, args);

    /// <summary>Every request the fake Confluence was sent, in order.</summary>
    private List<IRequestMessage> Seen() =>
        _server.LogEntries
            .Select(entry => entry.RequestMessage)
            .OfType<IRequestMessage>()
            .ToList();

    private List<IRequestMessage> Requests(string method, string path) =>
        Seen()
            .Where(request => string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Path, path, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// A consumer repo as `docume init` leaves it, pointed at this test's own server. Scaffolded by the
    /// CLI rather than written here so the fixture cannot drift from what a real consumer gets.
    /// </summary>
    private string Scaffolded(string name)
    {
        var work = Path.Combine(_root, name);
        Directory.CreateDirectory(work);

        var run = Invoke(work, "init", "--space", SpaceKey, "--base-url", $"{_server.Url}/wiki");

        run.Code.ShouldBe(0, $"The fixture's own `docume init` failed.{Environment.NewLine}{run.Diagnostics}");

        return work;
    }

    private void StubSpace() =>
        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/spaces").UsingGet())
            .RespondWith(Json($$"""
                {
                  "results": [{ "id": "{{SpaceId}}", "key": "{{SpaceKey}}", "name": "DocuMe Sandbox" }],
                  "_links": {}
                }
                """));

    /// <summary>
    /// Answers a create with an id this side invents at request time and remembers, so a test can assert
    /// the CLI persisted what the server said rather than a value the test also knew.
    /// </summary>
    private void StubCreate() =>
        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingPost())
            .RespondWith(Json(request =>
            {
                var id = Interlocked.Increment(ref _nextPageId).ToString(CultureInfo.InvariantCulture);
                var title = Payload(request).GetProperty("title").GetString();

                lock (_created)
                {
                    _created.Add(id);
                }

                return $$"""
                    {
                      "id": "{{id}}",
                      "status": "current",
                      "title": {{JsonSerializer.Serialize(title)}},
                      "spaceId": "{{SpaceId}}",
                      "version": { "number": 1 }
                    }
                    """;
            }));

    /// <summary>
    /// Creates every page but one, which Confluence refuses by title. The refusal is a 400 because that
    /// is what a duplicate title arrives as, and because it is not retryable — a 5xx would spend the
    /// retry pipeline's backoff before failing.
    /// </summary>
    private void StubCreateExcept(string refusedTitle)
    {
        StubCreate();

        _server
            .Given(Request.Create()
                .WithPath("/wiki/api/v2/pages")
                .WithBody(new JsonPartialMatcher($$"""{ "title": {{JsonSerializer.Serialize(refusedTitle)}} }"""))
                .UsingPost())
            .AtPriority(-1)
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "errors": [{ "title": "A page with this title already exists" }] }"""));
    }

    /// <summary>
    /// The v1 CQL search both `sync --labels` and `dashboard` read the label state through, answered per
    /// label out of the <c>cql</c> the caller sent.
    /// </summary>
    private void StubLabelSearch(IReadOnlyList<string> approved, IReadOnlyList<string> stale) =>
        _server
            .Given(Request.Create().WithPath("/wiki/rest/api/content/search").UsingGet())
            .RespondWith(Json(request =>
            {
                var cql = request.Query?["cql"].ToString() ?? string.Empty;
                var hits = cql.Contains("\"approved\"", StringComparison.Ordinal) ? approved : stale;

                var results = hits.Select(id => $$"""
                    {
                      "id": "{{id}}", "type": "page", "status": "current",
                      "title": {{JsonSerializer.Serialize(HomeTitle)}},
                      "version": { "number": 1 }
                    }
                    """);

                var size = hits.Count.ToString(CultureInfo.InvariantCulture);

                return $$"""
                    {
                      "results": [{{string.Join(",", results)}}],
                      "start": 0, "limit": 50, "size": {{size}},
                      "_links": {}
                    }
                    """;
            }));

    /// <summary>The title lookup answering "no such page", which is what makes the dashboard a create.</summary>
    private void StubNoPageWithTitle() =>
        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingGet())
            .RespondWith(Json("""{ "results": [], "_links": {} }"""));

    private static IResponseBuilder Json(string body) => Response.Create()
        .WithStatusCode(HttpStatusCode.OK)
        .WithHeader("Content-Type", "application/json")
        .WithBody(body);

    private static IResponseBuilder Json(Func<IRequestMessage, string> body) => Response.Create()
        .WithStatusCode(HttpStatusCode.OK)
        .WithHeader("Content-Type", "application/json")
        .WithBody(body);
}
