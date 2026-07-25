using DocuMe.Core.Feedback;
using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.Feedback;

/// <summary>
/// The ingestion decision (PLAN.md §6.3's Comments bullet, §5.4): which comments become inbox items,
/// what those items say, and how far each page's <c>feedbackCursor</c> moves.
/// </summary>
/// <remarks>
/// No network, no clock, no filesystem — the planner is pure, so every case here is a shape of
/// observation rather than a scenario that has to be staged. The Confluence half of the same feature is
/// covered against WireMock in <see cref="FeedbackReaderTests"/>.
/// </remarks>
public sealed class FeedbackInboxPlannerTests
{
    private const string Path = "10-domains/loans/README.md";
    private const string BotAccount = "557058:docume-bot";
    private const string HumanAccount = "557058:jonas";

    /// <summary>
    /// §5.4 field by field, for the two kinds. An inline comment carries the text it is anchored to; a
    /// footer comment carries none, because it is anchored to nothing.
    /// </summary>
    [Fact]
    public void Writes_the_inbox_item_plan_md_5_4_describes()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("987654", FeedbackKind.Inline, createdAt: "2026-08-02T14:11:00.000Z") with
            {
                AuthorDisplayName = "Jónas",
                Body = "<p>This is wrong — disbursement is instant.</p>",
                QuotedText = "Loans are disbursed within 24 hours",
            },
            Comment("987655", FeedbackKind.Footer, createdAt: "2026-08-02T15:00:00.000Z") with
            {
                AuthorDisplayName = "Jónas",
                Body = "<p>Nice page.</p>",
                QuotedText = "a footer comment is anchored to nothing",
            }));

        plan.Items.Count.ShouldBe(2);

        var inline = plan.Items[0].Item;
        inline.Id.ShouldBe("conf-comment-987654");
        inline.Page.ShouldBe(Path);
        inline.Kind.ShouldBe("inline");
        inline.Author.ShouldBe("Jónas");
        inline.CreatedAt.ShouldBe("2026-08-02T14:11:00.000Z");
        inline.QuotedText.ShouldBe("Loans are disbursed within 24 hours");
        inline.Body.ShouldBe("<p>This is wrong — disbursement is instant.</p>");
        inline.Status.ShouldBe("new");
        inline.Resolution.ShouldBeNull();

        // quotedText is inline-only per §5.4: a footer comment that arrived carrying anchored text is
        // describing something the shape has no meaning for, so it is dropped rather than recorded.
        plan.Items[1].Item.Kind.ShouldBe("footer");
        plan.Items[1].Item.QuotedText.ShouldBeNull();
    }

    /// <summary>The file name §5.4 specifies: the page slug, the comment id, and nothing else.</summary>
    [Fact]
    public void Names_each_item_file_after_the_page_and_the_comment()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(Comment("987654", FeedbackKind.Inline)));

        plan.Items[0].FileName.ShouldBe("10-domains-loans-README-987654.json");
        plan.Items[0].Path.ShouldBe(Path);
    }

    /// <summary>
    /// §6.3's own rule: DocuMe's replies are not feedback. Without this the reply /docs-feedback posts
    /// (§9 step 5) comes back as a new claim on the next sync and gets triaged against the code.
    /// </summary>
    [Fact]
    public void Skips_the_comments_docume_posted_itself()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("1", FeedbackKind.Footer) with { AuthorAccountId = BotAccount },
            Comment("2", FeedbackKind.Footer)));

        plan.Items.Select(item => item.Item.Id).ShouldBe(["conf-comment-2"]);
        plan.Skipped.ShouldContain(skip => skip.CommentId == "1" && skip.Reason == FeedbackSkipReason.Bot);
    }

    /// <summary>
    /// A comment with no author cannot be shown to be DocuMe's, so it is ingested. Guessing the other way
    /// would silently drop a person's feedback.
    /// </summary>
    [Fact]
    public void Never_assumes_an_authorless_comment_is_its_own()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("1", FeedbackKind.Footer) with { AuthorAccountId = null, AuthorDisplayName = null }));

        plan.Items.Count.ShouldBe(1);
        plan.Items[0].Item.Author.ShouldBe("unknown");
    }

    /// <summary>
    /// With no bot account established, nothing is skipped as the bot's — the same asymmetry: a duplicate
    /// item is recoverable, a dropped reviewer comment is not.
    /// </summary>
    [Fact]
    public void Skips_nothing_as_its_own_when_the_bot_account_is_unknown()
    {
        var observation = new FeedbackObservation(
            [new ObservedPageComments(Path, null, [Comment("1", FeedbackKind.Footer) with
            {
                AuthorAccountId = BotAccount,
            }])],
            BotAccountId: null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var plan = FeedbackInboxPlanner.Plan(observation);

        plan.Items.Count.ShouldBe(1);
        plan.Skipped.ShouldBeEmpty();
    }

    /// <summary>
    /// A resolved comment is closed feedback: a human dealt with it, and filing it for triage would ask
    /// for the work twice. <c>dangling</c> and <c>reopened</c> are not resolved — see
    /// <c>CommentResolution</c> — and arrive here as <c>IsResolved: false</c>.
    /// </summary>
    [Fact]
    public void Does_not_file_a_comment_a_human_has_already_resolved()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("1", FeedbackKind.Inline) with { IsResolved = true },
            Comment("2", FeedbackKind.Inline)));

        plan.Items.Select(item => item.Item.Id).ShouldBe(["conf-comment-2"]);
        plan.Skipped.ShouldContain(skip => skip.CommentId == "1" && skip.Reason == FeedbackSkipReason.Resolved);
    }

    /// <summary>A comment the response carried no body for is reported, not filed as empty feedback.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reports_a_comment_with_no_body_rather_than_filing_an_empty_item(string? body)
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("1", FeedbackKind.Footer) with { Body = body }));

        plan.Items.ShouldBeEmpty();
        plan.Skipped.Single().Reason.ShouldBe(FeedbackSkipReason.NoBody);
    }

    /// <summary>
    /// The cursor is a watermark on <c>createdAt</c> (§5.3): strictly newer is ingested, the rest was
    /// filed by an earlier run.
    /// </summary>
    [Fact]
    public void Ingests_only_comments_newer_than_the_pages_cursor()
    {
        var observation = new FeedbackObservation(
            [new ObservedPageComments(
                Path,
                "2026-08-02T12:00:00.000Z",
                [
                    Comment("1", FeedbackKind.Footer, createdAt: "2026-08-02T11:00:00.000Z"),
                    Comment("2", FeedbackKind.Footer, createdAt: "2026-08-02T12:00:00.000Z"),
                    Comment("3", FeedbackKind.Footer, createdAt: "2026-08-02T13:00:00.000Z"),
                ])],
            BotAccount,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var plan = FeedbackInboxPlanner.Plan(observation);

        plan.Items.Select(item => item.Item.Id).ShouldBe(["conf-comment-3"]);
        plan.SkippedCount(FeedbackSkipReason.AlreadyIngested).ShouldBe(2);
        plan.Cursors.Single().Cursor.ShouldBe("2026-08-02T13:00:00.000Z");
    }

    /// <summary>
    /// A page nothing has been ingested from takes everything: the first sync on a page with existing
    /// discussion is supposed to pick that discussion up.
    /// </summary>
    [Fact]
    public void Ingests_every_comment_when_the_page_has_no_cursor_yet()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("1", FeedbackKind.Footer, createdAt: "2020-01-01T00:00:00.000Z"),
            Comment("2", FeedbackKind.Footer, createdAt: "2026-08-02T13:00:00.000Z")));

        plan.Items.Count.ShouldBe(2);
        plan.Cursors.Single().Cursor.ShouldBe("2026-08-02T13:00:00.000Z");
        plan.Cursors.Single().PreviousCursor.ShouldBeNull();
    }

    /// <summary>
    /// The cursor moves past comments the run deliberately declined. Otherwise a bot reply at the top of a
    /// thread would be re-decided on every run for as long as the page exists, and the cursor would never
    /// pass it.
    /// </summary>
    [Fact]
    public void Advances_the_cursor_past_comments_it_declined_to_file()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("1", FeedbackKind.Footer, createdAt: "2026-08-02T10:00:00.000Z"),
            Comment("2", FeedbackKind.Footer, createdAt: "2026-08-02T18:00:00.000Z") with
            {
                AuthorAccountId = BotAccount,
            }));

        plan.Items.Select(item => item.Item.Id).ShouldBe(["conf-comment-1"]);
        plan.Cursors.Single().Cursor.ShouldBe("2026-08-02T18:00:00.000Z");
    }

    /// <summary>
    /// Nothing new means nothing written: a cron sync over a page whose comments were all ingested plans
    /// no cursor move, so it produces no state diff and opens no empty PR (§6.3).
    /// </summary>
    [Fact]
    public void Plans_nothing_when_every_comment_is_already_ingested()
    {
        var observation = new FeedbackObservation(
            [new ObservedPageComments(
                Path,
                "2026-08-02T13:00:00.000Z",
                [Comment("1", FeedbackKind.Footer, createdAt: "2026-08-02T11:00:00.000Z")])],
            BotAccount,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var plan = FeedbackInboxPlanner.Plan(observation);

        plan.HasChanges.ShouldBeFalse();
        plan.Cursors.ShouldBeEmpty();
    }

    /// <summary>
    /// A page whose only comments carry no readable timestamp moves no cursor: a watermark nobody can
    /// place is not evidence of how far the run got.
    /// </summary>
    [Fact]
    public void Files_a_comment_with_an_unreadable_timestamp_without_moving_the_cursor()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("1", FeedbackKind.Footer, createdAt: "last Tuesday")));

        plan.Items.Count.ShouldBe(1);
        plan.Items[0].Item.CreatedAt.ShouldBeNull();
        plan.Cursors.ShouldBeEmpty();
    }

    /// <summary>The watermark never regresses, whatever order the comments arrived in.</summary>
    [Fact]
    public void Never_moves_a_cursor_backwards()
    {
        var observation = new FeedbackObservation(
            [new ObservedPageComments(
                Path,
                "2026-08-02T13:00:00.000Z",
                [
                    Comment("2", FeedbackKind.Footer, createdAt: "2026-08-02T09:00:00.000Z"),
                    Comment("1", FeedbackKind.Footer, createdAt: "2026-08-02T10:00:00.000Z"),
                ])],
            BotAccount,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var plan = FeedbackInboxPlanner.Plan(observation);

        plan.Cursors.ShouldBeEmpty();
    }

    /// <summary>
    /// An item already on disk is never rewritten — by the time /docs-feedback has triaged one it carries
    /// a status and a resolution, and a re-ingest would reset both to <c>new</c>. This is what keeps a
    /// hand-edited or unreadable cursor from costing somebody their triage.
    /// </summary>
    [Fact]
    public void Leaves_an_item_that_is_already_on_disk_alone()
    {
        var observation = new FeedbackObservation(
            [new ObservedPageComments(Path, "not a timestamp", [Comment("987654", FeedbackKind.Inline)])],
            BotAccount,
            new HashSet<string>(["10-domains-loans-README-987654.json"], StringComparer.OrdinalIgnoreCase));

        var plan = FeedbackInboxPlanner.Plan(observation);

        plan.Items.ShouldBeEmpty();
        plan.Skipped.Single().Reason.ShouldBe(FeedbackSkipReason.AlreadyOnDisk);
    }

    /// <summary>
    /// A comment id that cannot be written into a file name is reported rather than filed under a name
    /// that would overwrite a sibling's item. The id is remote input, so this is also the path-traversal
    /// boundary (CLAUDE.md §0.2).
    /// </summary>
    [Fact]
    public void Reports_a_comment_whose_id_cannot_be_written_to_a_file_name()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(Comment("../../", FeedbackKind.Footer)));

        plan.Items.ShouldBeEmpty();
        plan.Skipped.Single().Reason.ShouldBe(FeedbackSkipReason.UnusableId);
    }

    /// <summary>
    /// An unusable id does not move the cursor either: it is the one skip the run cannot settle, so
    /// letting the watermark pass it would bury it.
    /// </summary>
    [Fact]
    public void Does_not_advance_the_cursor_past_a_comment_it_could_not_file_at_all()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("..", FeedbackKind.Footer, createdAt: "2026-08-02T18:00:00.000Z")));

        plan.Cursors.ShouldBeEmpty();
    }

    /// <summary>
    /// Timestamps are normalized to UTC with milliseconds. Truncating to the second — the format the label
    /// reader writes — would put the cursor before the comment it was written from, and the next run would
    /// file that comment again.
    /// </summary>
    [Fact]
    public void Normalizes_timestamps_to_utc_and_keeps_the_milliseconds_the_cursor_needs()
    {
        var plan = FeedbackInboxPlanner.Plan(Observation(
            Comment("1", FeedbackKind.Footer, createdAt: "2026-08-02T16:11:00.123+02:00")));

        plan.Items[0].Item.CreatedAt.ShouldBe("2026-08-02T14:11:00.123Z");
        plan.Cursors.Single().Cursor.ShouldBe("2026-08-02T14:11:00.123Z");
    }

    /// <summary>Ordered by page then comment id, so two runs over one space read the same way.</summary>
    [Fact]
    public void Orders_the_plan_by_page_then_comment_id()
    {
        var observation = new FeedbackObservation(
            [
                new ObservedPageComments("20-guides/z.md", null, [Comment("9", FeedbackKind.Footer)]),
                new ObservedPageComments(
                    "10-domains/a.md",
                    null,
                    [Comment("2", FeedbackKind.Footer), Comment("1", FeedbackKind.Footer)]),
            ],
            BotAccount,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var plan = FeedbackInboxPlanner.Plan(observation);

        plan.Items.Select(item => item.FileName).ShouldBe([
            "10-domains-a-1.json",
            "10-domains-a-2.json",
            "20-guides-z-9.json",
        ]);
    }

    /// <summary>
    /// Apply touches nothing but the cursors, and only for pages state has an entry for: the inbox items
    /// are files, not state, and a cursor for a page state does not know would be bookkeeping about
    /// nothing.
    /// </summary>
    [Fact]
    public void Apply_writes_the_cursor_and_leaves_the_rest_of_the_page_state_alone()
    {
        var state = new DocumeState
        {
            Pages = new Dictionary<string, PageState>(StringComparer.Ordinal)
            {
                [Path] = new()
                {
                    PageId = "65601",
                    Title = "Loans Domain",
                    ContentHash = "sha256:abc",
                    PublishedVersion = 6,
                    FeedbackCursor = "2026-08-01T10:00:00.000Z",
                },
            },
        };

        var plan = FeedbackInboxPlanner.Plan(new FeedbackObservation(
            [
                new ObservedPageComments(
                    Path,
                    "2026-08-01T10:00:00.000Z",
                    [Comment("1", FeedbackKind.Footer, createdAt: "2026-08-02T13:00:00.000Z")]),
                new ObservedPageComments(
                    "never-published.md",
                    null,
                    [Comment("2", FeedbackKind.Footer, createdAt: "2026-08-02T14:00:00.000Z")]),
            ],
            BotAccount,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        var updated = FeedbackInboxPlanner.Apply(state, plan);

        updated.Pages[Path].FeedbackCursor.ShouldBe("2026-08-02T13:00:00.000Z");
        updated.Pages[Path].ContentHash.ShouldBe("sha256:abc");
        updated.Pages[Path].PublishedVersion.ShouldBe(6);
        updated.Pages.ShouldNotContainKey("never-published.md");
    }

    /// <summary>One page's worth of comments with no cursor and no items on disk.</summary>
    private static FeedbackObservation Observation(params ObservedComment[] comments)
        => new(
            [new ObservedPageComments(Path, null, comments)],
            BotAccount,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static ObservedComment Comment(
        string id,
        string kind,
        string? createdAt = "2026-08-02T14:11:00.000Z")
        => new(
            id,
            kind,
            HumanAccount,
            AuthorDisplayName: null,
            createdAt,
            "<p>A claim to verify.</p>",
            QuotedText: null,
            IsResolved: false);
}
