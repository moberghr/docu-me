using System.Text.Encodings.Web;
using System.Text.Json;
using DocuMe.Core.Json;

namespace DocuMe.Core.Feedback;

/// <summary>
/// The inbox directory on disk: <c>&lt;wiki.root&gt;/_meta/feedback/inbox/</c> (PLAN.md §5.4).
/// </summary>
/// <remarks>
/// <para>
/// The only part of ingestion that touches a filesystem. Committing what it writes is deliberately not
/// its job (§6.3): the sync workflow commits the inbox and the state file together to a <c>docs/sync</c>
/// branch and opens a PR, so a human sees the feedback arrive before anything acts on it (§9's CI
/// posture).
/// </para>
/// <para>
/// It never deletes and never overwrites. <c>/docs-feedback</c> moves processed items to
/// <c>_meta/feedback/archive/</c> (§5.4) and that move is a PR's business, not a CLI run's.
/// </para>
/// </remarks>
public static class FeedbackInbox
{
    /// <summary>Where inbox items live, relative to the wiki root (§5.4).</summary>
    public const string RelativeDirectory = "_meta/feedback/inbox";

    /// <summary>Where <c>/docs-feedback</c> moves an item once it has been triaged (§5.4).</summary>
    public const string RelativeArchiveDirectory = "_meta/feedback/archive";

    /// <summary>
    /// <see cref="DocumeJson.Options"/> without the HTML escaping, because an inbox item is read by
    /// people.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default encoder writes a comment body of <c>&lt;p&gt;This is wrong&lt;/p&gt;</c> as
    /// <c><p>This is wrong</p></c> and a reviewer named Jónas as <c>Jónas</c>.
    /// Both are valid JSON and both defeat the reason §5.4 commits these files: a human reads them in a
    /// PR diff and decides whether the claim is worth acting on.
    /// </para>
    /// <para>
    /// <strong>The relaxed encoder is not a hole in the untrusted-input rule.</strong> What it stops
    /// escaping is <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and non-ASCII letters, which matters when JSON is
    /// pasted into an HTML page — and no part of DocuMe renders an inbox item into HTML, or into anything
    /// else. Quotes, backslashes and control characters are still escaped, so the file stays parseable
    /// whatever the body contains. What defends against a comment that tries to give instructions is the
    /// SKILL.md contract that treats it as a claim to verify (rule §1.3), never a character escape.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions ItemOptions = new(DocumeJson.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The absolute inbox path for <paramref name="wikiRoot"/>.</summary>
    public static string DirectoryFor(string wikiRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wikiRoot);

        return Path.Combine(wikiRoot, RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The item file names already in <paramref name="directory"/> — what stops a re-ingest from
    /// overwriting a triaged item (<see cref="FeedbackSkipReason.AlreadyOnDisk"/>).
    /// </summary>
    /// <remarks>
    /// A directory that does not exist yet reads as empty rather than failing: the first sync on a repo is
    /// the ordinary case, and <c>docume init</c> does not scaffold an inbox for a wiki with no feedback in
    /// it. Names are compared with <see cref="StringComparer.OrdinalIgnoreCase"/> because macOS and Windows
    /// would treat two casings as one file, and an inbox that behaved differently per platform would
    /// overwrite an item on exactly one of them.
    /// </remarks>
    public static IReadOnlySet<string> ExistingItemFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            return names;
        }

        foreach (var file in Directory.EnumerateFiles(directory, $"*{FeedbackItemFile.Extension}"))
        {
            names.Add(Path.GetFileName(file));
        }

        return names;
    }

    /// <summary>
    /// Writes every item in <paramref name="plan"/> into <paramref name="directory"/>, returning the file
    /// names written in the order they were written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Items first, state last.</strong> The caller advances the cursor after this returns
    /// (<see cref="FeedbackInboxPlanner.Apply"/>), so a run that dies halfway re-reads those comments next
    /// time rather than skipping past comments it never filed.
    /// </para>
    /// <para>
    /// Each file ends with a newline: these are committed text files, and a diff whose every hunk carries
    /// "\ No newline at end of file" is noise in the PR a human reviews.
    /// </para>
    /// </remarks>
    /// <exception cref="IOException">The directory or a file could not be written.</exception>
    public static IReadOnlyList<string> Write(string directory, FeedbackIngestPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Items.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(directory);

        var written = new List<string>(plan.Items.Count);

        foreach (var item in plan.Items)
        {
            var json = JsonSerializer.Serialize(item.Item, ItemOptions);
            File.WriteAllText(Path.Combine(directory, item.FileName), json + Environment.NewLine);
            written.Add(item.FileName);
        }

        return written;
    }
}
