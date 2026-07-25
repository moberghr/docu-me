using DocuMe.Core.Drift;
using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.Drift;

/// <summary>
/// The join that turns a drift report into label writes (PLAN.md §6.4's <c>--mark</c>). Pure, so every
/// case here is a report plus a state — the plan a <c>--dry-run</c> prints is the plan a real run applies.
/// </summary>
public sealed class DriftMarkPlannerTests
{
    private const string Loans = "domains/loans.md";
    private const string Rates = "domains/rates.md";
    private const string LoansId = "200001";
    private const string RatesId = "200002";

    [Fact]
    public void An_affected_published_page_is_labelled()
    {
        var state = StateWith((Loans, new PageState { PageId = LoansId, Title = "Loans" }));

        var plan = DriftMarkPlanner.Plan(ReportFor(Loans), state);

        var mark = plan.ToLabel.ShouldHaveSingleItem();
        mark.Path.ShouldBe(Loans);
        mark.PageId.ShouldBe(LoansId);
        plan.HasChanges.ShouldBeTrue();
        plan.ChangeCount.ShouldBe(1);
        plan.AlreadyMarked.ShouldBeEmpty();
        plan.Unmarkable.ShouldBeEmpty();
    }

    [Fact]
    public void The_title_comes_from_state_rather_than_the_tree()
    {
        // The label goes on the page as published, so a rename in the repo reads as the two different
        // names it really is rather than being quietly reconciled to the new one.
        var state = StateWith((Loans, new PageState { PageId = LoansId, Title = "Loans (published)" }));

        var plan = DriftMarkPlanner.Plan(ReportFor(Loans, title: "Loans, renamed in the repo"), state);

        plan.ToLabel.ShouldHaveSingleItem().Title.ShouldBe("Loans (published)");
    }

    [Fact]
    public void A_page_state_already_calls_stale_costs_no_request()
    {
        // §6.3 reconciles the flag from the live labels, so state saying stale is Confluence saying it
        // too. Re-adding the label would be a request that changes nothing.
        var state = StateWith((Loans, new PageState { PageId = LoansId, Stale = true }));

        var plan = DriftMarkPlanner.Plan(ReportFor(Loans), state);

        plan.ToLabel.ShouldBeEmpty();
        plan.HasChanges.ShouldBeFalse();
        plan.AlreadyMarked.ShouldHaveSingleItem().Path.ShouldBe(Loans);
    }

    [Fact]
    public void An_affected_page_state_has_never_seen_is_reported_rather_than_failing()
    {
        // Declaring `sources` on a page nobody has published yet is ordinary. There is no page to put a
        // label on, so it is named and skipped — failing the run would fail an advisory check for a
        // reason that has nothing to do with drift.
        var plan = DriftMarkPlanner.Plan(ReportFor(Loans), new DocumeState());

        plan.ToLabel.ShouldBeEmpty();
        var skipped = plan.Unmarkable.ShouldHaveSingleItem();
        skipped.Path.ShouldBe(Loans);
        skipped.Reason.ShouldBe(DriftMarkPlanner.NeverPublishedReason);
    }

    [Fact]
    public void A_state_entry_with_no_page_id_is_reported_rather_than_failing()
    {
        var state = StateWith((Loans, new PageState { Title = "Loans" }));

        var plan = DriftMarkPlanner.Plan(ReportFor(Loans), state);

        plan.ToLabel.ShouldBeEmpty();
        plan.Unmarkable.ShouldHaveSingleItem().Reason.ShouldBe(DriftMarkPlanner.NoPageIdReason);
    }

    [Fact]
    public void Unaffected_pages_are_never_touched()
    {
        // The report is the whole scope of a mark run: a published page that drifted last month but not
        // in this diff keeps whatever flag it has.
        var state = StateWith(
            (Loans, new PageState { PageId = LoansId }),
            (Rates, new PageState { PageId = RatesId }));

        var plan = DriftMarkPlanner.Plan(ReportFor(Loans), state);

        plan.ToLabel.ShouldHaveSingleItem().Path.ShouldBe(Loans);
    }

    [Fact]
    public void A_report_with_no_drift_plans_nothing()
    {
        var state = StateWith((Loans, new PageState { PageId = LoansId }));

        var plan = DriftMarkPlanner.Plan(Report([]), state);

        plan.HasChanges.ShouldBeFalse();
        plan.ToLabel.ShouldBeEmpty();
        plan.AlreadyMarked.ShouldBeEmpty();
        plan.Unmarkable.ShouldBeEmpty();
    }

    [Fact]
    public void The_plan_keeps_the_reports_order()
    {
        // Path order, from DriftPlanner: the dry-run listing, the terminal log and the request sequence
        // all read the same way twice.
        var state = StateWith(
            (Loans, new PageState { PageId = LoansId }),
            (Rates, new PageState { PageId = RatesId }));

        var report = Report([Page(Loans, "Loans"), Page(Rates, "Rates")]);

        var plan = DriftMarkPlanner.Plan(report, state);

        plan.ToLabel.Select(mark => mark.Path).ShouldBe([Loans, Rates]);
    }

    [Fact]
    public void The_three_outcomes_partition_the_affected_pages()
    {
        var state = StateWith(
            (Loans, new PageState { PageId = LoansId }),
            (Rates, new PageState { PageId = RatesId, Stale = true }));

        var report = Report([Page(Loans, "Loans"), Page(Rates, "Rates"), Page("domains/fx.md", "FX")]);

        var plan = DriftMarkPlanner.Plan(report, state);

        plan.ToLabel.ShouldHaveSingleItem().Path.ShouldBe(Loans);
        plan.AlreadyMarked.ShouldHaveSingleItem().Path.ShouldBe(Rates);
        plan.Unmarkable.ShouldHaveSingleItem().Path.ShouldBe("domains/fx.md");
    }

    private static DriftReport ReportFor(string path, string title = "Loans") =>
        Report([Page(path, title)]);

    private static DriftReport Report(IReadOnlyList<DriftedPage> pages) => new()
    {
        Baseline = "abc1234",
        Head = "HEAD",
        ChangedFileCount = 3,
        PageCount = 4,
        PagesWithSourcesCount = 3,
        Pages = pages,
    };

    private static DriftedPage Page(string path, string title) =>
        new(path, title, [new SourceMatch("src/**", ["src/Thing.cs"])]);

    private static DocumeState StateWith(params (string Path, PageState Page)[] pages) => new()
    {
        Pages = pages.ToDictionary(entry => entry.Path, entry => entry.Page, StringComparer.Ordinal),
    };
}
