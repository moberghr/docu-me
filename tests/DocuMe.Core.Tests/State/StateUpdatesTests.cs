using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.State;

public sealed class StateUpdatesTests
{
    private const string Path = "10-domains/loans/README.md";

    private static readonly PublishedPage Result = new(
        PageId: "123456",
        Title: "Loans Domain",
        ParentPageId: "999",
        ContentHash: "sha256:new",
        PublishedVersion: 7,
        Attachments: new Dictionary<string, string> { ["diagram.svg"] = "sha256:d" },
        DiagramWidths: new Dictionary<string, string> { ["diagram.svg"] = "213" });

    private static DocumeState StateWith(PageState page) => new()
    {
        Pages = new Dictionary<string, PageState> { [Path] = page },
    };

    private static ApprovalState ApprovedByJonas() => new()
    {
        Status = ApprovalStatus.Approved,
        ApprovedBy = "jonas",
        ApprovedAt = "2026-08-01T09:00:00Z",
        ApprovedVersion = 6,
    };

    [Fact]
    public void RecordPublish_UnknownPage_AddsEntry()
    {
        var updated = StateUpdates.RecordPublish(new DocumeState(), Path, Result);

        var page = updated.Pages[Path];
        page.PageId.ShouldBe("123456");
        page.Title.ShouldBe("Loans Domain");
        page.ParentPageId.ShouldBe("999");
        page.ContentHash.ShouldBe("sha256:new");
        page.PublishedVersion.ShouldBe(7);
        page.Attachments["diagram.svg"].ShouldBe("sha256:d");
    }

    [Fact]
    public void RecordPublish_PreservesFieldsPublishDoesNotOwn()
    {
        // approval, stale and feedbackCursor belong to sync (§6.3) and drift (§6.4). A publish that
        // reset them would drop an approval the reviewer never withdrew and re-ingest old comments.
        var existing = new PageState
        {
            PageId = "123456",
            ContentHash = "sha256:old",
            Approval = ApprovedByJonas(),
            Stale = true,
            FeedbackCursor = "2026-08-01T10:00:00Z",
        };

        var page = StateUpdates.RecordPublish(StateWith(existing), Path, Result).Pages[Path];

        page.Approval!.Status.ShouldBe(ApprovalStatus.Approved);
        page.Approval.ApprovedBy.ShouldBe("jonas");
        page.Stale.ShouldBeTrue();
        page.FeedbackCursor.ShouldBe("2026-08-01T10:00:00Z");
    }

    [Fact]
    public void RecordPublish_ReplacesAttachmentSet()
    {
        var existing = new PageState
        {
            PageId = "123456",
            Attachments = new Dictionary<string, string> { ["gone.svg"] = "sha256:g", ["diagram.svg"] = "sha256:old" },
        };

        var page = StateUpdates.RecordPublish(StateWith(existing), Path, Result).Pages[Path];

        page.Attachments.Count.ShouldBe(1);
        page.Attachments["diagram.svg"].ShouldBe("sha256:d");
    }

    /// <summary>
    /// Widths are replaced wholesale like the attachment set, and for the same reason: a diagram the page
    /// dropped must not leave a width behind that a later body write would inject into markup nothing
    /// references (<c>DiagramImageWidth</c> throws on exactly that).
    /// </summary>
    [Fact]
    public void RecordPublish_ReplacesDiagramWidthSet()
    {
        var existing = new PageState
        {
            PageId = "123456",
            DiagramWidths = new Dictionary<string, string> { ["gone.svg"] = "88", ["diagram.svg"] = "120" },
        };

        var page = StateUpdates.RecordPublish(StateWith(existing), Path, Result).Pages[Path];

        page.DiagramWidths.Count.ShouldBe(1);
        page.DiagramWidths["diagram.svg"].ShouldBe("213");
    }

    [Fact]
    public void RecordPublish_LeavesOtherPagesAndInputUntouched()
    {
        var other = new PageState { PageId = "777", ContentHash = "sha256:other" };
        var original = new DocumeState
        {
            Pages = new Dictionary<string, PageState> { ["other.md"] = other },
        };

        var updated = StateUpdates.RecordPublish(original, Path, Result);

        updated.Pages["other.md"].ContentHash.ShouldBe("sha256:other");
        original.Pages.ShouldNotContainKey(Path);
    }

