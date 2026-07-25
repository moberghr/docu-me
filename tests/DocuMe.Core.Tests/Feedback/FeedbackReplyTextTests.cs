using DocuMe.Core.Feedback;
using Shouldly;

namespace DocuMe.Core.Tests.Feedback;

/// <summary>
/// The reply body (PLAN.md §9 step 5): the sentence each triage outcome gets, and the escaping that
/// keeps it valid storage format.
/// </summary>
public sealed class FeedbackReplyTextTests
{
    /// <summary>§9 step 5 quotes this sentence, so it is asserted verbatim rather than paraphrased.</summary>
    [Fact]
    public void Answers_a_fixed_item_with_plan_md_9s_own_sentence()
    {
        var body = FeedbackReplyText.Compose(FeedbackStatus.Fixed, null);

        body.ShouldStartWith("<p>Fixed in the latest version — thanks.</p>");
    }

    /// <summary>
    /// A rejected comment answered with "fixed" is a lie, and a question answered with "fixed" retires an
    /// open point that was never resolved. Each outcome says what actually happened.
    /// </summary>
    [Fact]
    public void Gives_each_triage_outcome_its_own_sentence()
    {
        var bodies = new[] { FeedbackStatus.Fixed, FeedbackStatus.Rejected, FeedbackStatus.Question }
            .Select(status => FeedbackReplyText.Compose(status, null))
            .ToList();

        bodies.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);
        bodies[1].ShouldContain("staying as it is");
        bodies[2].ShouldContain("_meta/GAPS.md");
    }

    /// <summary>The triage's reason reaches the reviewer as its own paragraph, when there is one.</summary>
    [Fact]
    public void Carries_the_triage_resolution_as_a_second_paragraph()
    {
        var body = FeedbackReplyText.Compose(
            FeedbackStatus.Rejected,
            "The Straumur integration is documented on the payments page instead.");

        body.ShouldContain(
            "<p>The Straumur integration is documented on the payments page instead.</p>");
    }

    /// <summary>An absent or blank resolution produces no empty paragraph.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Writes_no_paragraph_for_a_resolution_that_says_nothing(string? resolution)
    {
        var body = FeedbackReplyText.Compose(FeedbackStatus.Fixed, resolution);

        body.ShouldNotContain("<p></p>");
        body.Split("<p>").Length.ShouldBe(3); // the opening sentence and the signature
    }

    /// <summary>
    /// The resolution is free text written elsewhere, and storage format is XHTML: an unescaped
    /// <c>&lt;</c> in it produces a body Confluence rejects as malformed. This is about the markup, not
    /// about trust.
    /// </summary>
    [Fact]
    public void Escapes_markup_characters_in_the_resolution()
    {
        var body = FeedbackReplyText.Compose(
            FeedbackStatus.Fixed,
            "Corrected: disbursement is <1 minute & always instant.");

        body.ShouldContain("&lt;1 minute &amp; always instant.");
        body.ShouldNotContain("<1 minute");
    }

    /// <summary>A human reading the thread should be able to tell an automated answer from a colleague's.</summary>
    [Fact]
    public void Signs_the_reply_as_automated()
    {
        FeedbackReplyText.Compose(FeedbackStatus.Fixed, null).ShouldContain("<em>Posted automatically");
    }

    /// <summary>
    /// Only the three triage outcomes are answerable. <c>new</c> means nobody has looked at the item yet.
    /// </summary>
    [Theory]
    [InlineData(FeedbackStatus.Fixed, true)]
    [InlineData(FeedbackStatus.Rejected, true)]
    [InlineData(FeedbackStatus.Question, true)]
    [InlineData(FeedbackStatus.New, false)]
    [InlineData(null, false)]
    [InlineData("escalated", false)]
    public void Knows_which_statuses_are_answerable(string? status, bool triaged)
        => FeedbackReplyText.IsTriaged(status).ShouldBe(triaged);

    /// <summary>Composing an answer for an untriaged status is a bug, not a fallback.</summary>
    [Fact]
    public void Refuses_to_compose_an_answer_for_an_untriaged_status()
        => Should.Throw<ArgumentOutOfRangeException>(
            () => FeedbackReplyText.Compose(FeedbackStatus.New, null));
}
