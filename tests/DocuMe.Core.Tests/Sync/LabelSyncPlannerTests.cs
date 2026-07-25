using DocuMe.Core.State;
using DocuMe.Core.Sync;
using Shouldly;

namespace DocuMe.Core.Tests.Sync;

/// <summary>
/// The label reconciler (PLAN.md §6.3's Labels bullet, §8). Pure, so every case here is a state plus an
/// observation — no network, no clock, and the plan a <c>--dry-run</c> would print is the plan a real
/// run applies.
/// </summary>
public sealed class LabelSyncPlannerTests
{
    private const string Home = "README.md";
    private const string Guide = "20-guides/README.md";
    private const string HomeId = "200001";
    private const string GuideId = "200002";
    private const string ObservedAt = "2026-08-02T07:00:00Z";

    [Fact]
    public void A_new_label_records_an_approval_at_the_observed_version()
    {
        var state = StateWith((Home, new PageState { PageId = HomeId, PublishedVersion = 4 }));

        var plan = LabelSyncPlanner.Plan(state, Observation([HomeId], versions: new() { [HomeId] = 5 }));

        var approval = plan.Approvals.ShouldHaveSingleItem();
        approval.Path.ShouldBe(Home);
        approval.PageId.ShouldBe(HomeId);
        approval.ApprovedAt.ShouldBe(ObservedAt);
        approval.PreviousVersion.ShouldBeNull();

        // v5, not state's v4: §8 records the version current at observation time, and the two differ
        // exactly when a human edited the page in a browser.
        approval.Version.ShouldBe(5);
    }

    [Fact]
    public void The_approver_is_unknown_rather_than_the_authenticating_account()
    {
        // §13 S3, settled with §6.3's documented fallback: Confluence exposes no label author, and the
        // account DocuMe authenticates as is not the reviewer.
        var state = StateWith((Home, new PageState { PageId = HomeId }));

        var plan = LabelSyncPlanner.Plan(state, Observation([HomeId]));

        plan.Approvals.ShouldHaveSingleItem().ApprovedBy.ShouldBe("unknown");
        LabelSyncPlanner.UnknownApprover.ShouldBe("unknown");
    }

    [Fact]
    public void An_absent_label_on_an_approved_page_is_a_revocation()
    {
        var state = StateWith((Home, new PageState { PageId = HomeId, Approval = Approved(4) }));

        var plan = LabelSyncPlanner.Plan(state, Observation([]));

        var revocation = plan.Revocations.ShouldHaveSingleItem();
        revocation.Path.ShouldBe(Home);
        revocation.ApprovedVersion.ShouldBe(4);
        plan.Approvals.ShouldBeEmpty();
    }

    [Fact]
    public void An_unchanged_space_plans_nothing()
    {
        // The cron case. A sync that restamped approvedAt every run would commit an empty diff through a
        // PR every 30 minutes (§6.3).
        var state = StateWith(
            (Home, new PageState { PageId = HomeId, Approval = Approved(4) }),
            (Guide, new PageState { PageId = GuideId, Stale = true }));

        var plan = LabelSyncPlanner.Plan(
            state,
            Observation([HomeId], stale: [GuideId], versions: new() { [HomeId] = 4 }));

        plan.HasChanges.ShouldBeFalse();
        plan.ChangeCount.ShouldBe(0);
    }

