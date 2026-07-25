using System.Xml.Linq;
using DocuMe.Core.Dashboard;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using DocuMe.Core.Status;
using Shouldly;

namespace DocuMe.Core.Tests.Dashboard;

/// <summary>
/// The dashboard's contract (PLAN.md §6.5). Three things are pinned here and each is load-bearing
/// somewhere else: the markup is well-formed and every consumer string escaped, because Confluence
/// rejects a body outright rather than partially; the render is deterministic, because
/// <c>docume dashboard</c> compares the rendered body against the live one to avoid spending a page
/// version per cron run; and the markers say what the legend says they say, because a status page whose
/// legend has drifted from its rows is worse than no status page.
/// </summary>
public sealed class DashboardPageTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 7, 25, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Render_IsWellFormedXml()
    {
        // A string comparison against a hand-written expectation would happily accept an unbalanced
        // tag on both sides; Confluence would reject the whole body at upload time instead.
        Should.NotThrow(() => XDocument.Parse(Wrap(Page(Report()).Render())));
    }

    [Fact]
    public void Render_OpensWithTheMachineOwnedWarning()
    {
        var body = Page(Report()).Render();

        body.ShouldStartWith("<ac:structured-macro ac:name=\"warning\">");
        body.ShouldContain("rewritten in full on every run");
        body.ShouldContain("the wiki source lives in the repository");
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        // What the upsert's skip-if-unchanged rests on: two renders of the same report must be the
        // same bytes, or every run would look like a change and bump the page version forever.
        var report = Report();

        Page(report).Render().ShouldBe(Page(report).Render());
    }

    [Fact]
    public void Render_CountsCoverageFromTheReport()
    {
        var body = Page(Report()).Render();

        // 1 approved of 4 pages = 25%, and the three approval states stay separate: "no record" is
        // not "needs review" (StatusReport.UnrecordedApprovalCount).
        body.ShouldContain("<td>1 of 4 (25%)</td>");
        body.ShouldContain($"<td>{DashboardPage.AttentionMarker} Needs review</td>\n<td>1</td>");
        body.ShouldContain($"<td>{DashboardPage.NoneMarker} No review recorded</td>\n<td>2</td>");
        body.ShouldContain("<td>Published</td>\n<td>3 of 4</td>");
    }

    [Fact]
    public void Render_MarksAnApprovedPageApproved()
    {
        var body = Page(Report()).Render();

        body.ShouldContain($"<td>{DashboardPage.ApprovedMarker} approved</td>");
        body.ShouldContain("<td>alice@example.com</td>");
        body.ShouldContain("<td>2026-07-20T08:00:00Z</td>");
    }

    [Fact]
    public void Render_FlagsAnApprovalTheWikiHasMovedPast()
    {
        // §8 has publish invalidate approval on a content change, so the two versions only diverge
        // when something else moved the page — a browser edit, a machine write. Rendering that as a
        // plain "approved" would launder it.
        var page = ApprovedPage() with { ApprovedVersion = 3, PublishedVersion = 5 };
        var rows = PagesSection(Page(Report([page])).Render());

        rows.ShouldContain($"<td>{DashboardPage.AttentionMarker} approved at v3, page is at v5</td>");
        rows.ShouldNotContain($"<td>{DashboardPage.ApprovedMarker} approved</td>");
    }

    [Fact]
    public void Render_DistinguishesNeedsReviewFromNoRecordAtAll()
    {
        var body = Page(Report()).Render();

        body.ShouldContain($"<td>{DashboardPage.AttentionMarker} needs-review</td>");
        body.ShouldContain($"<td>{DashboardPage.NoneMarker} no review recorded</td>");
    }

    [Fact]
    public void Render_LinksAPublishedPageByTitleAndLeavesAnUnpublishedOneAsText()
    {
        var body = Page(Report()).Render();

        // ri:page, not the page URL: Confluence rewrites its own page links when a page is renamed.
        body.ShouldContain(
            "<td><ac:link><ri:page ri:content-title=\"Getting started\"/>"
            + "<ac:link-body>Getting started</ac:link-body></ac:link></td>");

        // The unpublished page has no page to link to; an ri:page to a title Confluence does not hold
        // renders as a broken link on a status page, which is a poor way to say "not published".
        body.ShouldContain("<td>Draft page</td>");
        body.ShouldNotContain("ri:content-title=\"Draft page\"");
    }

    [Fact]
    public void Render_EscapesEveryConsumerString()
    {
        // Titles come back from Confluence (untrusted, CLAUDE.md §0.2) and approver names come out of a
        // committed file a human can hand-edit. Both reach the markup, in text and in an attribute.
        var hostile = ApprovedPage() with
        {
            Title = "R&D <script> \"quoted\"",
            ApprovedBy = "a & b <c>",
        };

        var body = Page(Report([hostile])).Render();

        body.ShouldContain("ri:content-title=\"R&amp;D &lt;script&gt; &quot;quoted&quot;\"");
        body.ShouldContain("<ac:link-body>R&amp;D &lt;script&gt; \"quoted\"</ac:link-body>");
        body.ShouldContain("<td>a &amp; b &lt;c&gt;</td>");
        body.ShouldNotContain("<script>");

        Should.NotThrow(() => XDocument.Parse(Wrap(body)));
    }

    [Fact]
    public void Render_PinsTheLegendToTheMarkersTheRowsUse()
    {
        var body = Page(Report()).Render();

        body.ShouldContain("<h2>Legend</h2>");
        body.ShouldContain($"<td>{DashboardPage.ApprovedMarker}</td>");
        body.ShouldContain($"<td>{DashboardPage.AttentionMarker}</td>");
        body.ShouldContain($"<td>{DashboardPage.NoneMarker}</td>");
        body.ShouldContain("add the `approved` label");
    }

    [Fact]
    public void Render_CarriesTheReportsOwnGapsOntoThePage()
    {
        // §6.5 asks for an open-feedback count and the comment reader is M4. The gap is stated rather
        // than printed as a zero nobody counted — StatusReport.NotYetAvailable is where it comes from.
        var body = Page(Report() with { NotYetAvailable = ["open feedback per page: not built yet"] })
            .Render();

        body.ShouldContain("<h2>Not reported yet</h2>");
        body.ShouldContain("<li>open feedback per page: not built yet</li>");
    }

    [Fact]
    public void Render_WithNoGaps_OmitsTheSection()
    {
        Page(Report() with { NotYetAvailable = [] }).Render().ShouldNotContain("Not reported yet");
    }

    [Fact]
    public void Render_ReportsOrphansBecauseTheyAreStillLivePages()
    {
        var body = Page(Report() with { Orphans = ["gone/removed.md"] }).Render();

        body.ShouldContain("<h2>Orphans</h2>");
        body.ShouldContain("<td>gone/removed.md</td>");
        body.ShouldContain("publish --prune");
    }

    [Fact]
    public void Render_ReportsPagesTheConverterRefuses()
    {
        var failure = new PageConversionFailure("broken.md", "unresolved link to missing.md");
        var body = Page(Report() with { Failures = [failure] }).Render();

        body.ShouldContain("<h2>Pages that cannot publish</h2>");
        body.ShouldContain("<td>broken.md</td>\n<td>unresolved link to missing.md</td>");
    }

    [Fact]
    public void Render_WithNoOrphansOrFailures_OmitsBothSections()
    {
        var body = Page(Report()).Render();

        body.ShouldNotContain("<h2>Orphans</h2>");
        body.ShouldNotContain("<h2>Pages that cannot publish</h2>");
    }

    [Fact]
    public void Render_WithAnEmptyWiki_SaysSoRatherThanShowingAnEmptyTable()
    {
        var body = Page(Report([])).Render();

        body.ShouldContain("No pages.");
        body.ShouldContain("wiki.exclude");
        body.ShouldContain("<td>0 of 0</td>");
    }

    [Fact]
    public void Render_StampsTheProvenanceLineInUtc()
    {
        var body = Page(Report()).Render();

        body.ShouldContain("live labels in DOCUMESBX");
        body.ShouldContain("last changed 2026-07-25 09:30 UTC");
    }

    [Fact]
    public void Render_WithoutAGeneratedAt_OmitsTheTimestamp()
    {
        var body = new DashboardPage { Report = Report() }.Render();

        body.ShouldContain("Generated by DocuMe from the repository state");
        body.ShouldNotContain("last changed");
    }

    [Fact]
    public void WithoutProvenance_IgnoresTheTimestampAndNothingElse()
    {
        // The premise the upsert's skip-if-unchanged rests on: two runs over the same data differ only
        // in the provenance line, so comparing above it spends a page version exactly when data moved.
        var early = Page(Report()).Render();
        var later = new DashboardPage
        {
            Report = Report(),
            GeneratedAt = GeneratedAt.AddHours(6),
        }.Render();

        later.ShouldNotBe(early);
        DashboardPage.WithoutProvenance(later).ShouldBe(DashboardPage.WithoutProvenance(early));

        // And a real change still reads as one.
        var changed = Page(Report([ApprovedPage() with { Stale = true }])).Render();
        DashboardPage.WithoutProvenance(changed).ShouldNotBe(DashboardPage.WithoutProvenance(early));
    }

    [Fact]
    public void WithoutProvenance_WithNoMarker_KeepsTheWholeBody()
    {
        // A page a human rewrote by hand has no marker to split on. Returning it whole makes it compare
        // unequal, so the next run overwrites it — which is what "machine-owned" (§6.5) means.
        const string handWritten = "<p>I edited this page myself</p>";

        DashboardPage.WithoutProvenance(handWritten).ShouldBe(handWritten);
    }

    private static DashboardPage Page(StatusReport report)
        => new() { Report = report, GeneratedAt = GeneratedAt };

    /// <summary>
    /// The per-page table only. The coverage table above it labels its rows with the same markers
    /// ("✅ Approved", 1 of 4), so an assertion about what a page row does <em>not</em> say has to be
    /// scoped or it matches the summary instead.
    /// </summary>
    private static string PagesSection(string body)
    {
        var start = body.IndexOf("<h2>Pages</h2>", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);

        var end = body.IndexOf("<h2>", start + 1, StringComparison.Ordinal);

        return end < 0 ? body[start..] : body[start..end];
    }

    private static StatusPage ApprovedPage() => new(
        "10-intro/README.md",
        "Getting started",
        StatusSync.InSync,
        "131100",
        "https://example.atlassian.net/wiki/spaces/DOCUMESBX/pages/131100",
        5,
        2,
        ApprovalStatus.Approved,
        "alice@example.com",
        "2026-07-20T08:00:00Z",
        5,
        Stale: false,
        DiagnosticCount: 0);

    /// <summary>
    /// Four pages covering every approval state at once: approved, needs-review, and two with no record
    /// (one of them unpublished, one of them stale).
    /// </summary>
    private static StatusPage[] Pages() =>
    [
        ApprovedPage(),
        new(
            "20-guides/README.md",
            "Guides",
            StatusSync.Drifted,
            "131101",
            null,
            3,
            0,
            ApprovalStatus.NeedsReview,
            null,
            null,
            null,
            Stale: true,
            DiagnosticCount: 0),
        new(
            "30-ops/README.md",
            "Operations",
            StatusSync.InSync,
            "131102",
            null,
            2,
            0,
            null,
            null,
            null,
            null,
            Stale: false,
            DiagnosticCount: 0),
        new(
            "40-new/README.md",
            "Draft page",
            StatusSync.Unpublished,
            null,
            null,
            null,
            0,
            null,
            null,
            null,
            null,
            Stale: false,
            DiagnosticCount: 0),
    ];

    private static StatusReport Report(IReadOnlyList<StatusPage>? pages = null) => new()
    {
        ConfigPath = "/repo/docume.json",
        WikiRoot = "/repo/docs/wiki",
        StatePath = "/repo/docs/wiki/_meta/state.json",
        StateFileExists = true,
        SpaceKey = "DOCUMESBX",
        BaseUrl = "https://example.atlassian.net/wiki",
        Pages = pages ?? Pages(),
    };

    private static string Wrap(string fragment)
        => "<root xmlns:ac=\"http://atlassian.com/content\" "
            + "xmlns:ri=\"http://atlassian.com/resource/identifier\">"
            + fragment
            + "</root>";
}
