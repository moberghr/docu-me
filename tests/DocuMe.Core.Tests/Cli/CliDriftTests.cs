using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocuMe.Core.State;
using Shouldly;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Cli;

/// <summary>
/// <c>docume drift --mark</c> (PLAN.md §6.4) run as a process against a real throwaway git repository and
/// a local HTTP server (.claude/rules/testing.md §4.2). It is the last Confluence-facing command whose
/// write half had only Core-level cover, and the questions below are the ones only the command can be
/// asked: whether the space lock stops it, whether <c>--dry-run</c> reaches the network at all, what it
/// exits with when a label write is refused, and what survives in the consumer repo's state file.
/// </summary>
/// <remarks>
/// <para>
/// The matcher and the join under this command are covered at the Core level already
/// (<see cref="Drift.DriftPlannerTests"/>, <see cref="Drift.DriftMarkPlannerTests"/>), and none of that
/// reaches the option binding, the two-console split the machine formats need, or the order in which the
/// command writes labels, state and the dashboard.
/// </para>
/// <para>
/// The git repository is real rather than stubbed, for the reason <see cref="Git.GitRepositoryTests"/>
/// gives: what is worth pinning is the paths git actually prints, and a fake diff would only prove the
/// globs can match strings this suite invented. The repo is scaffolded by <c>docume init</c> and its state
/// file is then given page ids directly — a mark labels pages a publish recorded, and seeding that fact
/// costs a stub set and a process run less than earning it.
/// </para>
/// </remarks>
public sealed class CliDriftTests : IDisposable
{
    private const string SpaceKey = "SBX";
    private const string SpaceId = "98304";

    /// <summary>The dashboard title in the scaffolded docume.json (§6.5), which <c>--mark</c> refreshes.</summary>
    private const string DashboardTitle = "Documentation Status";

    /// <summary>
    /// The dashboard's own page id — deliberately unlike <see cref="LimitsPageId"/> and
    /// <see cref="RatesPageId"/>, so "the refresh wrote the dashboard" and "the refresh wrote a page this
    /// run just labelled" cannot read the same in an asserted request path.
    /// </summary>
    private const string DashboardPageId = "770900";

    /// <summary>The version the dashboard is found at, so the refresh's PUT has a revision to increment.</summary>
    private const int DashboardVersion = 4;

    /// <summary>The default <c>labels.stale</c> from the scaffolded docume.json.</summary>
    private const string StaleLabel = "stale";

    /// <summary>
    /// The two pages this suite drifts, in the path order the report and the request sequence both use —
    /// so "the first write landed and the second was refused" names a fixed page either way.
    /// </summary>
    private const string LimitsPath = "limits.md";

    private const string LimitsTitle = "Limits";

    private const string LimitsPageId = "770101";

    private const string RatesPath = "rates.md";

    private const string RatesTitle = "Rates";

    private const string RatesPageId = "770102";

    /// <summary>The globs the two pages declare, and the two the seal is computed over.</summary>
    private const string LimitsGlob = "src/limits/*.cs";

    private const string RatesGlob = "src/rates/*.cs";

    /// <summary>
    /// The two owner spellings this suite writes into frontmatter (spec §3.1). Deliberately unalike: a
    /// team handle a forge resolves into a mention, and a display name that resolves to nobody anywhere.
    /// Both have to reach the comment as written, and the second is the one a tool "helpfully"
    /// normalizing would turn into a notification for a stranger.
    /// </summary>
    private const string LimitsOwner = "@moberghr/lending";

    private const string RatesOwner = "Alice Smith";

    /// <summary>The date <see cref="Seal"/> stamps, so the disclosure can be asserted verbatim.</summary>
    private const string SealedOn = "2026-08-19T09:12:44Z";

    private readonly WireMockServer _server = WireMockServer.Start();

    private readonly string _root = Directory.CreateTempSubdirectory("docume-cli-drift").FullName;

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// §6.4's write half through the command: every affected page gets the <c>stale</c> label, the state
    /// file records it, and the dashboard is refreshed from the state this run just wrote. The label name
    /// comes out of <c>docume.json</c>, so this is the first layer at which "the configured label reached
    /// Confluence" means anything.
    /// </summary>
    [Fact]
    public void Drift_mark_labels_every_affected_page_and_records_it_as_stale()
    {
        var work = Seeded(nameof(Drift_mark_labels_every_affected_page_and_records_it_as_stale));

        // Renamed away from the default, because "the label came out of docume.json" and "the command
        // carries the literal 'stale'" are indistinguishable while the two agree.
        const string configured = "needs-review";

        Relabel(work, configured);

        StubLabels();
        StubDashboard();

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldBe(0, run.Diagnostics);

        var labelled = LabelWrites();

        labelled.ShouldBe([LimitsPageId, RatesPageId], run.Diagnostics);

        // The v1 label body is an array of {prefix, name}, and 'global' is the prefix an ordinary
        // human-visible label lives under — the one a reviewer sees on the page.
        var posted = Payload("POST", $"/wiki/rest/api/content/{RatesPageId}/label")[0];

        posted.GetProperty("name").GetString().ShouldBe(configured, run.Diagnostics);
        posted.GetProperty("prefix").GetString().ShouldBe("global", run.Diagnostics);

        // The flag is what stops the next run re-labelling, and it has to be on disk rather than in the
        // report: the six-hourly drift job is a fresh process every time.
        var state = State(work);

        state.Pages[LimitsPath].Stale.ShouldBeTrue(run.Diagnostics);
        state.Pages[RatesPath].Stale.ShouldBeTrue(run.Diagnostics);

        // Refreshed from the just-marked state, so the page agrees with the labels before any later sync.
        var titles = Requests("POST", "/wiki/api/v2/pages")
            .Select(request => Payload(request).GetProperty("title").GetString())
            .ToList();

        titles.ShouldBe([DashboardTitle], $"The dashboard was not refreshed.{Environment.NewLine}{run.Diagnostics}");
    }

    /// <summary>
    /// What the refreshed dashboard actually says about the pages this run just marked. §6.4 pairs the
    /// marking with the refresh so a human sees the drift on the one page §9.3 makes the staleness surface,
    /// and <c>DriftCommand.RefreshDashboardAsync</c> claims the page "agrees with the labels this run wrote
    /// even before the cron <c>sync</c> commits them".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The label search answers empty here, which is the ordinary case rather than a contrived one: CQL is
    /// index-backed, and a label written milliseconds earlier in the same run need not be in it yet. So this
    /// is the shape in which the sentence above is worth anything — with a search that already returned the
    /// pages, "rendered from the just-marked state" and "rendered from the search" are indistinguishable.
    /// </para>
    /// <para>
    /// Asserted as counts and as the two named rows, because the claim's subject is what the page says. The
    /// dashboard was already known to be posted (see
    /// <see cref="Drift_mark_labels_every_affected_page_and_records_it_as_stale"/>) — that a request went
    /// out is a boolean, and no boolean can fail a claim about a body.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_dashboard_a_mark_run_publishes_shows_the_pages_it_just_marked_as_stale()
    {
        var work = Seeded(nameof(The_dashboard_a_mark_run_publishes_shows_the_pages_it_just_marked_as_stale));

        StubLabels();
        StubDashboard();

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldBe(0, run.Diagnostics);

        // The anchor for everything below: state and the dashboard are two surfaces of one fact, written
        // seconds apart by one process, and the failure this guards against is them disagreeing.
        var state = State(work);

        state.Pages[LimitsPath].Stale.ShouldBeTrue(run.Diagnostics);
        state.Pages[RatesPath].Stale.ShouldBeTrue(run.Diagnostics);

        var body = Body("POST", "/wiki/api/v2/pages");

        // Counted first and alone, so a regression reads as a sentence rather than as a body diff.
        var marked = Occurrences(body, PageStaleCell);

        var because = $"The dashboard shows {marked} page(s) as stale in the run that labelled 2 and saved "
            + $"both to state.json.{Environment.NewLine}{run.Diagnostics}";

        marked.ShouldBe(2, because);

        // The summary above the table, which is the number a reader takes in first and is built from the
        // reconciled state rather than from the search's own count.
        body.ShouldContain($"{SummaryStaleCell}{Environment.NewLine}<td>2</td>", customMessage: run.Diagnostics);

        // Which two, not just how many: a render that marked the home page and one other would also count 2.
        Row(body, LimitsTitle).ShouldContain(PageStaleCell, customMessage: run.Diagnostics);
        Row(body, RatesTitle).ShouldContain(PageStaleCell, customMessage: run.Diagnostics);
    }

