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

    private static string StatePath(string work) =>
        Path.Combine(work, "docs", "wiki", "_meta", "state.json");

    private static DocumeState State(string work) => StateStore.Load(StatePath(work));

    private static CliRun Invoke(string workingDirectory, params string[] args) =>
        DocumeCli.Invoke(workingDirectory, args);

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

    /// <summary>One wiki page declaring the glob that makes it drift when its source moves (§5.2).</summary>
    private static string Page(string title, string glob) => $"""
        ---
        sources:
          - {glob}
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
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

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

        Write(work, $"docs/wiki/{LimitsPath}", Page(LimitsTitle, "src/limits/*.cs"));
        Write(work, $"docs/wiki/{RatesPath}", Page(RatesTitle, "src/rates/*.cs"));
        Write(work, "src/limits/Limits.cs", "// the limits, as first written\n");
        Write(work, "src/rates/Rates.cs", "// the rates, as first written\n");

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
