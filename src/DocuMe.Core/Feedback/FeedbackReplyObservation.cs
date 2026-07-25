namespace DocuMe.Core.Feedback;

/// <summary>
/// One inbox item as it sits on disk, with the path it was read from.
/// </summary>
/// <remarks>
/// The path is carried rather than recomposed because the reply pass reads two directories — the inbox
/// and the archive (<see cref="FeedbackInbox.Read"/>) — and stamps the item back where it found it. A
/// name plus a "which directory" flag would be the same thing spelled less directly.
/// </remarks>
/// <param name="FilePath">The absolute path of the item file.</param>
/// <param name="Item">
/// The parsed item, or <c>null</c> when the file could not be read or parsed at all. Reported rather
/// than thrown: these are hand-editable committed files, and one bad item must not block the rest.
/// </param>
public sealed record StoredFeedbackItem(string FilePath, FeedbackItem? Item);

/// <summary>
/// A comment as it exists in the channel right now, at the moment the reply pass looked.
/// </summary>
/// <remarks>
/// <strong>Channel-neutral, like <see cref="ObservedComment"/>.</strong> The reply planner decides
/// against this shape rather than against a Confluence type, for the reason §9 gives: the inbox is the
/// seam, and a second intake channel should be able to answer "does this still exist, is it already
/// closed, can it be closed" without teaching the planner its own vocabulary.
/// </remarks>
/// <param name="Id">The channel's own comment id.</param>
/// <param name="Kind"><see cref="FeedbackKind.Inline"/> or <see cref="FeedbackKind.Footer"/>.</param>
/// <param name="IsResolved">Whether the channel already considers it closed.</param>
/// <param name="IsClosable">
/// Whether the channel would accept a request to close it. <c>false</c> for a Confluence comment whose
/// anchor is dangling, which its API refuses to update at all.
/// </param>
/// <param name="Version">
/// The channel's optimistic-lock token for the comment, or <c>null</c> when it answered none. Confluence
/// requires the current version incremented by one on a resolve, so a comment with no version can be
/// replied to but not closed.
/// </param>
public sealed record ObservedLiveComment(
    string Id,
    string Kind,
    bool IsResolved,
    bool IsClosable,
    int? Version);

/// <summary>
/// Everything <see cref="FeedbackReplyPlanner.Plan"/> needs: the items on disk, and what the channel
/// currently says about the comments they name (PLAN.md §9 step 5).
/// </summary>
/// <param name="Items">Every item read from the inbox and the archive, in file order.</param>
/// <param name="ReadPages">
/// The page paths whose live comments were actually read. An item about a page that is <em>not</em> in
/// here was never checked, which is a different fact from "the comment is gone" and is reported as such:
/// the usual cause is an item about a page state has no <c>pageId</c> for.
/// </param>
/// <param name="LiveComments">
/// The comments the channel answered, keyed by the channel's comment id. Absent for a page in
/// <see cref="ReadPages"/> means the comment itself is gone.
/// </param>
public sealed record FeedbackReplyObservation(
    IReadOnlyList<StoredFeedbackItem> Items,
    IReadOnlySet<string> ReadPages,
    IReadOnlyDictionary<string, ObservedLiveComment> LiveComments);