    /// <summary>
    /// The read half of the footprint below, which nothing counted: two CQL label searches, one space
    /// lookup and one title lookup — four requests for the run, not four per page. The fixture drifts two
    /// pages, so a refresh that moved inside the label loop, or a reader that asked per page, is a
    /// different number here rather than the same one.
    /// </summary>
    /// <remarks>
    /// A read costs rate-limit budget on a six-hourly job like any other request, and this class already
    /// treats one as a promise broken where it asserts <c>--dry-run</c> reaches Confluence for nothing.
    /// </remarks>
    [Fact]
    public void The_whole_read_footprint_of_a_mark_run_is_four_requests_however_many_pages_drifted()
    {
        var work = Seeded(nameof(The_whole_read_footprint_of_a_mark_run_is_four_requests_however_many_pages_drifted));

        StubLabels();
        StubDashboard();

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldBe(0, run.Diagnostics);

        var reads = Seen()
            .Where(request => string.Equals(request.Method, "GET", StringComparison.Ordinal))
            .Select(request => $"GET {request.Path}")
            .ToList();

        var expected = new List<string>
        {
            "/wiki/rest/api/content/search",
            "/wiki/rest/api/content/search",
            "/wiki/api/v2/spaces",
            "/wiki/api/v2/pages",
        }
            .Select(path => $"GET {path}")
            .ToList();

        var because = $"A two-page mark run read Confluence {reads.Count} time(s)."
            + Environment.NewLine + run.Diagnostics;

        reads.ShouldBe(expected, because);
    }

    /// <summary>
    /// Rule §9.3, as the whole write footprint of a mark run: two labels and one dashboard page, and
    /// nothing else. Asserted as an exact set rather than as "no page-body edit", because a negative
    /// nothing currently does is a test that cannot fail — this one goes red the moment any write is
    /// added, which is how a page-body edit would arrive.
    /// </summary>
    /// <remarks>
    /// A body edit is the specific write §6.4 rules out: it bumps the page version, which invalidates no
    /// approval but does disturb the history §8 keeps for audit.
    /// </remarks>
    [Fact]
    public void The_whole_write_footprint_of_a_mark_run_is_two_labels_and_the_dashboard()
    {
        var work = Seeded(nameof(The_whole_write_footprint_of_a_mark_run_is_two_labels_and_the_dashboard));

        StubLabels();
        StubDashboard();

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldBe(0, run.Diagnostics);

        var writes = Writes()
            .Select(request => $"{request.Method} {request.Path}")
            .ToList();

        writes.ShouldBe(
            [
                $"POST /wiki/rest/api/content/{LimitsPageId}/label",
                $"POST /wiki/rest/api/content/{RatesPageId}/label",
                "POST /wiki/api/v2/pages",
            ],
            run.Diagnostics);
    }

    /// <summary>
    /// The same footprint one run later, which is the run that actually recurs. Every check above stubs the
    /// dashboard as absent, so §6.5's refresh is always a create — but a create happens once and the
    /// six-hourly job takes the update branch forever after, and that branch had no whole-write assertion
    /// behind it. Only the upsert did, one layer down
    /// (<see cref="Dashboard.DashboardPublisherTests.Writes_a_version_when_the_data_above_the_provenance_line_moved"/>),
    /// which cannot see what else the command wrote around it.
    /// </summary>
    /// <remarks>
    /// What an exact list catches here and the create-path one cannot: a write aimed at the wrong page. A
    /// create names no page in its path, so the update branch is the only place the dashboard's id can be
    /// confused with a page id this very run holds in hand — and a body write onto one of those is exactly
    /// §9.3's forbidden edit, landing on a page a reviewer may have approved.
    /// </remarks>
    [Fact]
    public void A_mark_run_that_finds_its_dashboard_updates_that_page_and_no_other()
    {
        var work = Seeded(nameof(A_mark_run_that_finds_its_dashboard_updates_that_page_and_no_other));

        // A sentence no render produces, so the body assertion below tells "wrote the render" apart from
        // "echoed back what it read" — the stored dashboard is the product's one page-body read (§9.1).
        const string handEdit = "<p>NOTE from a reviewer: keep this paragraph.</p>";

        StubLabels();
        StubExistingDashboard(handEdit);

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldBe(0, run.Diagnostics);

        var writes = Writes()
            .Select(request => $"{request.Method} {request.Path}")
            .ToList();

        writes.ShouldBe(
            [
                $"POST /wiki/rest/api/content/{LimitsPageId}/label",
                $"POST /wiki/rest/api/content/{RatesPageId}/label",
                $"PUT /wiki/api/v2/pages/{DashboardPageId}",
            ],
            run.Diagnostics);

        // The one body a mark run may write, checked for what is in it rather than that it was sent.
        var body = Payload("PUT", $"/wiki/api/v2/pages/{DashboardPageId}");

        body.GetProperty("title").GetString().ShouldBe(DashboardTitle, run.Diagnostics);

        const string echoed = "The dashboard refresh echoed back part of the body it read, so a hand edit "
            + "in Confluence survives the next refresh and the page has two sources of truth (rule §9.1).";

        var stored = body.GetProperty("body").GetProperty("storage").GetProperty("value").GetString();

        stored.ShouldNotBeNull(run.Diagnostics);
        stored!.ShouldNotContain("NOTE from a reviewer", Case.Sensitive, echoed);
    }

    /// <summary>
    /// The promise <c>DriftCommand</c>'s own docs make: <c>--mark --dry-run</c> is wholly offline. Run
    /// with the credential variables emptied, so a regression that built a client would fail here rather
    /// than pass on whatever the developer happens to have exported, and asserted as zero requests —
    /// a read is a promise broken too.
    /// </summary>
    [Fact]
    public void A_mark_dry_run_asks_confluence_for_nothing_and_needs_no_credentials()
    {
        var work = Seeded(nameof(A_mark_dry_run_asks_confluence_for_nothing_and_needs_no_credentials));

        var before = File.ReadAllBytes(StatePath(work));

        var run = DocumeCli.Invoke(
            work,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DOCUME_CONFLUENCE_EMAIL"] = string.Empty,
                ["DOCUME_CONFLUENCE_TOKEN"] = string.Empty,
            },
            "drift",
            "--mark",
            "--dry-run");

        run.Code.ShouldBe(0, run.Diagnostics);

        // The plan is the point of the run: both pages named, so a reviewer can approve exactly what a
        // real run would then do.
        run.Flowed.ShouldContain(LimitsPath, customMessage: run.Diagnostics);
        run.Flowed.ShouldContain(RatesPath, customMessage: run.Diagnostics);

        var asked = Seen()
            .Select(request => $"{request.Method} {request.Path}")
            .ToList();

        var because = $"`drift --mark --dry-run` sent Confluence {asked.Count} request(s): "
            + $"[{string.Join(", ", asked)}].{Environment.NewLine}{run.Diagnostics}";

        asked.ShouldBeEmpty(because);

