using DocuMe.Core.Feedback;
using Shouldly;

namespace DocuMe.Core.Tests.Feedback;

/// <summary>
/// The reply decision (PLAN.md §9 step 5): which triaged items get answered, which comments get closed,
/// and what is reported rather than silently dropped.
/// </summary>
public sealed class FeedbackReplyPlannerTests
{
    private const string Page = "10-domains/loans/README.md";

    /// <summary>
    /// The ordinary case: a fixed item, an inline comment that is open and versioned, so the reply is
    /// planned and the close comes with it.
    /// </summary>
    [Fact]
    public void Answers_a_triaged_item_and_closes_the_inline_comment_it_answers()
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", Item("987654", FeedbackStatus.Fixed))],
            Live("987654", FeedbackKind.Inline)));

        plan.Replies.Count.ShouldBe(1);
        plan.Replies[0].CommentId.ShouldBe("987654");
        plan.Replies[0].Page.ShouldBe(Page);
        plan.Replies[0].Kind.ShouldBe(FeedbackKind.Inline);
        plan.Replies[0].Status.ShouldBe(FeedbackStatus.Fixed);
        plan.Replies[0].Resolve.ShouldBe(ReplyResolvePlan.Planned);
        plan.Replies[0].ResolveAtVersion.ShouldBe(2);
        plan.ResolveCount.ShouldBe(1);
        plan.HasChanges.ShouldBeTrue();
    }

    /// <summary>
    /// A footer comment has no resolution state at all, so §9 step 5's "where the API allows" means the
    /// reply alone. That is not a failure and must not read like one.
    /// </summary>
    [Fact]
    public void Replies_to_a_footer_comment_without_trying_to_close_it()
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", Item("987655", FeedbackStatus.Rejected))],
            Live("987655", FeedbackKind.Footer)));

        plan.Replies[0].Resolve.ShouldBe(ReplyResolvePlan.NotApplicable);
        plan.Replies[0].ResolveAtVersion.ShouldBeNull();
        plan.ResolveCount.ShouldBe(0);
    }

    /// <summary>
    /// <c>repliedAt</c> is the whole double-reply guard: the triage that set <c>status</c> happened in an
    /// earlier PR, so nothing else on the item says whether the reviewer was already told.
    /// </summary>
    [Fact]
    public void Never_answers_an_item_twice()
    {
        var answered = Item("987654", FeedbackStatus.Fixed) with
        {
            RepliedAt = "2026-08-03T09:00:00.000Z",
        };

        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", answered)],
            Live("987654", FeedbackKind.Inline)));

        plan.Replies.ShouldBeEmpty();
        plan.SkippedCount(FeedbackReplySkipReason.AlreadyReplied).ShouldBe(1);
    }

    /// <summary>An item /docs-feedback has not looked at yet is nobody's to answer.</summary>
    [Fact]
    public void Leaves_an_untriaged_item_alone()
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", Item("987654", FeedbackStatus.New))],
            Live("987654", FeedbackKind.Inline)));

        plan.Replies.ShouldBeEmpty();
        plan.SkippedCount(FeedbackReplySkipReason.NotTriaged).ShouldBe(1);
    }

    /// <summary>
    /// A status this build does not recognize is treated like an untriaged one. Answering it would put a
    /// sentence under a reviewer's comment that nothing in the item actually supports.
    /// </summary>
    [Fact]
    public void Treats_a_status_it_does_not_recognize_as_untriaged()
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", Item("987654", "escalated"))],
            Live("987654", FeedbackKind.Inline)));

        plan.Replies.ShouldBeEmpty();
        plan.SkippedCount(FeedbackReplySkipReason.NotTriaged).ShouldBe(1);
    }

    /// <summary>All three triage outcomes are answered, each with its own sentence.</summary>
    [Theory]
    [InlineData(FeedbackStatus.Fixed)]
    [InlineData(FeedbackStatus.Rejected)]
    [InlineData(FeedbackStatus.Question)]
    public void Answers_every_triage_outcome(string status)
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", Item("987654", status))],
            Live("987654", FeedbackKind.Footer)));

        plan.Replies.Count.ShouldBe(1);
        plan.Replies[0].Body.ShouldBe(FeedbackReplyText.Compose(status, null));
    }

    /// <summary>
    /// A comment somebody deleted is reported, not answered — and the item is left unstamped, so the
    /// report says the same thing on the next run rather than the item quietly reading as handled.
    /// </summary>
    [Fact]
    public void Reports_a_comment_that_no_longer_exists()
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", Item("987654", FeedbackStatus.Fixed))],
            Live("999999", FeedbackKind.Inline)));

        plan.Replies.ShouldBeEmpty();
        plan.Skipped.Single().Reason.ShouldBe(FeedbackReplySkipReason.CommentGone);
        plan.Skipped.Single().CommentId.ShouldBe("987654");
    }

    /// <summary>
    /// An item about a page whose comments were never read is a different fact from a deleted comment,
    /// and is reported as its own reason: the usual cause is a page state has no pageId for.
    /// </summary>
    [Fact]
    public void Distinguishes_an_unpublished_page_from_a_deleted_comment()
    {
        var observation = new FeedbackReplyObservation(
            [Stored("a.json", Item("987654", FeedbackStatus.Fixed))],
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, ObservedLiveComment>(StringComparer.Ordinal));

        var plan = FeedbackReplyPlanner.Plan(observation);

        plan.Skipped.Single().Reason.ShouldBe(FeedbackReplySkipReason.PageNotPublished);
    }

    /// <summary>
    /// A dangling comment still gets its reply — the reviewer asked something and deserves an answer —
    /// but no resolve is attempted, because Confluence's own schema refuses to update one.
    /// </summary>
    [Fact]
    public void Replies_to_a_dangling_comment_but_does_not_try_to_close_it()
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", Item("987654", FeedbackStatus.Fixed))],
            Live("987654", FeedbackKind.Inline) with { IsClosable = false }));

        plan.Replies.Count.ShouldBe(1);
        plan.Replies[0].Resolve.ShouldBe(ReplyResolvePlan.NotClosable);
        plan.Replies[0].ResolveAtVersion.ShouldBeNull();
    }

    /// <summary>
    /// A human who closed the comment between the triage and this run has done the loop's work for it.
    /// The reply still goes out; the close does not, and it is not reported as a problem.
    /// </summary>
    [Fact]
    public void Still_replies_to_a_comment_a_human_already_closed()
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", Item("987654", FeedbackStatus.Fixed))],
            Live("987654", FeedbackKind.Inline) with { IsResolved = true }));

        plan.Replies.Count.ShouldBe(1);
        plan.Replies[0].Resolve.ShouldBe(ReplyResolvePlan.AlreadyResolved);
    }

    /// <summary>A resolve is an optimistic-lock write, so a comment with no version cannot be closed.</summary>
    [Fact]
    public void Does_not_close_a_comment_the_channel_answered_no_version_for()
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", Item("987654", FeedbackStatus.Fixed))],
            Live("987654", FeedbackKind.Inline) with { Version = null }));

        plan.Replies[0].Resolve.ShouldBe(ReplyResolvePlan.NoVersion);
    }

    /// <summary>
    /// The endpoint a reply goes to comes from the live comment, not from the item's <c>kind</c>. The
    /// item is a committed file a human can edit; the channel is right there saying what the comment is.
    /// </summary>
    [Fact]
    public void Takes_the_endpoint_from_the_live_comment_not_from_the_file()
    {
        var mislabelled = Item("987654", FeedbackStatus.Fixed) with { Kind = FeedbackKind.Footer };

        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", mislabelled)],
            Live("987654", FeedbackKind.Inline)));

        plan.Replies[0].Kind.ShouldBe(FeedbackKind.Inline);
    }

    /// <summary>
    /// An id from a channel v1 does not implement is refused rather than posted bare: §9 makes the inbox
    /// the seam for future channels, and "jira-DOCS-4" is not a Confluence comment id.
    /// </summary>
    [Fact]
    public void Refuses_an_item_from_a_channel_it_cannot_post_to()
    {
        var foreign = Item("987654", FeedbackStatus.Fixed) with { Id = "jira-DOCS-4" };

        var plan = FeedbackReplyPlanner.Plan(Observation(
            [Stored("a.json", foreign)],
            Live("987654", FeedbackKind.Inline)));

        plan.Replies.ShouldBeEmpty();
        plan.Skipped.Single().Reason.ShouldBe(FeedbackReplySkipReason.Unaddressable);
    }

    /// <summary>An item file that would not parse is reported, and the run carries on with the rest.</summary>
    [Fact]
    public void Reports_an_unreadable_item_without_dropping_the_others()
    {
        var plan = FeedbackReplyPlanner.Plan(Observation(
            [
                new StoredFeedbackItem("broken.json", Item: null),
                Stored("a.json", Item("987654", FeedbackStatus.Fixed)),
            ],
            Live("987654", FeedbackKind.Inline)));

        plan.Replies.Count.ShouldBe(1);
        plan.SkippedCount(FeedbackReplySkipReason.Unreadable).ShouldBe(1);
    }

    /// <summary>
    /// The read is sized by what is pending: an already-answered item costs no page read, which is what
    /// keeps a nightly reply pass from re-reading an ~80-page wiki to do nothing.
    /// </summary>
    [Fact]
    public void Only_asks_for_the_pages_that_still_owe_a_reply()
    {
        var answered = Item("1", FeedbackStatus.Fixed) with
        {
            Page = "other.md",
            RepliedAt = "2026-08-03T09:00:00.000Z",
        };

        var pages = FeedbackReplyPlanner.PagesToRead(
        [
            Stored("a.json", Item("987654", FeedbackStatus.Fixed)),
            Stored("b.json", answered),
            Stored("c.json", Item("2", FeedbackStatus.New) with { Page = "third.md" }),
        ]);

        pages.ShouldBe([Page]);
    }

    /// <summary>The plan reads the same way twice regardless of what order the files arrived in.</summary>
    [Fact]
    public void Orders_replies_by_item_path()
    {
        var live = new Dictionary<string, ObservedLiveComment>(StringComparer.Ordinal)
        {
            ["1"] = new("1", FeedbackKind.Footer, IsResolved: false, IsClosable: true, Version: 1),
            ["2"] = new("2", FeedbackKind.Footer, IsResolved: false, IsClosable: true, Version: 1),
        };

        var observation = new FeedbackReplyObservation(
            [
                Stored("z.json", Item("2", FeedbackStatus.Fixed)),
                Stored("a.json", Item("1", FeedbackStatus.Fixed)),
            ],
            new HashSet<string>(StringComparer.Ordinal) { Page },
            live);

        FeedbackReplyPlanner.Plan(observation)
            .Replies.Select(reply => reply.CommentId)
            .ShouldBe(["1", "2"]);
    }

    private static FeedbackReplyObservation Observation(
        IReadOnlyList<StoredFeedbackItem> items,
        ObservedLiveComment live)
        => new(
            items,
            new HashSet<string>(StringComparer.Ordinal) { Page },
            new Dictionary<string, ObservedLiveComment>(StringComparer.Ordinal) { [live.Id] = live });

    private static ObservedLiveComment Live(string id, string kind)
        => new(id, kind, IsResolved: false, IsClosable: true, Version: 2);

    private static StoredFeedbackItem Stored(string name, FeedbackItem item) => new(name, item);

    private static FeedbackItem Item(string commentId, string status) => new()
    {
        Id = FeedbackItemId.ForConfluenceComment(commentId),
        Page = Page,
        Kind = FeedbackKind.Inline,
        Author = "Jónas",
        CreatedAt = "2026-08-02T14:11:00.000Z",
        Body = "<p>A claim to verify.</p>",
        Status = status,
    };
}
