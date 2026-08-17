using Microsoft.Extensions.FileSystemGlobbing;

namespace DocuMe.Core.Drift;

/// <summary>
/// The parsed <c>_meta/drift-ignore</c> file: globs naming the changes that never mean the docs
/// moved, held out of every page's <c>sources</c> matching (PLAN.md §6.4) by
/// <see cref="DriftPlanner"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why one wiki-level file and not frontmatter.</strong> A codegen sweep or a vendored-file
/// bump touches paths that many pages legitimately declare in <c>sources</c> (§5.2), and the
/// statement "this class of change is never drift" is about the change, not about any one page.
/// Frontmatter would say it once per page and miss the page added next week; the file says it once
/// for all of them, and a page's own <c>sources</c> stay an honest list of what it derives from.
/// </para>
/// <para>
/// <strong>The format is the smallest one that can carry a reason.</strong> One glob per line, a
/// line whose first character is <c>#</c> is a comment, and a pattern may trail <c> # why</c>,
/// because six months on the question is never "what is exempt" (the report says so) but "why did
/// we decide that was safe". The globs are built by <see cref="DriftPlanner.BuildMatcher"/>, the
/// exact construction <c>sources</c> patterns get, so an exemption and the match it exists to
/// cancel can never disagree about what <c>**</c> means, which slash counts, or whether case does.
/// A line no glob survives on, an indented reason with no pattern or a bare <c>/</c> that
/// normalization leaves empty, is refused at parse time with its line number: an exemption that
/// silently never fires would sit in the file looking like protection.
/// </para>
/// </remarks>
public sealed class DriftExemptions
{
    /// <summary>Matches nothing: what a wiki without a <c>drift-ignore</c> file gets.</summary>
    public static readonly DriftExemptions None = new([]);

    private readonly IReadOnlyList<Entry> _entries;

    private DriftExemptions(IReadOnlyList<Entry> entries) => _entries = entries;

    /// <summary>
    /// Parses <c>drift-ignore</c> text. Line-based, the gitignore convention: <c>#</c> opening a
    /// line is a comment, blank lines are skipped, and a pattern line is a glob optionally followed
    /// by whitespace, <c>#</c>, and a reason, both trimmed. An indented <c>#</c> does not comment
    /// the line out; it is a reason with no pattern before it, and that is refused.
    /// </summary>
    /// <exception cref="DriftIgnoreFormatException">A line's pattern can never match a file.</exception>
    public static DriftExemptions Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<Entry>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
            {
                continue;
            }

            var marker = ReasonMarkerIndex(line);
            var pattern = (marker < 0 ? line : line[..marker]).Trim();
            var reason = marker < 0 ? null : NullIfEmpty(line[(marker + 1)..].Trim());

            if (pattern.Length == 0 || DriftPlanner.NormalizePattern(pattern).Length == 0)
            {
                throw new DriftIgnoreFormatException(index + 1, line.Trim());
            }

            entries.Add(new Entry(pattern, reason, DriftPlanner.BuildMatcher(pattern)));
        }

        return entries.Count == 0 ? None : new DriftExemptions(entries);
    }

    /// <summary>
    /// The first pattern claiming <paramref name="changedFile"/>, with the file filled in as
    /// <see cref="ExemptedChange.Path"/>, or null when no pattern does. First match rather than
    /// most specific: the file reads top to bottom, and the report should quote the line its
    /// author would point at.
    /// </summary>
    public ExemptedChange? Match(string changedFile)
    {
        ArgumentNullException.ThrowIfNull(changedFile);

        foreach (var entry in _entries)
        {
            if (entry.Matcher.Match(changedFile).HasMatches)
            {
                return new ExemptedChange(changedFile, entry.Pattern, entry.Reason);
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>#</c> that opens a trailing reason: the first one with whitespace before it, so a
    /// glob may itself contain <c>#</c> (a legal path character) without losing its tail. Starts
    /// at index 1 because a <c>#</c> at index 0 already made the line a comment.
    /// </summary>
    private static int ReasonMarkerIndex(string line)
    {
        for (var index = 1; index < line.Length; index++)
        {
            if (line[index] == '#' && char.IsWhiteSpace(line[index - 1]))
            {
                return index;
            }
        }

        return -1;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed record Entry(string Pattern, string? Reason, Matcher Matcher);
}

/// <summary>One changed file an exemption held out of drift matching.</summary>
/// <param name="Path">
/// The changed file in the planner's normalized spelling (trimmed, forward slashes) — the same
/// spelling every other part of the report uses, not necessarily the diff's own.
/// </param>
/// <param name="Pattern">The exempting glob exactly as <c>drift-ignore</c> spells it, not as normalized.</param>
/// <param name="Reason">The trailing comment on the pattern's line, when its author left one.</param>
public sealed record ExemptedChange(string Path, string Pattern, string? Reason);

/// <summary>
/// Thrown when a <c>drift-ignore</c> line holds a pattern that can never match a file: a reason
/// with nothing before its <c>#</c> but whitespace, or a pattern normalization leaves empty (a
/// bare <c>/</c>). Refused at parse time rather than loaded, because an exemption that silently
/// never fires is worse than none: the file reads as protection that does not exist.
/// </summary>
/// <remarks>
/// The message names the line but not the file: the one caller with a path prefixes it, and a
/// message that named the file too would print it twice.
/// </remarks>
public sealed class DriftIgnoreFormatException(int lineNumber, string line)
    : Exception($"line {lineNumber} holds no usable pattern: \"{line}\". A pattern must name at least one path segment; a comment's '#' must open its line.")
{
    public int LineNumber { get; } = lineNumber;

    /// <summary>The offending line, trimmed — as quoted in the message, not as written in the file.</summary>
    public string Line { get; } = line;
}
