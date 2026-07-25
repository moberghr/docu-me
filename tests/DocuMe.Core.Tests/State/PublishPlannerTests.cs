using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.State;

public sealed class PublishPlannerTests
{
    private const string Path = "10-domains/loans/README.md";
    private const string OldHash = "sha256:1111";
    private const string NewHash = "sha256:2222";

    private static readonly Dictionary<string, string> NoAttachments = [];

    private static PageState Published(
        string contentHash = OldHash,
        string? approvalStatus = null,
        Dictionary<string, string>? attachments = null) => new()
        {
            PageId = "123456",
            Title = "Loans Domain",
            ContentHash = contentHash,
            PublishedVersion = 6,
            Attachments = attachments ?? [],
            Approval = approvalStatus is null ? null : new ApprovalState { Status = approvalStatus },
        };

    [Fact]
    public void PlanPage_NotInState_Creates()
    {
        var plan = PublishPlanner.PlanPage(Path, current: null, NewHash, NoAttachments);

        plan.Action.ShouldBe(PagePublishAction.Create);
        plan.ContentHash.ShouldBe(NewHash);
        plan.WritesBody.ShouldBeTrue();
        plan.InvalidatesApproval.ShouldBeFalse();
    }

    [Fact]
    public void PlanPage_StateEntryWithoutPageId_Creates()
    {
        // What `init --adopt` leaves behind: paths and titles, no page yet (§6.1).
        var adopted = new PageState { Title = "Loans Domain" };

        var plan = PublishPlanner.PlanPage(Path, adopted, NewHash, NoAttachments);

        plan.Action.ShouldBe(PagePublishAction.Create);
    }

    [Fact]
    public void PlanPage_Create_UploadsEveryAttachment()
    {
        var attachments = new Dictionary<string, string> { ["b.svg"] = "sha256:b", ["a.png"] = "sha256:a" };

        var plan = PublishPlanner.PlanPage(Path, current: null, NewHash, attachments);

        plan.ChangedAttachments.ShouldBe(["a.png", "b.svg"]);
        plan.OrphanAttachments.ShouldBeEmpty();
    }

    [Fact]
    public void PlanPage_UnchangedHash_Skips()
    {
        var plan = PublishPlanner.PlanPage(Path, Published(), OldHash, NoAttachments);

        plan.Action.ShouldBe(PagePublishAction.Skip);
        plan.WritesBody.ShouldBeFalse();
        plan.ChangedAttachments.ShouldBeEmpty();
        plan.InvalidatesApproval.ShouldBeFalse();
    }

    [Fact]
    public void PlanPage_ChangedHash_Updates()
    {
        var plan = PublishPlanner.PlanPage(Path, Published(), NewHash, NoAttachments);

        plan.Action.ShouldBe(PagePublishAction.Update);
        plan.WritesBody.ShouldBeTrue();
    }