    [Fact]
    public void InvalidateApproval_ApprovedPage_MovesToNeedsReviewAndKeepsHistory()
    {
        var existing = new PageState { PageId = "123456", Approval = ApprovedByJonas() };

        var page = StateUpdates.InvalidateApproval(StateWith(existing), Path).Pages[Path];

        page.Approval!.Status.ShouldBe(ApprovalStatus.NeedsReview);
        page.Approval.ApprovedBy.ShouldBeNull();
        page.Approval.ApprovedAt.ShouldBeNull();
        page.Approval.ApprovedVersion.ShouldBeNull();

        var entry = page.Approval.History.ShouldHaveSingleItem();
        entry.By.ShouldBe("jonas");
        entry.At.ShouldBe("2026-08-01T09:00:00Z");
        entry.Version.ShouldBe(6);
    }

    [Fact]
    public void InvalidateApproval_AppendsToExistingHistory()
    {
        var existing = new PageState
        {
            PageId = "123456",
            Approval = ApprovedByJonas() with { History = [new ApprovalHistoryEntry { By = "mirko", Version = 4 }] },
        };

        var history = StateUpdates.InvalidateApproval(StateWith(existing), Path).Pages[Path].Approval!.History;

        history.Count.ShouldBe(2);
        history[0].By.ShouldBe("mirko");
        history[1].By.ShouldBe("jonas");
    }

    [Fact]
    public void InvalidateApproval_AlreadyNeedsReview_IsUnchanged()
    {
        var existing = new PageState
        {
            PageId = "123456",
            Approval = new ApprovalState { Status = ApprovalStatus.NeedsReview },
        };

        var page = StateUpdates.InvalidateApproval(StateWith(existing), Path).Pages[Path];

        page.Approval!.History.ShouldBeEmpty();
        page.Approval.Status.ShouldBe(ApprovalStatus.NeedsReview);
    }

    [Fact]
    public void InvalidateApproval_NeverApproved_IsUnchanged()
    {
        var page = StateUpdates.InvalidateApproval(StateWith(new PageState { PageId = "123456" }), Path).Pages[Path];

        page.Approval.ShouldBeNull();
    }

    [Fact]
    public void InvalidateApproval_UnknownPage_IsUnchanged()
    {
        var state = new DocumeState();

        StateUpdates.InvalidateApproval(state, "nope.md").Pages.ShouldBeEmpty();
    }

    [Fact]
    public void InvalidateApproval_IsIdempotent()
    {
        var once = StateUpdates.InvalidateApproval(
            StateWith(new PageState { PageId = "123456", Approval = ApprovedByJonas() }), Path);

        var twice = StateUpdates.InvalidateApproval(once, Path);

        twice.Pages[Path].Approval!.History.ShouldHaveSingleItem();
    }

    [Fact]
    public void RecordLastPublishedSha_StampsSha()
    {
        StateUpdates.RecordLastPublishedSha(new DocumeState(), "abc123").LastPublishedSha.ShouldBe("abc123");
    }

    [Fact]
    public void RemovePage_DropsEntry()
    {
        var state = StateWith(new PageState { PageId = "123456" });

        StateUpdates.RemovePage(state, Path).Pages.ShouldBeEmpty();
        state.Pages.ShouldContainKey(Path);
    }

    [Fact]
    public void RemovePage_UnknownPage_IsUnchanged()
    {
        var state = StateWith(new PageState { PageId = "123456" });

        StateUpdates.RemovePage(state, "nope.md").Pages.Count.ShouldBe(1);
    }

    [Fact]
    public void PublishRoundTrip_SurvivesSaveAndLoad()
    {
        // The whole §6.2 step 7-8 sequence against the real file, since state.json is what the next
        // run compares against: publish, invalidate, stamp the sha, persist, reload.
        var dir = Directory.CreateTempSubdirectory("docume-state-updates");
        try
        {
            var path = System.IO.Path.Combine(dir.FullName, "state.json");
            var state = StateWith(new PageState
            {
                PageId = "123456",
                ContentHash = "sha256:old",
                PublishedVersion = 6,
                Approval = ApprovedByJonas(),
            });

            state = StateUpdates.RecordPublish(state, Path, Result);
            state = StateUpdates.InvalidateApproval(state, Path);
            state = StateUpdates.RecordLastPublishedSha(state, "abc123");
            StateStore.Save(path, state);

            var loaded = StateStore.Load(path);

            loaded.LastPublishedSha.ShouldBe("abc123");
            var page = loaded.Pages[Path];
            page.ContentHash.ShouldBe("sha256:new");
            page.PublishedVersion.ShouldBe(7);
            page.Approval!.Status.ShouldBe(ApprovalStatus.NeedsReview);
            page.Approval.History.ShouldHaveSingleItem().By.ShouldBe("jonas");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
