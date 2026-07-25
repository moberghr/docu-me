using DocuMe.Core.Confluence;

namespace DocuMe.Core.Publishing;

/// <summary>
/// The open-comment guard (PLAN.md §6.2 step 6): which of a page's inline comments count as unresolved,
/// and what to say about them before its body is overwritten.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the guard is protecting.</strong> An inline comment is anchored to a span of text inside
/// the stored body. A republish rewrites that body, and Confluence re-attaches a comment by matching the
/// text around it — so an update can leave a comment anchored to nothing (Confluence's own word for that
/// state is <c>dangling</c>), with a reviewer's question still unanswered and no longer attached to
/// anything a reader will find.
/// </para>
/// <para>
/// <strong>Warning is the default, and that is a decision rather than a leniency.</strong> §6.2 puts the
/// feedback loop (§9) in charge of comments: <c>docume sync --comments</c> ingests them, a human answers
/// them in the repo, and the answer arrives as a republish. A publish that refused to run until every
/// comment was resolved would block the very mechanism that resolves them.
/// <c>--block-on-open-comments</c> exists for teams that want the opposite trade and say so.
/// </para>
/// <para>
/// <strong>No comment text is quoted, deliberately.</strong> Confluence comment bodies are untrusted
/// input (CLAUDE.md §0.2, rule §1.3), and a terminal or a CI log is a poor place to render prose a
/// stranger wrote. The id and the browser link are enough to go read the comment where it lives, and the
/// read does not ask for bodies at all (<see cref="ConfluenceClient.GetInlineCommentsAsync"/>).
/// </para>
/// <para>
/// Pure by construction: the network call belongs to <see cref="PublishExecutor"/>, so every message
/// here is testable without a server, and the guard cannot quietly change what a run does.
/// </para>
/// </remarks>
public static class OpenCommentGuard
{
    /// <summary>How many comments a message names before it summarizes the rest.</summary>
    private const int CommentsListed = 5;

    /// <summary>
    /// The comments that hold a page back or warn about it: everything Confluence does not call
    /// resolved, in the order it listed them (<see cref="ConfluenceInlineComment.IsResolved"/>).
    /// </summary>
    public static IReadOnlyList<ConfluenceInlineComment> Unresolved(
        IReadOnlyList<ConfluenceInlineComment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);

        return [.. comments.Where(comment => !comment.IsResolved)];
    }

    /// <summary>
    /// What a run says about a page it published over unresolved comments. Starts with the path, like
    /// every other publish warning, because warnings are printed on their own.
    /// </summary>
    /// <param name="path">The wiki-root-relative markdown path.</param>
    /// <param name="unresolved">The comments, from <see cref="Unresolved"/>; never empty.</param>
    public static string Warning(string path, IReadOnlyList<ConfluenceInlineComment> unresolved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RequireAny(unresolved);

        return $"{path} has {unresolved.Count} unresolved inline comment(s), and rewriting its body may "
            + "leave them anchored to text that is gone. The page published anyway: answering comments "
            + "through the repo is the designed round trip (§9). Pass --block-on-open-comments to hold "
            + $"pages like this back instead. {Summarize(unresolved)}";
    }

    /// <summary>
    /// Why <c>--block-on-open-comments</c> left a page alone. Reads as the continuation of a sentence
    /// naming the page, which is how a publish failure is reported.
    /// </summary>
    /// <param name="unresolved">The comments, from <see cref="Unresolved"/>; never empty.</param>
    public static string Refusal(IReadOnlyList<ConfluenceInlineComment> unresolved)
    {
        RequireAny(unresolved);

        return $"it has {unresolved.Count} unresolved inline comment(s) and --block-on-open-comments was "
            + "passed, so its body was left as Confluence holds it rather than risking their text "
            + "anchors. Resolve them in Confluence and re-run, or drop --block-on-open-comments to "
            + $"publish over them. {Summarize(unresolved)}";
    }

    /// <summary>The comments a message names, capped — with the number it dropped, never silently.</summary>
    private static string Summarize(IReadOnlyList<ConfluenceInlineComment> unresolved)
    {
        var listed = string.Join("; ", unresolved.Take(CommentsListed).Select(Describe));
        var rest = unresolved.Count - CommentsListed;

        return rest > 0 ? $"Comments: {listed}; … and {rest} more." : $"Comments: {listed}.";
    }

    /// <summary>
    /// One comment: its id, the status Confluence reported for it, and where to open it. The status is
    /// passed through verbatim rather than translated, so a value DocuMe does not know about still
    /// arrives at the human who can look it up.
    /// </summary>
    private static string Describe(ConfluenceInlineComment comment)
    {
        var status = comment.ResolutionStatus is { Length: > 0 } reported
            ? reported
            : "no resolution status";

        return comment.WebUiLink is { Length: > 0 } link
            ? $"{comment.Id} ({status}) {link}"
            : $"{comment.Id} ({status})";
    }

    /// <summary>
    /// Guards against a message that would read "0 unresolved inline comment(s)". An empty list here is
    /// a caller that skipped <see cref="Unresolved"/>, not a page with nothing to say about it.
    /// </summary>
    private static void RequireAny(IReadOnlyList<ConfluenceInlineComment> unresolved)
    {
        ArgumentNullException.ThrowIfNull(unresolved);

        if (unresolved.Count == 0)
        {
            throw new ArgumentException(
                "There is no open-comment message for a page with no unresolved comments; check "
                + $"{nameof(Unresolved)} first.",
                nameof(unresolved));
        }
    }
}
