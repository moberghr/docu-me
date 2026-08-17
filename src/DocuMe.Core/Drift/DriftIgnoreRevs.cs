namespace DocuMe.Core.Drift;

/// <summary>
/// The parsed <c>_meta/drift-ignore-revs</c> file: commits whose changes never mean the docs moved,
/// discarded whole from §6.4's drift attribution before any page's <c>sources</c> matching runs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why by commit and not by path.</strong> <c>drift-ignore</c> (the sibling file, see
/// <see cref="DriftExemptions"/>) says "changes to these paths are never drift", and for generated
/// or vendored trees that is the honest statement. A format sweep or a license-header pass touches
/// the very files the docs describe, and a path exemption there would outlive the sweep and swallow
/// the next real change to those files. Only the commit is safe to discard: this one change was
/// mechanical, everything before and after it still counts.
/// </para>
/// <para>
/// <strong>The format is git's own.</strong> One full 40-hex commit sha per line, comments behind
/// <c>#</c> (leading or trailing) — <c>blame.ignoreRevsFile</c>'s format as git itself reads it,
/// so the same file can serve both tools. Full shas only: matching is exact against the full sha
/// <c>git log</c> reports, so an abbreviation would sit in the file ignoring nothing while its
/// author believed the sweep was exempt; git refuses an abbreviation in this file too. Matching is
/// case-insensitive, because git prints object names lowercase but the file holds whatever a
/// human's tooling gave them to paste. A line that is anything else is refused at parse time with
/// its line number: an entry that silently never fires would sit in the file looking like
/// protection.
/// </para>
/// </remarks>
public sealed class DriftIgnoreRevs
{
    /// <summary>Ignores nothing: what a wiki without a <c>drift-ignore-revs</c> file gets.</summary>
    public static readonly DriftIgnoreRevs None = new([]);

    private readonly HashSet<string> _shas;

    private DriftIgnoreRevs(HashSet<string> shas) => _shas = shas;

    /// <summary>How many distinct commits the file names — a duplicated line counts once.</summary>
    public int Count => _shas.Count;

    /// <summary>
    /// Parses <c>drift-ignore-revs</c> text. Line-based, git's <c>blame.ignoreRevsFile</c>
    /// convention as git 2.54 actually reads it: a line whose first non-blank character is <c>#</c>
    /// is a comment, blank lines are skipped, a sha may carry a trailing <c> # reason</c>, and what
    /// remains must be exactly one full 40-character hex commit sha. Git tolerates all of those
    /// spellings and refuses only an abbreviation, and a file that git accepts but this parser
    /// fails would break the "one file serves both tools" promise in the direction that hurts.
    /// </summary>
    /// <exception cref="DriftIgnoreRevsFormatException">A line is not a full 40-hex commit sha.</exception>
    public static DriftIgnoreRevs Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var shas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var marker = line.IndexOf('#', StringComparison.Ordinal);
            var sha = (marker < 0 ? line : line[..marker]).Trim();

            if (!IsFullSha(sha))
            {
                throw new DriftIgnoreRevsFormatException(index + 1, line);
            }

            shas.Add(sha);
        }

        return shas.Count == 0 ? None : new DriftIgnoreRevs(shas);
    }

    /// <summary>
    /// Whether the file names <paramref name="sha"/>. Case-insensitive, because git prints object
    /// names lowercase but a human pastes whatever their tooling showed them, and an exemption that
    /// stops firing over its case is exactly the quiet failure this file must not have.
    /// </summary>
    public bool Ignores(string sha)
    {
        ArgumentNullException.ThrowIfNull(sha);

        return _shas.Contains(sha);
    }

    private static bool IsFullSha(string line) =>
        line.Length == 40 && line.All(char.IsAsciiHexDigit);
}

/// <summary>
/// Thrown when a <c>drift-ignore-revs</c> line holds anything but a full 40-character commit sha:
/// an abbreviation, a reason trailing the sha, or an indented <c>#</c>. Refused at parse time
/// rather than loaded, because matching is exact against the full sha and an entry that can never
/// match is worse than none: the file reads as protection that does not exist.
/// </summary>
/// <remarks>
/// The message names the line but not the file: the one caller with a path prefixes it, and a
/// message that named the file too would print it twice.
/// </remarks>
public sealed class DriftIgnoreRevsFormatException(int lineNumber, string line)
    : Exception($"line {lineNumber} is not a full 40-character commit sha: \"{line}\". One full sha per line, optionally followed by a '#' comment; abbreviations are refused, by git too.")
{
    public int LineNumber { get; } = lineNumber;

    /// <summary>The offending line, trimmed — as quoted in the message, not as written in the file.</summary>
    public string Line { get; } = line;
}
