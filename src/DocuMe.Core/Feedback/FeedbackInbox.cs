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
/// It never deletes and never moves. <c>/docs-feedback</c> moves processed items to
/// <c>_meta/feedback/archive/</c> (§5.4) and that move is a PR's business, not a CLI run's. It does
/// overwrite in exactly one place — <see cref="MarkReplied"/> stamps an item the reply pass has answered
/// — and never as part of ingestion, which is what keeps a triaged item's status safe from a re-read.
/// </para>
/// </remarks>
public static class FeedbackInbox
{
    /// <summary>Where inbox items live, relative to the wiki root (§5.4).</summary>
    public const string RelativeDirectory = "_meta/feedback/inbox";

    /// <summary>Where <c>/docs-feedback</c> moves an item once it has been triaged (§5.4).</summary>
    public const string RelativeArchiveDirectory = "_meta/feedback/archive";

    /// <summary>The last segment of <see cref="RelativeArchiveDirectory"/> — see <see cref="ArchiveBeside"/>.</summary>
    private const string ArchiveDirectoryName = "archive";

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
    /// The archive directory that belongs with <paramref name="inboxDirectory"/>: its sibling named
    /// <c>archive</c>.
    /// </summary>
    /// <remarks>
    /// Derived from the inbox rather than from the wiki root so that one rule covers both cases. For the
    /// default layout it produces exactly <see cref="RelativeArchiveDirectory"/>; for a run that relocated
    /// the inbox with <c>--output-dir</c> it keeps the pair together instead of reading an archive
    /// belonging to a different inbox.
    /// </remarks>
    public static string ArchiveBeside(string inboxDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inboxDirectory);

        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inboxDirectory));
        var parent = Path.GetDirectoryName(trimmed);

        // A path with no parent is either a filesystem root or a bare name; neither has a sibling, and
        // answering the inbox itself would make the reply pass read every item twice.
        return parent is { Length: > 0 }
            ? Path.Combine(parent, ArchiveDirectoryName)
            : Path.Combine(trimmed, ArchiveDirectoryName);
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

    /// <summary>
    /// Reads every item file in <paramref name="directories"/> that parses, ordered by file path, for the
    /// reply pass (PLAN.md §9 step 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both the inbox and the archive are read, and that is not belt-and-braces.</strong> §9 step
    /// 4 puts the archive move in the <em>same</em> PR as the triage, and step 5 runs after that PR
    /// merges — so by the time a reply is due, the item it answers is usually no longer in the inbox. A
    /// reply pass that read the inbox alone would find nothing and report success, which is the one
    /// failure mode worth engineering against here: a reviewer whose comment was fixed and never answered.
    /// Reading both also means the order of the move and the reply stops mattering at all.
    /// </para>
    /// <para>
    /// <strong>An unreadable item is skipped, not fatal.</strong> These are hand-editable committed files
    /// (§5.4); one with a typo in it must not stop the other forty from being answered. The caller reports
    /// what it could not read (<see cref="FeedbackReplySkipReason.Unreadable"/>) rather than this throwing.
    /// </para>
    /// </remarks>
    /// <param name="directories">Directories to read, in order. Ones that do not exist read as empty.</param>
    public static IReadOnlyList<StoredFeedbackItem> Read(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var items = new List<StoredFeedbackItem>();

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var files = Directory
                .EnumerateFiles(directory, $"*{FeedbackItemFile.Extension}")
                .OrderBy(file => file, StringComparer.Ordinal);

            foreach (var file in files)
            {
                items.Add(ReadItem(file));
            }
        }

        return items;
    }

    /// <summary>
    /// Rewrites <paramref name="stored"/> with <see cref="FeedbackItem.RepliedAt"/> set, in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Called per item, immediately after that item's reply lands</strong> — never batched at the
    /// end of a run. A process that dies after posting six of ten replies must leave six items stamped;
    /// stamping them together would re-post all six on the next run.
    /// </para>
    /// <para>
    /// <strong>The file is rewritten from the parsed item, so a member DocuMe does not know is
    /// dropped.</strong> §5.4 defines the shape and <see cref="FeedbackItem"/> carries all of it, so today
    /// there is nothing to lose; the note is here because a future channel adding a member would find out
    /// the hard way otherwise.
    /// </para>
    /// </remarks>
    /// <param name="stored">The item as it was read, with its path. Must have parsed.</param>
    /// <param name="repliedAt">When the reply was posted.</param>
    /// <exception cref="ArgumentException"><paramref name="stored"/> never parsed, so there is nothing to stamp.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void MarkReplied(StoredFeedbackItem stored, DateTimeOffset repliedAt)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (stored.Item is not { } parsed)
        {
            throw new ArgumentException(
                $"{stored.FilePath} did not parse, so there is no item to stamp as replied. Nothing plans "
                + "a reply for an unreadable item; reaching here means the plan and the read disagree.",
                nameof(stored));
        }

        var item = parsed with { RepliedAt = FeedbackTimestamp.Write(repliedAt) };
        var json = JsonSerializer.Serialize(item, ItemOptions);

        File.WriteAllText(stored.FilePath, json + Environment.NewLine);
    }

    private static StoredFeedbackItem ReadItem(string file)
    {
        try
        {
            var item = JsonSerializer.Deserialize<FeedbackItem>(File.ReadAllText(file), ItemOptions);

            return new StoredFeedbackItem(file, item);
        }
        catch (JsonException)
        {
            return new StoredFeedbackItem(file, Item: null);
        }
        catch (IOException)
        {
            return new StoredFeedbackItem(file, Item: null);
        }
    }
}
