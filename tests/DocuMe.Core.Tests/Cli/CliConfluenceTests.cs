using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// What the dashboard is renamed to mid-test. No markdown in the scaffolded wiki contains it, so
    /// finding it in a page body can only mean the banner put it there.
    /// </summary>
    private const string RenamedDashboard = "Renamed Status Page";

    /// <summary>The one page `docume init` scaffolds, as the state file keys it.</summary>
    private const string HomePath = "README.md";

    /// <summary>
    /// The version the fake Confluence holds when a republish reads the page. Deliberately not 1: the
    /// version the first publish left in state.json is 1, so a run that sent that one instead of the one
    /// it just read would still look right against a remote sitting at 1.
    /// </summary>
    private const int RemoteVersion = 9;

    /// <summary>
    /// A sentence no converter can produce from the repo's markdown, so finding it downstream can only
    /// mean it came back out of a Confluence response.
    /// </summary>
    private const string Sentinel = "A HUMAN TYPED THIS STRAIGHT INTO CONFLUENCE";

    /// <summary>The stored body a republish reads back, as a hand-edited page would answer.</summary>
    private const string HandEdit = $"<p>{Sentinel} and expected it to survive.</p>";

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
    /// What a label sync costs, at N&gt;1: two CQL searches for the whole run — one per label — and not
    /// one request more, whatever the size of the wiki.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Sync_labels_records_the_approval_the_space_carries"/> proves the outcome but structurally
    /// cannot prove the cost. It runs at one approved page and zero stale, where "twice for the run" and
    /// "twice per page" are the same two requests, and it counts no requests at all. So a sync that
    /// dropped <c>expand=version</c> and read each approved page by id instead would pass it untouched
    /// while costing an 80-page wiki 80 extra round trips — on the one command a cron job runs hourly
    /// (§6.3). <c>LabelReader.VersionsAsync</c> states the property in prose ("one request per label
    /// rather than one per page"); nothing executable held it.
    /// </para>
    /// <para>
    /// The fourth hit is the other half of that same paragraph. A labelled page state does not manage is
    /// reported and skipped, so its missing version must not be paid for either — and it is the one hit
    /// here answering no version, which is precisely the condition that sends a <em>managed</em> page to
    /// <c>FindPageByIdAsync</c>. If the unmanaged guard went, this is the request that would appear.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_three_page_label_sync_costs_two_searches_for_the_run_and_reads_no_page()
    {
        var work = Scaffolded(nameof(A_three_page_label_sync_costs_two_searches_for_the_run_and_reads_no_page));

        const string setup = "guides/setup.md";
        const string deploy = "guides/deploy.md";

        Write(work, setup, "---\ntitle: Setup Guide\n---\n\n# Setup\n\nHow to set the thing up.\n");
        Write(work, deploy, "---\ntitle: Deploy Guide\n---\n\n# Deploy\n\nHow to ship it.\n");

        StubSpace();
        StubCreate();
        StubChildren();

        Invoke(work, "publish").Code.ShouldBe(0, "The fixture's own publish failed.");

        _created.Count.ShouldBe(3, "The fixture did not publish a three-page wiki.");

        // An id the space says is labelled and state has never heard of, kept clear of the range
        // StubCreate invents from.
        const string unmanaged = "899999";

        // Every published page approved, and the last of them stale as well: the two labels are
        // independent lists, so a page carrying both is the ordinary shape of one that went stale after
        // it was approved.
        var stalePageId = _created[^1];

        _server.ResetLogEntries();
        StubLabelSearch(
            approved: [.. _created, unmanaged],
            stale: [stalePageId],
            withoutVersion: [unmanaged]);

        var run = Invoke(work, "sync", "--labels");

        run.Code.ShouldBe(0, run.Diagnostics);

        var footprint = Seen().Select(request => $"{request.Method} {request.Path}").ToList();

        var because = "A four-hit label sync over a three-page wiki sent Confluence "
            + $"[{string.Join(", ", footprint)}].{Environment.NewLine}{run.Diagnostics}";

        // Named first and on its own, because this is the number the N=1 test cannot see and the exact
        // list below would report as a diff rather than as a sentence.
        var searches = footprint.Count(entry =>
            string.Equals(entry, "GET /wiki/rest/api/content/search", StringComparison.Ordinal));

        const string perRun = "The label state was searched {0} time(s) over a three-page wiki. It is two "
            + "searches for the whole run — one per label — not one per page: CQL returns every page "
            + "carrying a label in bulk, and per-page is 80 extra round trips on an 80-page wiki every "
            + "cron run.";

        var counted = string.Format(CultureInfo.InvariantCulture, perRun, searches)
            + Environment.NewLine + because;

        searches.ShouldBe(2, counted);

        // And nothing else at all — no page read in particular. The search is asked for expand=version,
        // so a managed hit's version is already in hand, and the one hit that answered no version is
        // unmanaged: reported and skipped, never paid for.
        var expected = new List<string>
        {
            "GET /wiki/rest/api/content/search",
            "GET /wiki/rest/api/content/search",
        };

        footprint.ShouldBe(expected, because);

        // The footprint only means something if the run did the work. A sync that read the two searches
        // and then reconciled nothing would send these same two requests.
        var pages = State(work).Pages;

        pages.Count.ShouldBe(3, because);

        foreach (var (path, page) in pages)
        {
            var missed = $"{path} carried the approved label but state records no approval. {because}";

            page.Approval.ShouldNotBeNull(missed);
            page.Approval.Status.ShouldBe("approved", missed);
        }

        // Exactly the one page the stale search answered with, so the second search's result is not being
        // smeared across every page the first one returned.
        // Every page here is published, so a null id would itself be the failure rather than something to
        // skip over.
        var staleNow = pages
            .Where(entry => entry.Value.Stale)
            .Select(entry => entry.Value.PageId ?? string.Empty)
            .ToList();

        var onlyStale = new List<string> { stalePageId };

        staleNow.ShouldBe(onlyStale, because);

        // The unmanaged id is reported rather than guessed at (§6.3): a sync that silently matched it to
        // a page by title would be inventing an approval nobody granted.
        run.Flowed.ShouldContain(unmanaged, customMessage: run.Diagnostics);
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

    /// <summary>
    /// Rule §1.4 / §0.1: the dashboard is a page, so publishing it into a protected space is a write the
    /// lock has to stop, and stop before any request — the refusal is resolved from the config alone.
    /// </summary>
    /// <remarks>
    /// The fourth of the four write paths <c>docs/wiki/20-reference/cli.md</c> names ("`publish`,
    /// `dashboard`, `drift --mark` and `sync --reply` all refuse it"), and the only one nothing asserted:
    /// the other three are pinned by <c>PublishExecutorTests</c>, <c>CliDriftTests</c> and
    /// <c>CliFeedbackTests</c> respectively. The guard is a placement, not a computation — it holds only
    /// while it sits ahead of the client — so a test that never runs the command cannot see it move.
    /// </remarks>
    [Fact]
    public void Dashboard_is_refused_when_the_space_is_protected()
    {
        var work = Scaffolded(nameof(Dashboard_is_refused_when_the_space_is_protected));

        Protect(work);
        StubSpace();
        StubLabelSearch(approved: [], stale: []);
        StubNoPageWithTitle();
        StubCreate();

        var run = Invoke(work, "dashboard");

        run.Code.ShouldNotBe(0, run.Diagnostics);
        run.FlowedAll.ShouldContain("protectedSpaces", customMessage: run.Diagnostics);

        var asked = Seen()
            .Select(request => $"{request.Method} {request.Path}")
            .ToList();

        var because = $"`dashboard` reached Confluence before refusing: [{string.Join(", ", asked)}]."
            + Environment.NewLine + run.Diagnostics;

        asked.ShouldBeEmpty(because);
    }

    /// <summary>
    /// The second publish of a page, which is every publish after the first — and the branch this class
    /// could not reach until now: every other test here stubs a create, so the whole CLI-level suite was
    /// a suite of first runs. A create happens once per page ever; an update happens on every scheduled
    /// docs job for the rest of the wiki's life.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted as the run's <em>whole</em> request list rather than "a PUT was sent", because what is
    /// being checked is what the command did <em>around</em> the page write — the part
    /// <see cref="Publishing.PublishExecutorTests.Updates_a_changed_page_at_the_version_confluence_holds_now"/>
    /// cannot see from one layer down. An extra create, a re-read of the space per page, a label write
    /// nobody asked for: each is invisible to an assertion that only looks for the request it expects.
    /// </para>
    /// <para>
    /// The space is read once and the page is read before it is written (§6.2): the update carries the
    /// version Confluence holds now, not the one state.json remembers, or two runs racing produce a
    /// version conflict instead of a second revision.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_republish_updates_the_page_it_already_published_and_writes_nothing_else()
    {
        var work = Scaffolded(nameof(A_republish_updates_the_page_it_already_published_and_writes_nothing_else));

        StubSpace();
        StubCreate();

        Invoke(work, "publish").Code.ShouldBe(0, "The fixture's own first publish failed.");

        var pageId = _created.ShouldHaveSingleItem();

        Write(work, HomePath, $"# {HomeTitle}\n\nRewritten in the repo after the first publish.\n");

        // The create stub is deliberately left standing: a regression that creates instead of updating
        // then succeeds against the server and is caught by the footprint below, which names the request
        // it did not expect — rather than by a 404 that only says the run failed.
        _server.ResetLogEntries();
        StubRepublish(RemoteVersion);

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);

        var footprint = Seen()
            .Select(request => $"{request.Method} {request.Path}")
            .ToList();

        var because = $"The second publish of a page sent Confluence "
            + $"[{string.Join(", ", footprint)}].{Environment.NewLine}{run.Diagnostics}";

        footprint.ShouldBe(
            [
                $"GET /wiki/api/v2/spaces",
                $"GET /wiki/api/v2/pages/{pageId}",
                $"GET /wiki/api/v2/pages/{pageId}/inline-comments",
                $"PUT /wiki/api/v2/pages/{pageId}",
            ],
            because);

        // The version travels from the read into the write, and out of the write's response into state:
        // an update sent at the version state.json remembered would 409 the moment anything else touched
        // the page.
        var sent = Payload(Requests("PUT", $"/wiki/api/v2/pages/{pageId}").ShouldHaveSingleItem());

        sent.GetProperty("version").GetProperty("number").GetInt32()
            .ShouldBe(RemoteVersion + 1, because);

        State(work).Pages[HomePath].PublishedVersion.ShouldBe(RemoteVersion + 1, because);
    }

    /// <summary>
    /// The same steady-state run at more than one page, which is the only place its per-page costs can be
    /// told apart from its per-run ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="A_republish_updates_the_page_it_already_published_and_writes_nothing_else"/> pins the
    /// footprint exactly, but at N=1 — where "once for the run", "once per parent" and "once per page" are
    /// all the number 1 and no assertion can distinguish them. The distinction is the whole cost model of a
    /// real wiki: a space lookup that moved inside the page loop would be 80 extra round trips on an
    /// 80-page publish, and nothing here or one layer down would notice.
    /// </para>
    /// <para>
    /// So this asserts the shape at N=3: the space resolved once before the first write, the child order
    /// read once for the one parent that has siblings under it, and only the page read, the comment check
    /// and the write itself repeating per page.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_three_page_republish_reads_the_space_once_for_the_run_and_costs_three_requests_per_page()
    {
        var work = Scaffolded(
            nameof(A_three_page_republish_reads_the_space_once_for_the_run_and_costs_three_requests_per_page));

        const string setup = "guides/setup.md";
        const string deploy = "guides/deploy.md";

        Write(work, setup, "---\ntitle: Setup Guide\n---\n\n# Setup\n\nHow to set the thing up.\n");
        Write(work, deploy, "---\ntitle: Deploy Guide\n---\n\n# Deploy\n\nHow to ship it.\n");

        StubSpace();
        StubCreate();
        StubChildren();

        Invoke(work, "publish").Code.ShouldBe(0, "The fixture's own first publish failed.");

        _created.Count.ShouldBe(3, "The fixture did not publish a three-page wiki.");

        // Every page changed, so the second run has a body to write for all three: a page the planner
        // skipped would cost nothing and hide whatever the ones that ran did cost.
        Write(work, HomePath, $"# {HomeTitle}\n\nRewritten in the repo after the first publish.\n");
        Write(work, setup, "---\ntitle: Setup Guide\n---\n\n# Setup\n\nRewritten too.\n");
        Write(work, deploy, "---\ntitle: Deploy Guide\n---\n\n# Deploy\n\nAnd this one.\n");

        _server.ResetLogEntries();
        StubRepublish(RemoteVersion);

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);

        var footprint = Seen()
            .Select(request => $"{request.Method} {request.Path}")
            .ToList();

        var because = $"A three-page republish sent Confluence "
            + $"[{string.Join(", ", footprint)}].{Environment.NewLine}{run.Diagnostics}";

        // Named first and on its own, because this is the regression the N=1 test cannot see and the
        // exact list below would report as a diff rather than as a sentence.
        var spaceReads = footprint.Count(entry =>
            string.Equals(entry, "GET /wiki/api/v2/spaces", StringComparison.Ordinal));

        var perRun = $"The space was resolved {spaceReads} time(s) for a 3-page run. It is a whole-run "
            + "lookup, not a per-page one — one per page is 80 wasted round trips on an 80-page wiki, and "
            + $"a token that cannot see the space is a run-level failure either way.{Environment.NewLine}"
            + because;

        spaceReads.ShouldBe(1, perRun);

        // The pages in the order the first run created them, which is the tree order the second run walks
        // in as well: home first, then the two under it.
        var expected = new List<string> { "GET /wiki/api/v2/spaces" };

        foreach (var pageId in _created)
        {
            expected.Add($"GET /wiki/api/v2/pages/{pageId}");
            expected.Add($"GET /wiki/api/v2/pages/{pageId}/inline-comments");
            expected.Add($"PUT /wiki/api/v2/pages/{pageId}");
        }

        // One read for the one parent with more than one child under it (§6.2's post-pass), after the
        // writes rather than per page: the order it reconciles is the order the whole run left behind.
        expected.Add($"GET /wiki/api/v2/pages/{_created[0]}/children");

        footprint.ShouldBe(expected, because);

        // And all three were recorded, not just the one the run happened to finish on: a page written but
        // not recorded is a page the next run creates a second time (§6.2 step 8).
        var pages = State(work).Pages;

        var recorded = pages.Keys.Order(StringComparer.Ordinal).ToList();

        recorded.ShouldBe([HomePath, deploy, setup], because);

        foreach (var path in pages.Keys)
        {
            pages[path].PublishedVersion.ShouldBe(RemoteVersion + 1, $"{path}: {because}");
        }
    }

    /// <summary>
    /// Rule §9.1, on the write path this time: the repo is the source of truth, and a hand edit made in
    /// Confluence is lost on republish by design. The executable half of the rule was pinned for the
    /// dashboard only
    /// (<see cref="Dashboard.DashboardPublisherTests.The_write_carries_the_render_it_was_given_not_the_body_it_read"/>);
    /// this is the same proof on the page path, which is the one that carries the wiki.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Confluence.RemoteBodyReadTests"/> proves no publish call site <em>asks</em> for a body.
    /// That is a different property from this one, and it is not enough on its own: a server may answer
    /// with more than it was asked for, and <c>MapPage</c> maps <c>body.storage.value</c> whenever it is
    /// present, so the remote text really does reach the publish path in memory. What must never happen
    /// is that any of it reaches the write or the state file.
    /// </para>
    /// <para>
    /// The sentinel is prose no converter can emit from the repo's markdown, so a single character of it
    /// downstream can only have come from the response.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_hand_edit_made_in_confluence_reaches_neither_the_republished_body_nor_the_state_file()
    {
        var work = Scaffolded(
            nameof(A_hand_edit_made_in_confluence_reaches_neither_the_republished_body_nor_the_state_file));

        StubSpace();
        StubCreate();

        Invoke(work, "publish").Code.ShouldBe(0, "The fixture's own first publish failed.");

        var pageId = _created.ShouldHaveSingleItem();

        Write(work, HomePath, $"# {HomeTitle}\n\nThe repo's own sentence, and the only one allowed out.\n");

        _server.ResetLogEntries();
        StubRepublish(RemoteVersion, storedBody: HandEdit);

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);

        // The poison has to have been served, or the rest of this test proves nothing: a stub set that
        // quietly stopped answering with a body would leave every assertion below passing vacuously.
        const string unserved = "The fake Confluence never answered the page read with the hand edit, so "
            + "this test is not exercising anything. Fix the stub before trusting it.";

        Served($"/wiki/api/v2/pages/{pageId}").ShouldContain(Sentinel, customMessage: unserved);

        var body = Payload(Requests("PUT", $"/wiki/api/v2/pages/{pageId}").ShouldHaveSingleItem())
            .GetProperty("body")
            .GetProperty("storage")
            .GetProperty("value")
            .GetString();

        const string carried = "The republished body carries text that came back from Confluence, so a "
            + "hand edit in the space survived a republish. Rule §9.1: the repo is the source of truth "
            + "and Confluence page bodies are never a content source — a body that round-trips makes the "
            + "space authoritative for anything nobody happened to overwrite.";

        body.ShouldNotBeNull(run.Diagnostics);
        body.ShouldNotContain(Sentinel, customMessage: carried);

        // And it must not have been recorded either: a contentHash taken over what Confluence answered
        // rather than what the repo rendered would make an approval turn on the space's text (§9.2).
        const string recorded = "The state file holds text that came back from Confluence. Rule §9.1/§9.2: "
            + "nothing the space says is a content source, contentHash included.";

        File.ReadAllText(StatePath(work)).ShouldNotContain(Sentinel, customMessage: recorded);
    }

    /// <summary>
    /// Rule §9.2 end to end and on the wire: a change to what the banner <em>says</em> is not a change to
    /// the page, so it rewrites nothing — and when <c>--force</c> rewrites the page anyway, both the
    /// recorded hash and the reviewer's approval survive it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A chain test, not a gap-closer — written down so nobody re-derives it.</strong> Four
    /// mutations were run against the whole suite while this was being written: hashing the
    /// banner-injected body, recording the uploaded body's hash instead of the plan's, letting
    /// <c>force</c> count as a content change, and dropping the lazy space probe. Every one was already
    /// caught by three to eighteen existing tests, so every link here is defended on its own. What no
    /// other test states is the whole chain — a <c>docume.json</c> edit travelling config, CLI, wire and
    /// back into state.json to come out the far side as nothing — which is the shape a wiring regression
    /// takes rather than a logic one.
    /// </para>
    /// <para>
    /// The three nearest pins each stop short in a different direction.
    /// <see cref="Markdown.PageBannerTests.Publish_StoredHashIsNotTheHashOfWhatWasUploaded"/> asserts §9.2
    /// against a two-line helper that test file writes itself, so what it pins is the idea, not the
    /// pipeline. <see cref="Publishing.PublishPipelineTests.The_uploaded_body_carries_the_banner_and_the_hash_does_not"/>
    /// asserts it at plan time and as an <em>inequality</em> — "the hash is not the hash of the upload" —
    /// which stays green for any wrong hash that merely differs from that one.
    /// <see cref="Publishing.PublishExecutorTests.Sends_nothing_when_a_second_run_finds_nothing_changed"/>
    /// does cover the round trip, but from a hand-built report: no banner input ever moves, and no config
    /// file is read.
    /// </para>
    /// <para>
    /// <c>dashboard.title</c> is the lever because it is a <c>PageBanner</c> input the publish path reads
    /// nowhere else (§5.1), and unlike the banner's other varying input — the date, which
    /// <c>PublishCommand</c> takes straight from the wall clock — a test can move it. Moving it is also
    /// what stops the silence below being vacuous: the forced run proves the new title really does reach
    /// the body, so the quiet run had something it could have written and declined to.
    /// </para>
    /// <para>
    /// The forced footprint is asserted whole for one absence in particular. Invalidating approval sends
    /// <c>DELETE /wiki/rest/api/content/{id}/label</c> (§6.2 step 7), and <c>PublishPlanner</c> keys that
    /// off <c>bodyChanged</c> alone while <c>force</c> reaches the very same write branch without setting
    /// it. An assertion that only looked for the PUT could not tell those two apart.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_banner_only_change_costs_nothing_and_a_forced_rewrite_moves_neither_the_hash_nor_the_approval()
    {
        var work = Scaffolded(
            nameof(A_banner_only_change_costs_nothing_and_a_forced_rewrite_moves_neither_the_hash_nor_the_approval));

        StubSpace();
        StubCreate();

        Invoke(work, "publish").Code.ShouldBe(0, "The fixture's own first publish failed.");

        var pageId = _created.ShouldHaveSingleItem();
        var published = State(work).Pages[HomePath];

        var created = UploadedBody(Requests("POST", "/wiki/api/v2/pages").ShouldHaveSingleItem());

        const string reaches = "The created body does not carry the dashboard title, so dashboard.title "
            + "is not reaching the banner and the rest of this test proves nothing.";

        created.ShouldContain(DashboardTitle, customMessage: reaches);

        // An approval for the forced rewrite to threaten, recorded the way `sync --labels` records one.
        StateStore.Save(
            StatePath(work),
            StateUpdates.RecordApproval(
                State(work), HomePath, "reviewer@example.com", "2026-07-26T09:00:00Z", published.PublishedVersion));

        RenameDashboard(work, RenamedDashboard);

        _server.ResetLogEntries();
        StubRepublish(RemoteVersion);

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);

        // Asserted as the whole request list rather than "no PUT was sent": a banner-only change leaves the
        // run with nothing to say to Confluence at all, so even a page read would be a regression.
        var quiet = Seen().Select(request => $"{request.Method} {request.Path}").ToList();

        quiet.ShouldBeEmpty(
            $"A banner-only change sent Confluence [{string.Join(", ", quiet)}]. Rule §9.2: the banner is "
            + $"outside contentHash, so moving it is not a content change.{Environment.NewLine}{run.Diagnostics}");

        var skipped = State(work).Pages[HomePath];
        var unmoved = $"A banner-only change moved state.json.{Environment.NewLine}{run.Diagnostics}";

        skipped.ContentHash.ShouldBe(published.ContentHash, unmoved);
        skipped.PublishedVersion.ShouldBe(published.PublishedVersion, unmoved);

        // --force is the escape hatch §6.2 documents, and it is the branch where the two facts could still
        // come apart: the body genuinely is rewritten, with a banner that genuinely differs.
        _server.ResetLogEntries();

        var forced = Invoke(work, "publish", "--force");

        forced.Code.ShouldBe(0, forced.Diagnostics);

        var footprint = Seen().Select(request => $"{request.Method} {request.Path}").ToList();

        var because = $"A forced republish sent Confluence [{string.Join(", ", footprint)}]."
            + Environment.NewLine + forced.Diagnostics;

        footprint.ShouldBe(
            [
                "GET /wiki/api/v2/spaces",
                $"GET /wiki/api/v2/pages/{pageId}",
                $"GET /wiki/api/v2/pages/{pageId}/inline-comments",
                $"PUT /wiki/api/v2/pages/{pageId}",
            ],
            because);

        var rewritten = UploadedBody(Requests("PUT", $"/wiki/api/v2/pages/{pageId}").ShouldHaveSingleItem());

        rewritten.ShouldContain(RenamedDashboard, customMessage: because);
        rewritten.ShouldNotContain(DashboardTitle, customMessage: because);
        rewritten.ShouldNotBe(created, because);

        var after = State(work).Pages[HomePath];

        // Two different bodies went over the wire under one unchanged hash. That is rule §9.2 in one line,
        // and the version proves the rewrite was real rather than a second skip.
        after.PublishedVersion.ShouldBe(RemoteVersion + 1, because);
        after.ContentHash.ShouldBe(published.ContentHash, because);
        after.Approval?.Status.ShouldBe(ApprovalStatus.Approved, because);
    }

    private static DocumeState State(string work) => StateStore.Load(StatePath(work));

    /// <summary>The storage-format body a page write carried.</summary>
    private static string UploadedBody(IRequestMessage request)
    {
        var body = Payload(request)
            .GetProperty("body")
            .GetProperty("storage")
            .GetProperty("value")
            .GetString();

        body.ShouldNotBeNull();

        return body;
    }

    /// <summary>
    /// Renames the dashboard in <c>docume.json</c> — a <c>PageBanner</c> input (§5.1), and the only one of
    /// its three a test can move without a clock. Guarded rather than a bare replace: a scaffolding change
    /// that renamed the field would otherwise leave every assertion downstream passing on an unedited file.
    /// </summary>
    private static void RenameDashboard(string work, string title)
    {
        var path = Path.Combine(work, "docume.json");
        var config = File.ReadAllText(path);
        var quoted = $"\"{DashboardTitle}\"";

        var named = $"The scaffolded docume.json does not name {quoted}, so this test never changed "
            + "the banner.";

        config.ShouldContain(quoted, customMessage: named);

        File.WriteAllText(path, config.Replace(quoted, $"\"{title}\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Adds this suite's space to <c>confluence.protectedSpaces</c>, which is how rule §1.4's write lock
    /// is expressed in a consumer repo (§9.5: the space key belongs in config, not in the tool).
    /// </summary>
    private static void Protect(string work)
    {
        var path = Path.Combine(work, "docume.json");
        var config = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path} parsed as null.");

        config["confluence"]!["protectedSpaces"] = new JsonArray(JsonValue.Create(SpaceKey));

        File.WriteAllText(path, config.ToJsonString());
    }

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

    /// <summary>
    /// What the fake Confluence actually answered a GET of <paramref name="path"/> with. Lets a test that
    /// poisons a response prove the poison was served rather than assume it.
    /// </summary>
    private string Served(string path) =>
        string.Join(
            Environment.NewLine,
            _server.LogEntries
                .Where(entry => string.Equals(entry.RequestMessage?.Path, path, StringComparison.Ordinal)
                    && string.Equals(entry.RequestMessage?.Method, "GET", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.ResponseMessage?.BodyData?.BodyAsString ?? string.Empty));

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
    /// <param name="withoutVersion">
    /// Ids whose hit carries no <c>version</c> at all. The search is asked for <c>expand=version</c> and
    /// normally answers one, but a hit that does not is the condition
    /// <c>LabelReader.VersionsAsync</c> branches on, so it has to be reachable from here.
    /// </param>
    private void StubLabelSearch(
        IReadOnlyList<string> approved,
        IReadOnlyList<string> stale,
        IReadOnlyCollection<string>? withoutVersion = null) =>
        _server
            .Given(Request.Create().WithPath("/wiki/rest/api/content/search").UsingGet())
            .RespondWith(Json(request =>
            {
                var cql = request.Query?["cql"].ToString() ?? string.Empty;
                var hits = cql.Contains("\"approved\"", StringComparison.Ordinal) ? approved : stale;

                var results = hits.Select(id => $$"""
                    {
                      "id": "{{id}}", "type": "page", "status": "current",
                      "title": {{JsonSerializer.Serialize(HomeTitle)}}
                      {{(withoutVersion?.Contains(id) == true ? string.Empty : ", \"version\": { \"number\": 1 }")}}
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

    /// <summary>
    /// What a second publish needs beyond a create: the page as Confluence holds it now (for the version
    /// the update sends), the inline comments a body rewrite could strand (§6.2 step 6), and the update
    /// itself. The comments stub takes priority because it sits under the same <c>pages/{id}/</c> prefix
    /// as the page read.
    /// </summary>
    /// <param name="version">The version the remote is holding when the run reads it.</param>
    /// <param name="storedBody">
    /// Storage-format body to answer the page read with, or <c>null</c> to answer with none. Confluence
    /// is under no obligation to omit a body just because the caller did not ask for one, and
    /// <c>MapPage</c> maps whatever arrives — so this is how a hand-edited page is put in front of the
    /// publish path.
    /// </param>
    private void StubRepublish(int version, string? storedBody = null)
    {
        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/wiki/api/v2/pages/*/inline-comments"))
                .UsingGet())
            .AtPriority(-1)
            .RespondWith(Json("""{ "results": [], "_links": {} }"""));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingGet())
            .RespondWith(Json(request => Page(request, version, storedBody)));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingPut())
            .RespondWith(Json(request => Page(request, version + 1, storedBody)));
    }

    /// <summary>One page, echoing back the id the request named, at the version asked for.</summary>
    private static string Page(IRequestMessage request, int version, string? storedBody)
    {
        var body = storedBody is null
            ? string.Empty
            : $$$""", "body": { "storage": { "value": {{{JsonSerializer.Serialize(storedBody)}}}, "representation": "storage" } }""";

        return $$"""
            {
              "id": "{{request.Path.Split('/')[^1]}}",
              "status": "current",
              "title": {{JsonSerializer.Serialize(HomeTitle)}},
              "spaceId": "{{SpaceId}}",
              "version": { "number": {{version.ToString(CultureInfo.InvariantCulture)}} }{{body}}
            }
            """;
    }

    /// <summary>
    /// Answers §6.2's child-order read with the children already in the order the source tree wants, so
    /// the post-pass has nothing to move and the footprint stays the one the page loop left. Priority as
    /// for the comments stub: this path sits under the page-read wildcard too.
    /// </summary>
    /// <remarks>
    /// The children are every page this server created except the one being asked about, in the order it
    /// created them — which is tree order, because that is the order the run publishes in. Answering from
    /// what the server actually handed out keeps the fixture honest about ids the test never chose.
    /// </remarks>
    private void StubChildren() =>
        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/wiki/api/v2/pages/*/children"))
                .UsingGet())
            .AtPriority(-1)
            .RespondWith(Json(request =>
            {
                var parent = request.Path.Split('/')[^2];

                string[] children;
                lock (_created)
                {
                    children = [.. _created.Where(id => !string.Equals(id, parent, StringComparison.Ordinal))];
                }

                var results = children.Select((id, index) => $$"""
                    {
                      "id": "{{id}}", "status": "current", "type": "page", "spaceId": "{{SpaceId}}",
                      "title": "child {{index.ToString(CultureInfo.InvariantCulture)}}",
                      "childPosition": {{index.ToString(CultureInfo.InvariantCulture)}}
                    }
                    """);

                return $$"""{ "results": [{{string.Join(",", results)}}], "_links": {} }""";
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