        File.ReadAllBytes(StatePath(work)).ShouldBe(
            before,
            $"`drift --mark --dry-run` rewrote the state file.{Environment.NewLine}{run.Diagnostics}");
    }

    /// <summary>
    /// Rule §1.4 / §0.1: a label is a write, and a write into a protected space is refused before any
    /// request. The refusal is resolved from the config alone, so it costs no rate-limit budget to learn
    /// what <c>docume.json</c> already said.
    /// </summary>
    [Fact]
    public void Drift_mark_is_refused_when_the_space_is_protected()
    {
        var work = Seeded(nameof(Drift_mark_is_refused_when_the_space_is_protected));

        Protect(work);
        StubLabels();
        StubDashboard();

        var before = File.ReadAllBytes(StatePath(work));

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldNotBe(0, run.Diagnostics);
        run.FlowedAll.ShouldContain("protectedSpaces", customMessage: run.Diagnostics);

        var asked = Seen()
            .Select(request => $"{request.Method} {request.Path}")
            .ToList();

        var because = $"`drift --mark` reached Confluence before refusing: [{string.Join(", ", asked)}]."
            + Environment.NewLine + run.Diagnostics;

        asked.ShouldBeEmpty(because);

        File.ReadAllBytes(StatePath(work)).ShouldBe(before, run.Diagnostics);
    }

    /// <summary>
    /// The other side of the lock: a dry run against a protected space still prints its plan, and says
    /// out loud that a real run would refuse. Refusing the plan too would leave a repo waiting on a
    /// go-live decision unable to see what it is waiting for, and nothing a dry run does is destructive.
    /// </summary>
    [Fact]
    public void A_protected_space_still_gets_its_mark_plan_under_dry_run()
    {
        var work = Seeded(nameof(A_protected_space_still_gets_its_mark_plan_under_dry_run));

        Protect(work);

        var run = Invoke(work, "drift", "--mark", "--dry-run");

        run.Code.ShouldBe(0, run.Diagnostics);

        // Both halves of the sentence: the plan, and the warning that it is a plan only.
        run.Flowed.ShouldContain(RatesPath, customMessage: run.Diagnostics);
        run.FlowedAll.ShouldContain("protectedSpaces", customMessage: run.Diagnostics);
        run.FlowedAll.ShouldContain("a real run refuses", customMessage: run.Diagnostics);

        Seen().ShouldBeEmpty(run.Diagnostics);
    }

    /// <summary>
    /// A label write Confluence refuses: exit non-zero, keep the labels that did land, and stop. The
    /// scaffolded drift job reads nothing but the exit code, and state that denied a label already on a
    /// page would make the next run spend a request re-adding it.
    /// </summary>
    [Fact]
    public void Drift_mark_keeps_the_labels_that_landed_when_a_later_write_is_refused()
    {
        var work = Seeded(nameof(Drift_mark_keeps_the_labels_that_landed_when_a_later_write_is_refused));

        StubLabel(LimitsPageId);
        StubDashboard();

        // 400 rather than a 5xx: an invalid label is what a refusal really arrives as, and a 5xx would
        // spend the retry pipeline's backoff before failing.
        _server
            .Given(Request.Create().WithPath($"/wiki/rest/api/content/{RatesPageId}/label").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.BadRequest)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{ "errors": [{ "title": "Label name is not valid" }] }"""));

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldNotBe(0, run.Diagnostics);

        var state = State(work);

        state.Pages[LimitsPath].Stale.ShouldBeTrue(
            $"The label that landed was not recorded.{Environment.NewLine}{run.Diagnostics}");

        state.Pages[RatesPath].Stale.ShouldBeFalse(
            $"A refused label was recorded as written.{Environment.NewLine}{run.Diagnostics}");

        // The run stopped at the failure: a dashboard refreshed after a half-done mark would publish a
        // page claiming a state Confluence does not have.
        var because = "The dashboard was refreshed after a refused label write."
            + Environment.NewLine + run.Diagnostics;

        Requests("POST", "/wiki/api/v2/pages").ShouldBeEmpty(because);
    }

    /// <summary>
    /// <c>--fail-on-drift</c> survives a successful mark. The two answers are independent — "the labels
    /// were written" and "pages drifted" — and a team that opted into a blocking check would otherwise
    /// find that adding <c>--mark</c> to the same command line silently turned it green again.
    /// </summary>
    [Fact]
    public void A_successful_mark_still_fails_the_run_when_fail_on_drift_was_asked_for()
    {
        var work = Seeded(nameof(A_successful_mark_still_fails_the_run_when_fail_on_drift_was_asked_for));

        StubLabels();
        StubDashboard();

        var run = Invoke(work, "drift", "--mark", "--fail-on-drift");

        run.Code.ShouldBe(1, run.Diagnostics);

        // Non-zero for the advisory reason, not because the write half gave up: both labels landed.
        LabelWrites().ShouldBe([LimitsPageId, RatesPageId], run.Diagnostics);
        State(work).Pages[RatesPath].Stale.ShouldBeTrue(run.Diagnostics);
    }

    /// <summary>
    /// An already-marked page costs no request. <c>sync --labels</c> reconciles the flag from the live
    /// labels, so re-adding one state already records would be a request that changes nothing — on a
    /// six-hourly job over a drifted tree that is the difference between a handful of writes and all of
    /// them, every run.
    /// </summary>
    [Fact]
    public void An_already_marked_page_is_skipped_without_a_request()
    {
        var work = Seeded(nameof(An_already_marked_page_is_skipped_without_a_request));

        MarkStale(work, LimitsPath);

        StubLabels();
        StubDashboard();

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldBe(0, run.Diagnostics);

        LabelWrites().ShouldBe(
            [RatesPageId],
            $"The already-marked page was re-labelled.{Environment.NewLine}{run.Diagnostics}");

        // Named rather than silently dropped, so the log accounts for every affected page the report
        // above it listed.
        run.FlowedAll.ShouldContain("already marked", customMessage: run.Diagnostics);
    }

    /// <summary>
    /// <c>--format json</c> with <c>--mark</c>: stdout carries the report and nothing else. A CI step
    /// pipes that into a parser, and a "+stale" line in the middle of it is a corrupt payload — so the
    /// write half's log has to go to stderr, which is a split only the process can be asked about.
    /// </summary>
    [Fact]
    public void A_json_mark_run_keeps_its_write_log_off_stdout()
    {
        var work = Seeded(nameof(A_json_mark_run_keeps_its_write_log_off_stdout));

        StubLabels();
        StubDashboard();

        var run = Invoke(work, "drift", "--mark", "--format", "json");

        run.Code.ShouldBe(0, run.Diagnostics);

        // Parsed whole: the assertion is that stdout is one JSON document, not that it contains one.
        using var document = JsonDocument.Parse(run.Output);

        document.RootElement.GetProperty("affectedCount").GetInt32().ShouldBe(2, run.Diagnostics);

        // The log did happen — it went to the other stream. Without this the test would also pass for a
        // run that marked nothing and printed nothing.
        run.Error.ShouldContain(RatesPath, customMessage: run.Diagnostics);
        run.Output.ShouldNotContain(StaleLabel, customMessage: run.Diagnostics);
    }

    /// <summary>
    /// <c>--mark</c> joins the affected pages onto state to find their page ids, so a missing state file
    /// is not "nothing to mark" — it is a run that can never mark anything. Saying so beats reporting
    /// every affected page as unpublished and exiting 0.
    /// </summary>
    [Fact]
    public void Drift_mark_stops_when_there_is_no_state_file_to_join_onto()
    {
        var work = Seeded(nameof(Drift_mark_stops_when_there_is_no_state_file_to_join_onto));

        File.Delete(StatePath(work));

        // --baseline is load-bearing: without it a run that lost this guard would die on "no baseline to
        // diff from" instead, and the test would pass for a reason that has nothing to do with state.
        var run = Invoke(work, "drift", "--mark", "--baseline", "HEAD~1");

        run.Code.ShouldNotBe(0, run.Diagnostics);
        run.FlowedAll.ShouldContain("docume publish", customMessage: run.Diagnostics);

        Seen().ShouldBeEmpty(run.Diagnostics);
    }

    /// <summary>
    /// A flag that quietly did nothing would be worse than an unknown-option error: <c>--dry-run</c> only
    /// means something for the write half, so a caller passing it to a read-only run has misunderstood
    /// what is about to happen and should be told rather than reassured.
    /// </summary>
    [Fact]
    public void Dry_run_without_mark_is_an_error_rather_than_a_no_op()
    {
        var work = Seeded(nameof(Dry_run_without_mark_is_an_error_rather_than_a_no_op));

        var run = Invoke(work, "drift", "--dry-run");

        run.Code.ShouldNotBe(0, run.Diagnostics);
        run.FlowedAll.ShouldContain("--mark", customMessage: run.Diagnostics);
    }

    /// <summary>
    /// The exemption file end-to-end (§6.4): with every changed file matched by
    /// <c>_meta/drift-ignore</c>, the run that would have reported both pages reports neither, and
    /// <c>--fail-on-drift</c> exits 0 because there is no drift left to fail on. The exemptions are
    /// named rather than swallowed — the count line, the reason a pattern carried, and the file whose
    /// pattern carried none rendered without a dangling separator.
    /// </summary>
    /// <remarks>
    /// The matcher itself is covered at the Core level (<see cref="Drift.DriftExemptionsTests"/>);
    /// what only the process can answer is whether the command finds the file next to the state it
    /// already reads, and what the exempt section looks like on the stream a user gets.
    /// </remarks>
    [Fact]
    public void A_change_the_ignore_file_matches_is_reported_exempt_rather_than_as_drift()
    {
        var work = Seeded(nameof(A_change_the_ignore_file_matches_is_reported_exempt_rather_than_as_drift));

        ExemptSources(
            work,
            "# mechanical sweeps never mean the docs moved",
            "src/limits/*.cs # rename-only sweep",
            "src/rates/*.cs");

        var run = Invoke(work, "drift", "--fail-on-drift");

        run.Code.ShouldBe(0, run.Diagnostics);

        // Neither page is reported: with every match exempt the verdict is the no-drift one, which
        // is what --fail-on-drift turned into the exit 0 above.
        run.Flowed.ShouldNotContain(LimitsPath, customMessage: run.Diagnostics);
        run.Flowed.ShouldNotContain(RatesPath, customMessage: run.Diagnostics);

        run.Flowed.ShouldContain(
            "2 changed file(s) ignored by _meta/drift-ignore",
            customMessage: run.Diagnostics);

        run.Flowed.ShouldContain(
            "src/limits/Limits.cs (src/limits/*.cs — rename-only sweep)",
            customMessage: run.Diagnostics);

        run.Flowed.ShouldContain(
            "src/rates/Rates.cs (src/rates/*.cs)",
            customMessage: run.Diagnostics);
    }

    /// <summary>
    /// <c>--format json</c> carries the exempted list, next to a count the exemption changed: one
    /// pattern takes one of the two changed files out of the match, so the same payload says both
    /// "one page drifted" and "one change was counted out, and here is why". A CI step that posts
    /// the report can then account for every changed file rather than only the ones that drifted.
    /// </summary>
    [Fact]
    public void A_json_report_carries_the_exempted_files()
    {
        var work = Seeded(nameof(A_json_report_carries_the_exempted_files));

        ExemptSources(work, "src/limits/*.cs # rename-only sweep");

        var run = Invoke(work, "drift", "--format", "json");

        run.Code.ShouldBe(0, run.Diagnostics);

        using var document = JsonDocument.Parse(run.Output);

        document.RootElement.GetProperty("affectedCount").GetInt32().ShouldBe(1, run.Diagnostics);

        var exempted = document.RootElement.GetProperty("exempted");

        exempted.GetArrayLength().ShouldBe(1, run.Diagnostics);

        var entry = exempted[0];

        entry.GetProperty("path").GetString().ShouldBe("src/limits/Limits.cs", run.Diagnostics);
        entry.GetProperty("pattern").GetString().ShouldBe("src/limits/*.cs", run.Diagnostics);
        entry.GetProperty("reason").GetString().ShouldBe("rename-only sweep", run.Diagnostics);
    }

    /// <summary>
    /// <c>--mark</c> considers only non-exempt matches (§6.4): with every match exempt there is no
    /// drift, so the write half has nothing to plan and never builds a client. Asserted as zero
    /// requests, the way the dry-run fact above is — the failure this guards against is a mark run
    /// labelling pages whose only "drift" was a sweep the repo declared mechanical.
    /// </summary>
    [Fact]
    public void A_mark_run_labels_nothing_when_every_match_is_exempt()
    {
        var work = Seeded(nameof(A_mark_run_labels_nothing_when_every_match_is_exempt));

        ExemptSources(work, "src/limits/*.cs", "src/rates/*.cs");

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldBe(0, run.Diagnostics);

        LabelWrites().ShouldBeEmpty(run.Diagnostics);
        Seen().ShouldBeEmpty(run.Diagnostics);

        // The flags stay down too: state that recorded an exempted change as staleness would make
        // the next sync report labels Confluence never got.
        var state = State(work);

        state.Pages[LimitsPath].Stale.ShouldBeFalse(run.Diagnostics);
        state.Pages[RatesPath].Stale.ShouldBeFalse(run.Diagnostics);
    }

    /// <summary>
    /// A malformed exemption line fails the run, naming the file and the 1-based line, so the fix is
    /// an edit rather than a search. Loud beats lenient here: an exemption list silently half-read
    /// would silently un-report drift, which is worse than reporting too much (§6.4).
    /// </summary>
    [Fact]
    public void A_malformed_drift_ignore_line_fails_the_run_naming_its_line()
    {
        var work = Seeded(nameof(A_malformed_drift_ignore_line_fails_the_run_naming_its_line));

        // Line 3 has a reason and no pattern: not a comment, because its `#` is not at line start.
        ExemptSources(
            work,
            "# a comment and a pattern both parse",
            "src/limits/*.cs # rename-only sweep",
            " # a reason with no pattern");

        var run = Invoke(work, "drift");

        run.Code.ShouldNotBe(0, run.Diagnostics);

        // De-wrapped before matching: the message opens with the file's full path, one long token the
        // console wraps mid-word, so "drift-ignore" can arrive split across a padded line break.
        var unwrapped = string.Concat(run.FlowedAll.Where(c => !char.IsWhiteSpace(c)));
        unwrapped.ShouldContain("drift-ignore", customMessage: run.Diagnostics);
        unwrapped.ShouldContain("line3", Case.Insensitive, run.Diagnostics);
    }

    /// <summary>
    /// The machine formats route the failure to stderr, and that is a wire contract: a CI step piping
    /// stdout into a PR comment must never post a parse error as the comment body.
    /// </summary>
    [Fact]
    public void A_malformed_drift_ignore_line_stays_off_stdout_in_a_machine_format()
    {
        var work = Seeded(nameof(A_malformed_drift_ignore_line_stays_off_stdout_in_a_machine_format));

        ExemptSources(
            work,
            "src/limits/*.cs # rename-only sweep",
            " # a reason with no pattern");

        var run = Invoke(work, "drift", "--format", "json");

        run.Code.ShouldNotBe(0, run.Diagnostics);
        run.Error.ShouldContain("line 2", Case.Insensitive, run.Diagnostics);
        run.Output.ShouldNotContain("{", customMessage: run.Diagnostics);
    }

    /// <summary>
    /// The commit exemption end-to-end (§6.4): with the one commit in range listed in
    /// <c>_meta/drift-ignore-revs</c>, the run that would have reported both pages reports neither,
    /// and <c>--fail-on-drift</c> exits 0 because no drift is left to fail on. The narrowing is
    /// disclosed rather than swallowed: the IGNORED COMMITS line renders, naming the file and the
    /// count, because a quiet verdict over a narrowed diff must say the diff was narrowed.
    /// </summary>
    /// <remarks>
    /// The parser is covered at the Core level (<c>DriftIgnoreRevsTests</c>); what only the process
    /// can answer is whether the command finds the file next to the exemption list it already reads,
    /// switches the diff to per-commit attribution, and renders the disclosure.
    /// </remarks>
    [Fact]
    public void A_commit_listed_in_drift_ignore_revs_no_longer_marks_the_page()
    {
        var work = Seeded(nameof(A_commit_listed_in_drift_ignore_revs_no_longer_marks_the_page));

        // The sweep is the fixture's second commit, the one that moved the code out from under
        // both pages, captured the way the fixture's own Commit helper reads a sha.
        var sweep = Git(work, "rev-parse", "HEAD").Trim();

        ExemptCommits(
            work,
            "# the rewrite of both sources was a mechanical sweep",
            sweep);

        var run = Invoke(work, "drift", "--fail-on-drift");

        run.Code.ShouldBe(0, run.Diagnostics);

        // Neither page is reported: every change in range came from the ignored commit.
        run.Flowed.ShouldNotContain(LimitsPath, customMessage: run.Diagnostics);
        run.Flowed.ShouldNotContain(RatesPath, customMessage: run.Diagnostics);

        // De-wrapped before matching: the line ends in one long token the console wraps mid-word,
        // the way the malformed-line fact above reads its path.
        var unwrapped = string.Concat(run.Flowed.Where(c => !char.IsWhiteSpace(c)));

        unwrapped.ShouldContain("IGNOREDCOMMITS", customMessage: run.Diagnostics);
        unwrapped.ShouldContain("1commit(s)heldoutby_meta/drift-ignore-revs", customMessage: run.Diagnostics);
    }

    /// <summary>
    /// The steady state of a long-lived sweep list: every sha in it is older than the current
    /// baseline. A file that ignores nothing in range must not change the answer, because the walk
    /// and the flat diff are different algorithms (a merge's resolution, a diverged baseline) and
    /// with nothing ignored there is no disclosure to say one replaced the other. The run answers
    /// exactly as it would with no file at all.
    /// </summary>
    [Fact]
    public void A_revs_file_naming_nothing_in_range_changes_nothing()
    {
        var work = Seeded(nameof(A_revs_file_naming_nothing_in_range_changes_nothing));

        ExemptCommits(
            work,
            "# a sweep sha from another era, long before this baseline",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var baseline = Invoke(work, "drift", "--format", "json");
        var report = JsonNode.Parse(baseline.Output)!;

        baseline.Code.ShouldBe(0, baseline.Diagnostics);
        report["ignoredCommitCount"]!.GetValue<int>().ShouldBe(0);

        // The fixture's sweep commit really did move both pages' sources, so the honest answer is
        // drift — the same answer a run without the file gives, byte for byte on the wire shape.
        report["affectedCount"]!.GetValue<int>().ShouldBe(2, baseline.Diagnostics);
        report["hasDrift"]!.GetValue<bool>().ShouldBeTrue(baseline.Diagnostics);
    }

    /// <summary>
    /// The write path under the revs file: when every change in range came from an ignored commit
    /// there is nothing to label, and the run says so before it reads a credential or sends a
    /// request (§9.3, §0.1).
    /// </summary>
    [Fact]
    public void A_mark_run_labels_nothing_when_the_only_drift_is_an_ignored_commit()
    {
        var work = Seeded(nameof(A_mark_run_labels_nothing_when_the_only_drift_is_an_ignored_commit));

        var sweep = Git(work, "rev-parse", "HEAD").Trim();
        ExemptCommits(work, sweep);

        // Nothing stubbed on purpose: a request of any kind would answer 404 and fail the run.
        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldBe(0, run.Diagnostics);
        LabelWrites().ShouldBeEmpty(run.Diagnostics);
        Seen().ShouldBeEmpty(run.Diagnostics);

        var state = State(work);
        state.Pages[LimitsPath].Stale.ShouldBeFalse(run.Diagnostics);
        state.Pages[RatesPath].Stale.ShouldBeFalse(run.Diagnostics);
    }

    /// <summary>
    /// <c>--format json</c> carries the commit disclosure the same way it carries the exempted
    /// files: a CI step reading <c>affectedCount: 0</c> can see from the same payload that one
    /// commit was held out of the attribution rather than that the diff was clean.
    /// </summary>
    [Fact]
    public void A_json_report_carries_the_ignored_commit_count()
    {
        var work = Seeded(nameof(A_json_report_carries_the_ignored_commit_count));

        ExemptCommits(work, Git(work, "rev-parse", "HEAD").Trim());

        var run = Invoke(work, "drift", "--format", "json");

        run.Code.ShouldBe(0, run.Diagnostics);

        using var document = JsonDocument.Parse(run.Output);

        document.RootElement.GetProperty("ignoredCommitCount").GetInt32().ShouldBe(1, run.Diagnostics);
        document.RootElement.GetProperty("affectedCount").GetInt32().ShouldBe(0, run.Diagnostics);
    }

    /// <summary>
    /// A malformed revs line fails the run, naming the file and the 1-based line, for the reason a
    /// malformed glob does: a commit list silently half-read would silently un-report drift. In a
    /// machine format the failure goes to stderr and stdout stays empty of payload.
    /// </summary>
    [Fact]
    public void A_malformed_drift_ignore_revs_line_fails_the_run_naming_its_line()
    {
        var work = Seeded(nameof(A_malformed_drift_ignore_revs_line_fails_the_run_naming_its_line));

        // Line 2 is a short sha. Git refuses an abbreviation in blame.ignoreRevsFile too (`fatal:
        // invalid object name`), and this format draws the line in the same place: an abbreviation
        // is ambiguous the day the repo grows.
        ExemptCommits(
            work,
            "# a comment parses",
            "deadbeef");

        var run = Invoke(work, "drift", "--format", "json");

        run.Code.ShouldNotBe(0, run.Diagnostics);
        run.Error.ShouldContain("line 2", Case.Insensitive, run.Diagnostics);
        run.Error.ShouldContain("drift-ignore-revs", customMessage: run.Diagnostics);
        run.Output.ShouldNotContain("{", customMessage: run.Diagnostics);
    }

    /// <summary>
    /// Per-commit attribution, not per-file forgiveness: a file the ignored sweep touched drifts
    /// anyway when a commit that is not ignored touched it too. Only the page whose sole change
    /// was the sweep goes quiet, so both directions of the attribution show in one report.
    /// </summary>
    [Fact]
    public void A_file_changed_by_both_an_ignored_and_a_real_commit_still_drifts()
    {
        var work = Seeded(nameof(A_file_changed_by_both_an_ignored_and_a_real_commit_still_drifts));

        var sweep = Git(work, "rev-parse", "HEAD").Trim();

        Write(work, "src/limits/Limits.cs", "// the limits, rewritten again and for real\n");
        Commit(work, "a real change on top of the sweep");

        ExemptCommits(work, sweep);

        var run = Invoke(work, "drift", "--format", "json");

        run.Code.ShouldBe(0, run.Diagnostics);

        using var document = JsonDocument.Parse(run.Output);

        var pages = document.RootElement.GetProperty("pages");

        pages.GetArrayLength().ShouldBe(1, run.Diagnostics);
        pages[0].GetProperty("path").GetString().ShouldBe(LimitsPath, run.Diagnostics);

        document.RootElement.GetProperty("ignoredCommitCount").GetInt32().ShouldBe(1, run.Diagnostics);
    }

    /// <summary>
    /// SC1, the fact the sealed-verdict slice exists for, and the only one here that can fail for the
    /// reason the design is about: the diff really did touch this page's sources, and the bytes under
    /// them are the ones its live body was published against, so it is disclosed rather than reported.
    /// SC3 rides along beside it — the page with no seal keeps exactly today's range-based answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the fact that catches the recompute the design cannot survive.</strong> The seal
    /// covers every tracked file the glob matches, <c>src/limits/Helper.cs</c> included, and that file is
    /// in no diff this fixture produces. A run that fingerprinted the changed-file list instead of
    /// <c>git ls-files</c> would compute a value over <c>Limits.cs</c> alone, no page would ever equal
    /// its seal, and every other fact in this file would still be green — a page staying in the report is
    /// what the feature does when it declines.
    /// </para>
    /// <para>
    /// The json format carries the claim best: "which pages" is an array here rather than a table a
    /// console wrapped, and the same payload holds both halves of the partition.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_page_whose_sources_still_match_its_seal_is_reported_sealed_rather_than_drifted()
    {
        var work = Seeded(nameof(A_page_whose_sources_still_match_its_seal_is_reported_sealed_rather_than_drifted));

        Seal(work, LimitsPath);

        var run = Invoke(work, "drift", "--format", "json");

        run.Code.ShouldBe(0, run.Diagnostics);

        using var document = JsonDocument.Parse(run.Output);
        var root = document.RootElement;

        var held = root.GetProperty("sealed");

        var because = "The page whose sources are byte-identical to its seal was not held out."
            + Environment.NewLine + run.Diagnostics;

        held.GetArrayLength().ShouldBe(1, because);

        held[0].GetProperty("path").GetString().ShouldBe(LimitsPath, run.Diagnostics);
        held[0].GetProperty("title").GetString().ShouldBe(LimitsTitle, run.Diagnostics);
        held[0].GetProperty("sealedAt").GetString().ShouldBe(SealedOn, run.Diagnostics);

        // SC3: the unsealed page is untouched, and it is the only page left in the verdict.
        var pages = root.GetProperty("pages");

        pages.GetArrayLength().ShouldBe(1, run.Diagnostics);
        pages[0].GetProperty("path").GetString().ShouldBe(RatesPath, run.Diagnostics);

        root.GetProperty("affectedCount").GetInt32().ShouldBe(1, run.Diagnostics);
        root.GetProperty("hasDrift").GetBoolean().ShouldBeTrue(run.Diagnostics);
    }

    /// <summary>
    /// SC7 through the table and the exit code: with every drifted page sealed there is no drift left to
    /// fail on, so <c>--fail-on-drift</c> exits 0 — and the block that says why renders, page by page
    /// with the date each seal was taken. A quiet verdict a machine narrowed must say so, the way the two
    /// declared exemptions do (§6.4).
    /// </summary>
    [Fact]
    public void Every_drifted_page_being_sealed_exits_zero_under_fail_on_drift_and_discloses_why()
    {
        var work = Seeded(nameof(Every_drifted_page_being_sealed_exits_zero_under_fail_on_drift_and_discloses_why));

        Seal(work, LimitsPath, RatesPath);

        var run = Invoke(work, "drift", "--fail-on-drift");

        run.Code.ShouldBe(0, run.Diagnostics);

        // Neither page is reported, so the verdict is the no-drift one that produced the exit 0 above —
        // and it says why it is quiet rather than claiming nothing was touched, because the SEALED block
        // three lines below names two pages whose sources this range did touch.
        run.Flowed.ShouldContain("Nothing left to review after the seal.", customMessage: run.Diagnostics);

        var contradicted = "The verdict claims nothing was touched directly above a block listing the "
            + $"pages whose sources were.{Environment.NewLine}{run.Diagnostics}";

        run.Flowed.ShouldNotContain("No documented sources touched.", customMessage: contradicted);

        // De-wrapped before matching, the way the IGNORED COMMITS fact above reads its line.
        var unwrapped = string.Concat(run.Flowed.Where(character => !char.IsWhiteSpace(character)));

        unwrapped.ShouldContain(
            "SEALED—2page(s)whosesourcesarebyte-identicaltotheirseal",
            customMessage: run.Diagnostics);

        run.Flowed.ShouldContain(
            $"{LimitsPath} ({LimitsTitle} — sealed {SealedOn})",
            customMessage: run.Diagnostics);

        run.Flowed.ShouldContain(
            $"{RatesPath} ({RatesTitle} — sealed {SealedOn})",
            customMessage: run.Diagnostics);
    }

    /// <summary>
    /// SC7 through the format a reviewer actually reads. The PR comment is where a disclosure the other
    /// formats carry has gone missing before, and it is the format where losing one matters most: the
    /// comment is the whole of what a reviewer sees, so a page held out of it silently is a page nobody
    /// can find out was held out.
    /// </summary>
    [Fact]
    public void The_pr_comment_carries_the_seal_disclosure_the_other_formats_do()
    {
        var work = Seeded(nameof(The_pr_comment_carries_the_seal_disclosure_the_other_formats_do));

        Seal(work, LimitsPath, RatesPath);

        var run = Invoke(work, "drift", "--format", "github-comment");

        run.Code.ShouldBe(0, run.Diagnostics);

        run.Output.ShouldContain(
            "This PR touches documented sources, but all 2 pages they belong to are byte-identical",
            customMessage: run.Diagnostics);

        var contradicted = "The comment tells a reviewer nothing was touched and then lists the pages "
            + $"whose sources were.{Environment.NewLine}{run.Diagnostics}";

        run.Output.ShouldNotContain("No documented sources were touched.", customMessage: contradicted);

        run.Output.ShouldContain(
            "2 flagged pages were held out by their seal",
            customMessage: run.Diagnostics);

        run.Output.ShouldContain(
            $"- **{LimitsTitle}** — `{LimitsPath}` (sealed {SealedOn})",
            customMessage: run.Diagnostics);

        run.Output.ShouldContain(
            $"- **{RatesTitle}** — `{RatesPath}` (sealed {SealedOn})",
            customMessage: run.Diagnostics);

        // The provenance line, which is what a reader takes in without scrolling.
        run.Output.ShouldContain("2 sealed.", customMessage: run.Diagnostics);
    }

    /// <summary>
    /// SC8 end to end: <c>--mark</c> never labels a sealed page. Asserted as the label writes that went
    /// out, not as a plan — the planner's half is pinned in
    /// <see cref="Drift.DriftMarkPlannerTests.A_page_its_seal_held_out_is_never_labelled"/>, and what only
    /// the process can answer is whether the seal check runs before the write half sees the report at all.
    /// </summary>
    [Fact]
    public void A_mark_run_never_labels_a_page_its_seal_held_out()
    {
        var work = Seeded(nameof(A_mark_run_never_labels_a_page_its_seal_held_out));

        Seal(work, LimitsPath);

        StubLabels();
        StubDashboard();

        var run = Invoke(work, "drift", "--mark");

        run.Code.ShouldBe(0, run.Diagnostics);

        LabelWrites().ShouldBe(
            [RatesPageId],
            $"A sealed page was labelled stale.{Environment.NewLine}{run.Diagnostics}");

        var state = State(work);

        state.Pages[LimitsPath].Stale.ShouldBeFalse(run.Diagnostics);
        state.Pages[RatesPath].Stale.ShouldBeTrue(run.Diagnostics);

        // The skip is visible above the plan rather than being a page that quietly vanished (spec §3.4).
        run.FlowedAll.ShouldContain("SEALED", customMessage: run.Diagnostics);
    }

    /// <summary>
    /// SC2: a page whose sources really moved after the seal was taken is reported exactly as it is
    /// today. The seal is about bytes and not about commits, which is the whole claim — so the page whose
    /// file changed once more drifts, and its neighbour, sealed at the same moment and untouched since,
    /// does not.
    /// </summary>
    [Fact]
    public void A_page_whose_sources_moved_after_the_seal_still_drifts()
    {
        var work = Seeded(nameof(A_page_whose_sources_moved_after_the_seal_still_drifts));

        Seal(work, LimitsPath, RatesPath);

        Write(work, "src/limits/Limits.cs", "// the limits, rewritten once more, after the seal\n");
        Commit(work, "a change the seal predates");

        var run = Invoke(work, "drift", "--format", "json");

        run.Code.ShouldBe(0, run.Diagnostics);

        using var document = JsonDocument.Parse(run.Output);
        var root = document.RootElement;

        var pages = root.GetProperty("pages");

        pages.GetArrayLength().ShouldBe(1, run.Diagnostics);
        pages[0].GetProperty("path").GetString().ShouldBe(LimitsPath, run.Diagnostics);

        var held = root.GetProperty("sealed");

        held.GetArrayLength().ShouldBe(1, run.Diagnostics);
        held[0].GetProperty("path").GetString().ShouldBe(RatesPath, run.Diagnostics);
    }

    /// <summary>
    /// SC9: a page whose sources cannot be read now is never sealed silently. Its files are tracked and
    /// gone from the working tree, which is what a deleted directory or a partial checkout produces, and
    /// a fingerprint over whatever happened to be readable would suppress the one report nobody could
    /// have verified. It stays in the verdict; the page whose sources are still there is still sealed.
    /// </summary>
    [Fact]
    public void A_page_whose_sources_cannot_be_read_stays_in_the_verdict()
    {
        var work = Seeded(nameof(A_page_whose_sources_cannot_be_read_stays_in_the_verdict));

        Seal(work, LimitsPath, RatesPath);

        Directory.Delete(Path.Combine(work, "src", "limits"), recursive: true);

        var run = Invoke(work, "drift", "--format", "json");

        run.Code.ShouldBe(0, run.Diagnostics);

        using var document = JsonDocument.Parse(run.Output);
        var root = document.RootElement;

        var pages = root.GetProperty("pages");

        pages.GetArrayLength().ShouldBe(
            1,
            $"An unreadable source tree suppressed a drift report.{Environment.NewLine}{run.Diagnostics}");

        pages[0].GetProperty("path").GetString().ShouldBe(LimitsPath, run.Diagnostics);

        var held = root.GetProperty("sealed");

        held.GetArrayLength().ShouldBe(1, run.Diagnostics);
        held[0].GetProperty("path").GetString().ShouldBe(RatesPath, run.Diagnostics);
    }

    /// <summary>
    /// SC4 and SC6 through the format a reviewer actually reads: the affected pages sit under their
    /// owner, and each owner reaches the comment exactly as its page's frontmatter spells it. This is the
    /// layer where the feature either works or does not — a handle that survives the renderer but not the
    /// parse would still notify nobody.
    /// </summary>
    [Fact]
    public void The_pr_comment_groups_the_affected_pages_under_their_owner()
    {
        var work = Seeded(nameof(The_pr_comment_groups_the_affected_pages_under_their_owner));

        Own(work, LimitsPath, LimitsOwner);
        Own(work, RatesPath, RatesOwner);

        var run = Invoke(work, "drift", "--format", "github-comment");

        run.Code.ShouldBe(0, run.Diagnostics);

        run.Output.ShouldContain($"**Owner:** {LimitsOwner}", customMessage: run.Diagnostics);
        run.Output.ShouldContain($"**Owner:** {RatesOwner}", customMessage: run.Diagnostics);

        // Each page under the heading that names it, ordinal by owner — '@' sorts below 'A', so the
        // team handle's group comes first however the pages arrived.
        var lending = run.Output.IndexOf($"**Owner:** {LimitsOwner}", StringComparison.Ordinal);
        var alice = run.Output.IndexOf($"**Owner:** {RatesOwner}", StringComparison.Ordinal);

        lending.ShouldBeLessThan(alice, run.Diagnostics);
        run.Output.IndexOf($"`{LimitsPath}`", StringComparison.Ordinal).ShouldBeInRange(lending, alice);
        run.Output.IndexOf($"`{RatesPath}`", StringComparison.Ordinal).ShouldBeGreaterThan(alice);

        // Verbatim: no `@` bolted onto the display name, and nothing said about an owner nobody lacks.
        run.Output.ShouldNotContain($"@{RatesOwner}", customMessage: run.Diagnostics);
        run.Output.ShouldNotContain("No owner", customMessage: run.Diagnostics);
    }

    /// <summary>
    /// SC7: the verdict line says how many affected pages this report cannot route. A count rather than
    /// a flag, because the proportion is the fact — "2 of 2 unowned" and "1 of 40" raise the same boolean
    /// and are not the same problem.
    /// </summary>
    [Fact]
    public void The_verdict_line_says_how_many_affected_pages_have_no_owner()
    {
        var work = Seeded(nameof(The_verdict_line_says_how_many_affected_pages_have_no_owner));

        // The seeded pages declare no owner at all, which is every repo on the day this ships.
        var run = Invoke(work, "drift");

        run.Code.ShouldBe(0, run.Diagnostics);
        run.Flowed.ShouldContain("2 of 2 page(s) with declared sources may need review.", customMessage: run.Diagnostics);
        run.Flowed.ShouldContain("2 page(s) carry no 'owner:'", customMessage: run.Diagnostics);

        // And the disclosure is not boilerplate: with both pages owned it has nothing to report.
        Own(work, LimitsPath, LimitsOwner);
        Own(work, RatesPath, RatesOwner);

        var owned = Invoke(work, "drift");

        owned.Code.ShouldBe(0, owned.Diagnostics);
        owned.Flowed.ShouldNotContain("carry no 'owner:'", customMessage: owned.Diagnostics);
    }

    /// <summary>
    /// SC8: <c>--format json</c> carries the owner per page and the unowned count, so a CI step that
    /// routes drift itself reads the same two facts the comment renders rather than parsing the comment.
    /// A page with no owner carries no <c>owner</c> key at all — <c>DocumeJson.Options</c> drops nulls,
    /// and an empty string would be a second spelling of "unowned".
    /// </summary>
    [Fact]
    public void A_json_report_carries_each_pages_owner_and_the_unowned_count()
    {
        var work = Seeded(nameof(A_json_report_carries_each_pages_owner_and_the_unowned_count));

        Own(work, LimitsPath, LimitsOwner);

        var run = Invoke(work, "drift", "--format", "json");

        run.Code.ShouldBe(0, run.Diagnostics);

        using var document = JsonDocument.Parse(run.Output);
        var root = document.RootElement;

        root.GetProperty("affectedCount").GetInt32().ShouldBe(2, run.Diagnostics);
        root.GetProperty("unownedCount").GetInt32().ShouldBe(1, run.Diagnostics);

        var pages = root.GetProperty("pages");

        pages[0].GetProperty("path").GetString().ShouldBe(LimitsPath, run.Diagnostics);
        pages[0].GetProperty("owner").GetString().ShouldBe(LimitsOwner, run.Diagnostics);

        pages[1].GetProperty("path").GetString().ShouldBe(RatesPath, run.Diagnostics);
        pages[1].TryGetProperty("owner", out _).ShouldBeFalse(run.Diagnostics);
    }

    /// <summary>
    /// SC10, end to end: a page its own seal held out is never routed to an owner. It falls out of the
    /// design — routing consumes <c>Pages</c> and a sealed page left that list before the renderer saw
    /// it — and is asserted anyway, because "it falls out" is exactly the kind of claim that quietly
    /// stops being true. Both pages carry an owner here, so the sealed one's absence is a decision rather
    /// than a page that had nothing to say.
    /// </summary>
    /// <remarks>
    /// The second half is SC7 held against SC10, which is the one interaction
    /// <see cref="DocuMe.Core.Drift.DriftReport.UnownedCount"/> exists to survive: a page that is both
    /// sealed and unowned must be counted by neither. Nothing else pins it —
    /// <see cref="The_verdict_line_says_how_many_affected_pages_have_no_owner"/> seals nothing and the
    /// first half here owns everything — so a refactor that stamped the count in <c>DriftPlanner.Plan</c>,
    /// before <c>SealedVerdicts.Apply</c> rewrites <c>Pages</c>, would go green while the verdict line
    /// disclosed an unowned page the report above it does not list.
    /// </remarks>
    [Fact]
    public void A_sealed_page_is_never_routed_to_its_owner()
    {
        var work = Seeded(nameof(A_sealed_page_is_never_routed_to_its_owner));

        Own(work, LimitsPath, LimitsOwner);
        Own(work, RatesPath, RatesOwner);
        Seal(work, LimitsPath);

        var run = Invoke(work, "drift", "--format", "github-comment");

        run.Code.ShouldBe(0, run.Diagnostics);

        var routed = $"A sealed page was routed to its owner.{Environment.NewLine}{run.Diagnostics}";

        run.Output.ShouldNotContain($"**Owner:** {LimitsOwner}", customMessage: routed);
        run.Output.ShouldContain($"**Owner:** {RatesOwner}", customMessage: run.Diagnostics);

        // Held out, not hidden: the page is still disclosed, under the seal that held it out.
        run.Output.ShouldContain(
            $"- **{LimitsTitle}** — `{LimitsPath}` (sealed {SealedOn})",
            customMessage: run.Diagnostics);

        // And it is counted out of the verdict the groups sit under.
        run.Output.ShouldContain(
            "This PR touches sources for **1 wiki page** of 2 with declared sources:",
            customMessage: run.Diagnostics);

        // Counted out of the other verdict too. With its `owner:` taken away the sealed page is the only
        // owner-less page in the wiki, and the disclosure has to stay silent: it speaks for the affected
        // pages this report cannot route, and a page held out of the report is not one of them.
        Unown(work, LimitsPath);

        var verdict = Invoke(work, "drift");

        verdict.Code.ShouldBe(0, verdict.Diagnostics);
        verdict.Flowed.ShouldContain(
            "1 of 2 page(s) with declared sources may need review.",
            customMessage: verdict.Diagnostics);

        verdict.Flowed.ShouldNotContain(
            "carry no 'owner:'",
            customMessage: $"A sealed page was counted as unowned.{Environment.NewLine}{verdict.Diagnostics}");
    }

    /// <summary>
    /// The per-page Stale cell as <see cref="DocuMe.Core.Dashboard.DashboardPage"/> renders it. Lowercase, which is
    /// what keeps it distinct from <see cref="SummaryStaleCell"/> under an ordinal count.
    /// </summary>
    private const string PageStaleCell = "<td>⚠️ stale</td>";

    /// <summary>The Coverage table's Stale row label, whose next cell is the count.</summary>
    private const string SummaryStaleCell = "<td>⚠️ Stale</td>";

    private static string StatePath(string work) =>
        Path.Combine(work, "docs", "wiki", "_meta", "state.json");

    /// <summary>How many times <paramref name="needle"/> appears in <paramref name="haystack"/>.</summary>
    /// <remarks>
    /// A count rather than a <c>ShouldContain</c>: the claims here are about how many pages the page says
    /// are stale, and a containment check answers that question with a yes for any number above zero.
    /// </remarks>
    private static int Occurrences(string haystack, string needle)
    {
        var found = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            found++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return found;
    }

    /// <summary>
    /// The one table row naming <paramref name="title"/>, so an assertion can say which page carries a
    /// marker rather than only that some page does.
    /// </summary>
    private static string Row(string body, string title)
    {
        var rows = body
            .Split("<tr>", StringSplitOptions.None)
            .Where(row => row.Contains(title, StringComparison.Ordinal))
            .ToList();

        var because = $"'{title}' names {rows.Count} row(s) in the dashboard, so a row assertion would be "
            + "ambiguous.";

        rows.Count.ShouldBe(1, because);

        return rows[0];
    }

    private static DocumeState State(string work) => StateStore.Load(StatePath(work));

    private static CliRun Invoke(string workingDirectory, params string[] args) =>
        DocumeCli.Invoke(workingDirectory, args);

    /// <summary>
    /// Writes the exemption list a consumer repo may keep at <c>&lt;wiki.root&gt;/_meta/drift-ignore</c>
    /// (§6.4), one entry per line. Not committed on purpose: the command reads the file from disk the
    /// way it reads state, so the diff between the two revisions is not what finds it.
    /// </summary>
    private static void ExemptSources(string work, params string[] lines) =>
        File.WriteAllLines(Path.Combine(work, "docs", "wiki", "_meta", "drift-ignore"), lines);

    /// <summary>
    /// Writes the commit list a consumer repo may keep at
    /// <c>&lt;wiki.root&gt;/_meta/drift-ignore-revs</c> (§6.4), one sha per line. Not committed,
    /// like <see cref="ExemptSources"/>: the command reads the file from disk the way it reads
    /// state, so the diff between the two revisions is not what finds it.
    /// </summary>
    private static void ExemptCommits(string work, params string[] lines) =>
        File.WriteAllLines(Path.Combine(work, "docs", "wiki", "_meta", "drift-ignore-revs"), lines);

    /// <summary>
    /// Seals the named pages in the seeded state, as the publish that generated their live bodies would
    /// have (<c>PublishExecutor.SealSources</c>): the fingerprint of the files each page's <c>sources</c>
    /// globs match, taken over git's tracked files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The candidate list is <c>git ls-files</c> because that is the universe the seal and the check have
    /// to share (spec §3.1 as amended, §4b defect F). It is read here with git itself rather than through
    /// <c>GitRepository</c>, so the fixture states the fact rather than borrowing the production
    /// answer for both sides of its own comparison.
    /// </para>
    /// <para>
    /// A real <c>docume publish</c> writing this is covered by <see cref="CliPublishTests"/> (SC11);
    /// earning it again here would cost a stub set and a second process run to reach the same state file.
    /// </para>
    /// </remarks>
    private static void Seal(string work, params string[] paths)
    {
        var tracked = Git(work, "ls-files")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var sha = Git(work, "rev-parse", "HEAD").Trim();
        var statePath = StatePath(work);
        var state = StateStore.Load(statePath);
        var pages = new Dictionary<string, PageState>(state.Pages, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            pages[path] = pages[path] with
            {
                Verdict = new SealedVerdict
                {
                    SourcesHash = DocuMe.Core.Drift.SourcesFingerprint.Compute(work, [Glob(path)], tracked),
                    SealedAt = SealedOn,
                    RepoSha = sha,
                },
            };
        }

        StateStore.Save(statePath, state with { Pages = pages });
    }

    /// <summary>The <c>sources</c> glob the seeded page at <paramref name="path"/> declares.</summary>
    private static string Glob(string path) => path switch
    {
        LimitsPath => LimitsGlob,
        RatesPath => RatesGlob,
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "The fixture seeds two pages."),
    };

    /// <summary>The title the seeded page at <paramref name="path"/> carries.</summary>
    private static string Title(string path) => path switch
    {
        LimitsPath => LimitsTitle,
        RatesPath => RatesTitle,
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "The fixture seeds two pages."),
    };

    /// <summary>
    /// Rewrites a seeded page with an <c>owner:</c> in its frontmatter (§5.2), as a consumer repo would.
    /// </summary>
    /// <remarks>
    /// Deliberately not committed, and it does not need to be: the drift range is a diff between two
    /// revisions, so an uncommitted page edit changes nothing about which files count as changed, while
    /// the frontmatter itself is read off the working tree the way <c>docume</c> reads every page.
    /// </remarks>
    private static void Own(string work, string path, string owner) =>
        Write(work, $"docs/wiki/{path}", Page(Title(path), Glob(path), owner));

    /// <summary>Rewrites a seeded page without an <c>owner:</c>, undoing <see cref="Own"/>.</summary>
    private static void Unown(string work, string path) =>
        Write(work, $"docs/wiki/{path}", Page(Title(path), Glob(path)));

    /// <summary>Flags one page stale in the seeded state, as a previous <c>--mark</c> would have left it.</summary>
    private static void MarkStale(string work, string path)
    {
        var statePath = StatePath(work);

        StateStore.Save(statePath, StateUpdates.SetStale(StateStore.Load(statePath), path, stale: true));
    }

    /// <summary>
    /// Adds this suite's space to <c>confluence.protectedSpaces</c> in the scaffolded config, which is how
    /// rule §1.4's lock is expressed in a consumer repo (§9.5: the space key belongs in config, not in the
    /// tool).
    /// </summary>
    private static void Protect(string work) =>
        Reconfigure(work, config =>
            config["confluence"]!["protectedSpaces"] = new JsonArray(JsonValue.Create(SpaceKey)));

    /// <summary>Renames <c>labels.stale</c> in the scaffolded config, as a consumer repo may (§9.5).</summary>
    private static void Relabel(string work, string name) =>
        Reconfigure(work, config =>
        {
            config["labels"] ??= new JsonObject();
            config["labels"]!["stale"] = JsonValue.Create(name);
        });

    private static void Reconfigure(string work, Action<JsonNode> edit)
    {
        var path = Path.Combine(work, "docume.json");
        var config = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path} parsed as null.");

        edit(config);

        File.WriteAllText(path, config.ToJsonString());
    }

    /// <summary>
    /// One wiki page declaring the glob that makes it drift when its source moves (§5.2), and optionally
    /// the owner that drift routes it to. The owner is written quoted, because a handle opening with
    /// <c>@</c> is a reserved indicator in a bare YAML scalar.
    /// </summary>
    private static string Page(string title, string glob, string? owner = null) => $"""
        ---
        sources:
          - {glob}{(owner is null ? string.Empty : $"\nowner: \"{owner}\"")}
        ---

        # {title}

        What the code under `{glob}` does.

        """;

    private static void Write(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Commits everything in the tree and answers the new commit's sha.</summary>
    private static string Commit(string work, string message)
    {
        Git(work, "add", "-A");
        Git(work, "commit", "-q", "-m", message);

        return Git(work, "rev-parse", "HEAD").Trim();
    }

    /// <summary>
    /// git in <paramref name="work"/>, with identity and signing from flags so a developer's global
    /// config cannot change the outcome.
    /// </summary>
    private static string Git(string work, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(work);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo).ShouldNotBeNull();

        // stderr drained concurrently with stdout: a sequential double read deadlocks once the
        // child fills the unread pipe (see ReleaseWorkflowTests.GitResult).
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var error = errorTask.GetAwaiter().GetResult();

        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)} failed: {error}{output}");

        return output;
    }

    private static JsonElement Payload(IRequestMessage request)
    {
        request.Body.ShouldNotBeNull();

        using var document = JsonDocument.Parse(request.Body!);

        return document.RootElement.Clone();
    }

    private static IResponseBuilder Json(string body) =>
        Response.Create()
            .WithStatusCode(HttpStatusCode.OK)
            .WithHeader("Content-Type", "application/json")
            .WithBody(body);

    /// <summary>
    /// A consumer repo as `docume init` leaves it, pointed at this test's own server, inside a git
    /// repository whose second commit moves the code two of its pages declare as sources — and with both
    /// of those pages recorded in state as published.
    /// </summary>
    private string Seeded(string name)
    {
        var work = Path.Combine(_root, name);
        Directory.CreateDirectory(work);

        var run = Invoke(work, "init", "--space", SpaceKey, "--base-url", $"{_server.Url}/wiki");

        run.Code.ShouldBe(0, $"The fixture's own `docume init` failed.{Environment.NewLine}{run.Diagnostics}");

        Write(work, $"docs/wiki/{LimitsPath}", Page(LimitsTitle, LimitsGlob));
        Write(work, $"docs/wiki/{RatesPath}", Page(RatesTitle, RatesGlob));
        Write(work, "src/limits/Limits.cs", "// the limits, as first written\n");
        Write(work, "src/rates/Rates.cs", "// the rates, as first written\n");

        // Tracked, matched by the Limits page's glob, and never touched again — which is the one thing
        // that makes the seal facts below able to fail. A fingerprint over `git ls-files` covers this
        // file; a fingerprint over the diff between the two commits cannot, so a drift run that
        // recomputed against its own changed-file list would produce a value no seal can ever equal and
        // every page would silently stay unsealed (spec §3.1 as amended).
        Write(work, "src/limits/Helper.cs", "// a helper nothing in this fixture ever edits\n");

        Git(work, "init", "-q", "-b", "main");
        Git(work, "config", "user.email", "loop@example.com");
        Git(work, "config", "user.name", "DocuMe loop");
        Git(work, "config", "commit.gpgsign", "false");

        var baseline = Commit(work, "the wiki and the code it describes");

        Write(work, "src/limits/Limits.cs", "// the limits, rewritten\n");
        Write(work, "src/rates/Rates.cs", "// the rates, rewritten\n");

        Commit(work, "move the code out from under both pages");

        var statePath = StatePath(work);

        StateStore.Save(statePath, StateStore.Load(statePath) with
        {
            BaselineSha = baseline,
            Pages = new Dictionary<string, PageState>(StringComparer.Ordinal)
            {
                [LimitsPath] = new() { PageId = LimitsPageId, Title = LimitsTitle, PublishedVersion = 1 },
                [RatesPath] = new() { PageId = RatesPageId, Title = RatesTitle, PublishedVersion = 1 },
            },
        });

        return work;
    }

    /// <summary>Every request the fake Confluence was sent, in order.</summary>
    private List<IRequestMessage> Seen() =>
        _server.LogEntries
            .Select(entry => entry.RequestMessage)
            .OfType<IRequestMessage>()
            .ToList();

    /// <summary>The requests that changed something.</summary>
    private List<IRequestMessage> Writes() =>
        Seen()
            .Where(request => request.Method is "POST" or "PUT" or "DELETE")
            .ToList();

    private List<IRequestMessage> Requests(string method, string path) =>
        Seen()
            .Where(request => string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Path, path, StringComparison.Ordinal))
            .ToList();

    /// <summary>The page ids this run added a label to, in the order it wrote them.</summary>
    private List<string> LabelWrites() =>
        Writes()
            .Where(request => request.Path.EndsWith("/label", StringComparison.Ordinal))
            .Select(request => request.Path.Split('/')[^2])
            .ToList();

    /// <summary>The storage-format body of the last page write to <paramref name="path"/>.</summary>
    private string Body(string method, string path)
    {
        var stored = Payload(method, path)
            .GetProperty("body")
            .GetProperty("storage")
            .GetProperty("value")
            .GetString();

        stored.ShouldNotBeNull($"{method} {path} carried no storage body.");

        return stored!;
    }

    private JsonElement Payload(string method, string path)
    {
        var request = Requests(method, path).LastOrDefault();

        request.ShouldNotBeNull($"No {method} {path} was sent.");

        return Payload(request!);
    }

    /// <summary>Both pages' label endpoints, answering the way Confluence answers an added label.</summary>
    private void StubLabels()
    {
        StubLabel(LimitsPageId);
        StubLabel(RatesPageId);
    }

    private void StubLabel(string pageId) =>
        _server
            .Given(Request.Create().WithPath($"/wiki/rest/api/content/{pageId}/label").UsingPost())
            .RespondWith(Json($$"""
                {
                  "results": [
                    { "prefix": "global", "name": "{{StaleLabel}}", "id": "10001", "label": "{{StaleLabel}}" }
                  ],
                  "start": 0, "limit": 200, "size": 1,
                  "_links": {}
                }
                """));

    /// <summary>
    /// What the dashboard refresh reads and writes: the space, the label state, the title lookup that
    /// finds nothing, and the create that follows from it.
    /// </summary>
    private void StubDashboard()
    {
        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/spaces").UsingGet())
            .RespondWith(Json($$"""
                {
                  "results": [{ "id": "{{SpaceId}}", "key": "{{SpaceKey}}", "name": "DocuMe Sandbox" }],
                  "_links": {}
                }
                """));

        _server
            .Given(Request.Create().WithPath("/wiki/rest/api/content/search").UsingGet())
            .RespondWith(Json("""
                { "results": [], "start": 0, "limit": 50, "size": 0, "_links": {} }
                """));

        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingGet())
            .RespondWith(Json("""{ "results": [], "_links": {} }"""));

        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingPost())
            .RespondWith(Json($$"""
                {
                  "id": "{{DashboardPageId}}",
                  "status": "current",
                  "title": {{DashboardTitleJson}},
                  "spaceId": "{{SpaceId}}",
                  "version": { "number": 1 }
                }
                """));
    }

    /// <summary>
    /// The same four stubs as <see cref="StubDashboard"/> with the title lookup answering instead of
    /// finding nothing, so the refresh takes its update branch: the space, the label state, the dashboard
    /// carrying <paramref name="stored"/> at <see cref="DashboardVersion"/>, and the PUT that follows.
    /// </summary>
    /// <param name="stored">
    /// The body Confluence holds. Compared above the provenance line, so anything unlike a current render
    /// makes the refresh spend a version rather than skip as unchanged.
    /// </param>
    private void StubExistingDashboard(string stored)
    {
        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/spaces").UsingGet())
            .RespondWith(Json($$"""
                {
                  "results": [{ "id": "{{SpaceId}}", "key": "{{SpaceKey}}", "name": "DocuMe Sandbox" }],
                  "_links": {}
                }
                """));

        _server
            .Given(Request.Create().WithPath("/wiki/rest/api/content/search").UsingGet())
            .RespondWith(Json("""
                { "results": [], "start": 0, "limit": 50, "size": 0, "_links": {} }
                """));

        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingGet())
            .RespondWith(Json($$"""
                {
                  "results": [
                    {
                      "id": "{{DashboardPageId}}",
                      "status": "current",
                      "title": {{DashboardTitleJson}},
                      "spaceId": "{{SpaceId}}",
                      "version": { "number": {{DashboardVersion}} },
                      "body": {
                        "storage": {
                          "value": {{JsonSerializer.Serialize(stored)}},
                          "representation": "storage"
                        }
                      }
                    }
                  ],
                  "_links": {}
                }
                """));

        _server
            .Given(Request.Create().WithPath($"/wiki/api/v2/pages/{DashboardPageId}").UsingPut())
            .RespondWith(Json($$"""
                {
                  "id": "{{DashboardPageId}}",
                  "status": "current",
                  "title": {{DashboardTitleJson}},
                  "spaceId": "{{SpaceId}}",
                  "version": { "number": {{DashboardVersion + 1}} }
                }
                """));
    }

    /// <summary>The dashboard title as a JSON string literal, so the stub body cannot be malformed.</summary>
    private static string DashboardTitleJson => JsonSerializer.Serialize(DashboardTitle);
}