    [Fact]
    public void PlanPage_ChangedHashOnApprovedPage_InvalidatesApproval()
    {
        var plan = PublishPlanner.PlanPage(Path, Published(approvalStatus: ApprovalStatus.Approved), NewHash, NoAttachments);

        plan.InvalidatesApproval.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ApprovalStatus.NeedsReview)]
    public void PlanPage_ChangedHashOnUnapprovedPage_DoesNotInvalidate(string? status)
    {
        var plan = PublishPlanner.PlanPage(Path, Published(approvalStatus: status), NewHash, NoAttachments);

        plan.InvalidatesApproval.ShouldBeFalse();
    }

    [Fact]
    public void PlanPage_UnchangedHashOnApprovedPage_KeepsApproval()
    {
        // §8: banner-only and machine edits never invalidate — invalidation keys off contentHash.
        var plan = PublishPlanner.PlanPage(Path, Published(approvalStatus: ApprovalStatus.Approved), OldHash, NoAttachments);

        plan.Action.ShouldBe(PagePublishAction.Skip);
        plan.InvalidatesApproval.ShouldBeFalse();
    }

    [Fact]
    public void PlanPage_ChangedAttachmentBytesOnly_UpdatesAttachmentsWithoutTouchingBody()
    {
        var current = Published(attachments: new Dictionary<string, string>
        {
            ["diagram.svg"] = "sha256:old",
            ["logo.png"] = "sha256:same",
        });
        var now = new Dictionary<string, string>
        {
            ["diagram.svg"] = "sha256:new",
            ["logo.png"] = "sha256:same",
        };

        var plan = PublishPlanner.PlanPage(Path, current, OldHash, now);

        plan.Action.ShouldBe(PagePublishAction.UpdateAttachments);
        plan.WritesBody.ShouldBeFalse();
        plan.ChangedAttachments.ShouldBe(["diagram.svg"]);
    }

    [Fact]
    public void PlanPage_ChangedAttachmentOnApprovedPage_KeepsApproval()
    {
        var current = Published(
            approvalStatus: ApprovalStatus.Approved,
            attachments: new Dictionary<string, string> { ["logo.png"] = "sha256:old" });

        var plan = PublishPlanner.PlanPage(Path, current, OldHash, new Dictionary<string, string> { ["logo.png"] = "sha256:new" });

        plan.InvalidatesApproval.ShouldBeFalse();
    }

    [Fact]
    public void PlanPage_NewAttachmentName_CountsAsChanged()
    {
        var current = Published(attachments: new Dictionary<string, string> { ["logo.png"] = "sha256:same" });
        var now = new Dictionary<string, string> { ["logo.png"] = "sha256:same", ["new.svg"] = "sha256:x" };

        var plan = PublishPlanner.PlanPage(Path, current, OldHash, now);

        plan.ChangedAttachments.ShouldBe(["new.svg"]);
    }

    [Fact]
    public void PlanPage_AttachmentNoLongerReferenced_IsReportedAsOrphanNotUploaded()
    {
        var current = Published(attachments: new Dictionary<string, string>
        {
            ["gone.svg"] = "sha256:g",
            ["kept.png"] = "sha256:k",
        });

        var plan = PublishPlanner.PlanPage(Path, current, OldHash, new Dictionary<string, string> { ["kept.png"] = "sha256:k" });

        plan.OrphanAttachments.ShouldBe(["gone.svg"]);
        plan.ChangedAttachments.ShouldBeEmpty();
        plan.Action.ShouldBe(PagePublishAction.Skip);
    }

    [Fact]
    public void PlanPage_Force_UpdatesAndReuploadsEverything()
    {
        var current = Published(attachments: new Dictionary<string, string> { ["logo.png"] = "sha256:same" });

        var plan = PublishPlanner.PlanPage(
            Path, current, OldHash, new Dictionary<string, string> { ["logo.png"] = "sha256:same" }, force: true);

        plan.Action.ShouldBe(PagePublishAction.Update);
        plan.ChangedAttachments.ShouldBe(["logo.png"]);
    }

    [Fact]
    public void PlanPage_ForceOnApprovedUnchangedPage_DoesNotInvalidate()
    {
        // --force distrusts the remote, not the content: nothing the reviewer approved changed.
        var plan = PublishPlanner.PlanPage(
            Path, Published(approvalStatus: ApprovalStatus.Approved), OldHash, NoAttachments, force: true);

        plan.Action.ShouldBe(PagePublishAction.Update);
        plan.InvalidatesApproval.ShouldBeFalse();
    }

    [Fact]
    public void PlanPage_EmptyPath_Throws()
    {
        Should.Throw<ArgumentException>(() => PublishPlanner.PlanPage(string.Empty, null, NewHash, NoAttachments));
    }

    [Fact]
    public void OrphanPages_ReturnsStateEntriesWithNoFile_Sorted()
    {
        var state = new DocumeState
        {
            Pages = new Dictionary<string, PageState>
            {
                ["z-gone.md"] = Published(),
                ["a-gone.md"] = Published(),
                ["kept.md"] = Published(),
            },
        };

        var orphans = PublishPlanner.OrphanPages(state, ["kept.md", "brand-new.md"]);

        orphans.ShouldBe(["a-gone.md", "z-gone.md"]);
    }

    [Fact]
    public void OrphanPages_EverythingPresent_IsEmpty()
    {
        var state = new DocumeState
        {
            Pages = new Dictionary<string, PageState> { ["kept.md"] = Published() },
        };

        PublishPlanner.OrphanPages(state, ["kept.md"]).ShouldBeEmpty();
    }
}
