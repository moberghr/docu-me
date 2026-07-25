using System.Globalization;
using DocuMe.Core.Confluence;
using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// The open-comment guard's two jobs (PLAN.md §6.2 step 6): deciding which comments count, and saying so
/// in a sentence a human can act on. Both are pure, so neither needs a server.
/// </summary>
public sealed class OpenCommentGuardTests
{
    /// <summary>
    /// The asymmetry the guard is built on: only <c>resolved</c> closes a comment, so an unknown status and
    /// a missing one both survive. Over-reporting costs a warning; under-reporting silently strands a
    /// reviewer's question.
    /// </summary>
    [Fact]
    public void Keeps_every_comment_confluence_does_not_call_resolved()
    {
        var comments = new[]
        {
            Comment("1", "open"),
            Comment("2", "resolved"),
            Comment("3", "dangling"),
            Comment("4", status: null),
            Comment("5", "something-new"),
        };

        OpenCommentGuard.Unresolved(comments)
            .Select(comment => comment.Id)
            .ShouldBe(["1", "3", "4", "5"]);
    }

    /// <summary>Case is Confluence's business, not a difference in meaning.</summary>
    [Theory]
    [InlineData("resolved")]
    [InlineData("Resolved")]
    [InlineData("RESOLVED")]
    public void Reads_a_resolved_status_whatever_case_it_arrives_in(string status)
        => OpenCommentGuard.Unresolved([Comment("1", status)]).ShouldBeEmpty();

    [Fact]
    public void Keeps_the_order_confluence_listed_the_comments_in()
        => OpenCommentGuard.Unresolved([Comment("9", "open"), Comment("1", "open")])
            .Select(comment => comment.Id)
            .ShouldBe(["9", "1"]);

    /// <summary>
    /// The warning names the page (warnings are printed on their own), the count, the comment, where to
    /// open it, and the flag that turns this into a refusal.
    /// </summary>
    [Fact]
    public void Warns_with_the_page_the_count_and_where_to_find_the_comment()
    {
        var warning = OpenCommentGuard.Warning("docs/wiki/domains/loans.md", [Comment("4001", "open")]);

        warning.ShouldStartWith("docs/wiki/domains/loans.md has 1 unresolved inline comment(s)");
        warning.ShouldContain("4001 (open) /spaces/DOCUMESBX/pages/1/x?focusedCommentId=4001");
        warning.ShouldContain("--block-on-open-comments");
    }

    /// <summary>
    /// The refusal reads as the continuation of a sentence naming the page, because that is how a publish
    /// failure is reported, and it names both ways out: resolve the comments, or drop the flag.
    /// </summary>
    [Fact]
    public void Refuses_with_the_flag_that_caused_it_and_both_ways_out()
    {
        var refusal = OpenCommentGuard.Refusal([Comment("4001", "dangling")]);

        refusal.ShouldStartWith("it has 1 unresolved inline comment(s)");
        refusal.ShouldContain("--block-on-open-comments was passed");
        refusal.ShouldContain("Resolve them in Confluence and re-run");
        refusal.ShouldContain("4001 (dangling)");
    }

    /// <summary>A comment with no link is still worth naming; the id is what finds it.</summary>
    [Fact]
    public void Names_a_comment_that_arrived_without_a_link()
    {
        var comment = new ConfluenceInlineComment("4001", "open", WebUiLink: null);

        OpenCommentGuard.Warning("a.md", [comment]).ShouldContain("4001 (open).");
    }

    [Fact]
    public void Says_a_comment_carried_no_status_rather_than_leaving_a_gap()
        => OpenCommentGuard.Warning("a.md", [Comment("4001", status: null)])
            .ShouldContain("4001 (no resolution status)");

    /// <summary>
    /// A capped list says what it dropped. A message that listed five of nine and stopped would read as
    /// nine having been five.
    /// </summary>
    [Fact]
    public void Says_how_many_comments_it_did_not_list()
    {
        var comments = Enumerable.Range(1, 9)
            .Select(id => Comment(id.ToString(CultureInfo.InvariantCulture), "open"))
            .ToArray();

        var warning = OpenCommentGuard.Warning("a.md", comments);

        warning.ShouldContain("9 unresolved inline comment(s)");
        warning.ShouldContain("… and 4 more.");
        warning.ShouldContain("5 (open)");
        warning.ShouldNotContain("6 (open)");
    }

    /// <summary>
    /// Neither message has a meaning for a page with nothing wrong with it, so an empty list is a caller
    /// that skipped <see cref="OpenCommentGuard.Unresolved"/> — a bug, not a run-time condition.
    /// </summary>
    [Fact]
    public void Refuses_to_write_a_message_about_no_comments()
    {
        Should.Throw<ArgumentException>(() => OpenCommentGuard.Warning("a.md", []));
        Should.Throw<ArgumentException>(() => OpenCommentGuard.Refusal([]));
    }

    private static ConfluenceInlineComment Comment(string id, string? status) =>
        new(id, status, $"/spaces/DOCUMESBX/pages/1/x?focusedCommentId={id}");
}