    [Fact]
    public void A_label_that_stayed_while_the_page_moved_is_re_recorded()
    {
        var state = StateWith((Home, new PageState { PageId = HomeId, Approval = Approved(4) }));

        var plan = LabelSyncPlanner.Plan(state, Observation([HomeId], versions: new() { [HomeId] = 9 }));

        var approval = plan.Approvals.ShouldHaveSingleItem();
        approval.Version.ShouldBe(9);
        approval.PreviousVersion.ShouldBe(4);
        plan.Revocations.ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_version_restamps_nothing()
    {
        // "We could not establish a version" is not evidence the page moved, and an approval rewritten
        // with a null version would erase the one fact §8 asks to be recorded.
        var state = StateWith((Home, new PageState { PageId = HomeId, Approval = Approved(4) }));

        var plan = LabelSyncPlanner.Plan(state, Observation([HomeId]));

        plan.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public void Stale_flags_follow_the_label_both_ways()
    {
        var state = StateWith(
            (Home, new PageState { PageId = HomeId, Stale = true }),
            (Guide, new PageState { PageId = GuideId }));

        var plan = LabelSyncPlanner.Plan(state, Observation([], stale: [GuideId]));

        // Ordinal by path, so "20-guides/…" precedes "README.md".
        plan.StaleChanges.Count.ShouldBe(2);
        plan.StaleChanges[0].Path.ShouldBe(Guide);
        plan.StaleChanges[0].Stale.ShouldBeTrue();
        plan.StaleChanges[1].Path.ShouldBe(Home);
        plan.StaleChanges[1].Stale.ShouldBeFalse();
    }

    [Fact]
    public void A_labelled_page_state_does_not_manage_is_reported_and_skipped()
    {
        var state = StateWith((Home, new PageState { PageId = HomeId }));

        var plan = LabelSyncPlanner.Plan(state, Observation(["999999"], stale: ["999999"]));

        var unmanaged = plan.Unmanaged.ShouldHaveSingleItem();
        unmanaged.PageId.ShouldBe("999999");
        unmanaged.Approved.ShouldBeTrue();
        unmanaged.Stale.ShouldBeTrue();

        // Reported, but not a state change: someone labelling their own page in a shared space must not
        // produce a commit.
        plan.Approvals.ShouldBeEmpty();
        plan.HasChanges.ShouldBeFalse();
    }

    [Fact]
    public void Matching_is_by_page_id_never_by_title()
    {
        // A state entry whose title equals the labelled page's title but whose id does not is still not
        // that page. Titles are things humans rename; ids are not.
        var state = StateWith((Home, new PageState { PageId = HomeId, Title = "Home" }));

        var plan = LabelSyncPlanner.Plan(state, Observation(["777777"]));

        plan.Approvals.ShouldBeEmpty();
        plan.Unmanaged.ShouldHaveSingleItem().PageId.ShouldBe("777777");
    }

    [Fact]
    public void An_unpublished_page_is_skipped()
    {
        // No pageId means no Confluence page, so no label on it can exist to observe.
        var state = StateWith((Home, new PageState { ContentHash = "sha256:whatever" }));

        var plan = LabelSyncPlanner.Plan(state, Observation([]));

        plan.HasChanges.ShouldBeFalse();
        plan.Unmanaged.ShouldBeEmpty();
    }

    [Fact]
    public void The_plan_is_ordered_by_path_so_two_runs_read_the_same()
    {
        var state = StateWith(
            (Guide, new PageState { PageId = GuideId }),
            (Home, new PageState { PageId = HomeId }));

        var plan = LabelSyncPlanner.Plan(state, Observation([HomeId, GuideId]));

        // State's insertion order was Guide, Home; the plan is ordered by path regardless, and ordinal
        // sorting puts a numeric prefix ahead of a capital letter.
        string.Join(",", plan.Approvals.Select(approval => approval.Path)).ShouldBe($"{Guide},{Home}");
    }

    [Fact]
    public void Apply_writes_the_plan_and_keeps_the_audit_trail()
    {
        var state = StateWith(
            (Home, new PageState { PageId = HomeId, Approval = Approved(4) }),
            (Guide, new PageState { PageId = GuideId, Stale = true }));

        // Home loses its label; Guide gains one and loses its stale flag.
        var plan = LabelSyncPlanner.Plan(
            state,
            Observation([GuideId], versions: new() { [GuideId] = 2 }));

        var updated = LabelSyncPlanner.Apply(state, plan);

        var home = updated.Pages[Home].Approval;
        home!.Status.ShouldBe(ApprovalStatus.NeedsReview);
        home.ApprovedBy.ShouldBeNull();
        home.History.ShouldHaveSingleItem().Version.ShouldBe(4);

        var guide = updated.Pages[Guide];
        guide.Approval!.Status.ShouldBe(ApprovalStatus.Approved);
        guide.Approval.ApprovedVersion.ShouldBe(2);
        guide.Stale.ShouldBeFalse();
    }

    [Fact]
    public void Apply_of_an_empty_plan_changes_nothing()
    {
        var state = StateWith((Home, new PageState { PageId = HomeId, Approval = Approved(4) }));

        var plan = LabelSyncPlanner.Plan(state, Observation([HomeId], versions: new() { [HomeId] = 4 }));

        LabelSyncPlanner.Apply(state, plan).ShouldBeSameAs(state);
    }

    [Fact]
    public void Plan_then_apply_survives_the_state_file()
    {
        // The whole point of the slice against the real file: what a sync decides has to be what the
        // next run and `docume status` read back.
        var dir = Directory.CreateTempSubdirectory("docume-label-sync");
        try
        {
            var path = System.IO.Path.Combine(dir.FullName, "state.json");
            var state = StateWith((Home, new PageState { PageId = HomeId, PublishedVersion = 4 }));

            var plan = LabelSyncPlanner.Plan(state, Observation([HomeId], versions: new() { [HomeId] = 5 }));
            StateStore.Save(path, LabelSyncPlanner.Apply(state, plan));

            var approval = StateStore.Load(path).Pages[Home].Approval;
            approval!.Status.ShouldBe(ApprovalStatus.Approved);
            approval.ApprovedBy.ShouldBe("unknown");
            approval.ApprovedAt.ShouldBe(ObservedAt);
            approval.ApprovedVersion.ShouldBe(5);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Apply_records_the_shape_an_approval_state_machine_reads_back()
    {
        // The two halves of §8 have to agree on one record: sync writes it, publish's invalidation
        // decides against it, and `docume status` reports it. Pinned here as a field-by-field shape
        // because the reader of each is a different slice.
        var state = StateWith((Home, new PageState { PageId = HomeId, PublishedVersion = 5 }));

        var plan = LabelSyncPlanner.Plan(state, Observation([HomeId], versions: new() { [HomeId] = 5 }));
        var approval = LabelSyncPlanner.Apply(state, plan).Pages[Home].Approval;

        approval.ShouldNotBeNull();
        approval.Status.ShouldBe(ApprovalStatus.Approved);
        approval.ApprovedBy.ShouldBe(LabelSyncPlanner.UnknownApprover);
        approval.ApprovedAt.ShouldBe(ObservedAt);
        approval.ApprovedVersion.ShouldBe(5);
    }

    [Fact]
    public void An_observation_with_no_timestamp_is_refused()
    {
        // approvedAt is an audit field (§8); an empty one would be a record of nothing.
        var observation = new LabelObservation(
            [],
            [],
            new Dictionary<string, int>(StringComparer.Ordinal),
            string.Empty);

        Should.Throw<ArgumentException>(() => LabelSyncPlanner.Plan(new DocumeState(), observation));
    }

    private static LabelObservation Observation(
        string[] approved,
        string[]? stale = null,
        Dictionary<string, int>? versions = null)
        => new(
            approved,
            stale ?? [],
            versions ?? new Dictionary<string, int>(StringComparer.Ordinal),
            ObservedAt);

    private static ApprovalState Approved(int version) => new()
    {
        Status = ApprovalStatus.Approved,
        ApprovedBy = "unknown",
        ApprovedAt = "2026-07-18T08:30:00Z",
        ApprovedVersion = version,
    };

    private static DocumeState StateWith(params (string Path, PageState Page)[] pages) => new()
    {
        Pages = pages.ToDictionary(entry => entry.Path, entry => entry.Page, StringComparer.Ordinal),
    };
}
