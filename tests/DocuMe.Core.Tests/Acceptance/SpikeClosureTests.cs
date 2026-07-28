using System.Text.RegularExpressions;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// The docs a reader consults held to what PLAN.md §13 already records (§13 is the spec's register of
/// which design questions are still open).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure this exists to catch.</strong> <see cref="SpikeTableTests"/> guards the first
/// hop of the propagation: a spike is run, and the table learns the answer. This guards the next hop,
/// which went wrong for sixty-odd iterations. §13 recorded S1 settled while
/// <c>.claude/references/decisions.md</c> still carried the heading "pending spike S1" and
/// <c>architecture-principles.md</c> P4 still told the reader to weigh <c>Dapplo.Confluence</c>
/// before committing. Those two files are auto-loaded into every agent session, so a stale condition
/// there does not read as history — it reads as an instruction, and the work it invites is work the
/// build finished at iteration 27.
/// </para>
/// <para>
/// <strong>Why the row's own wording decides.</strong> Recording an outcome is not the same as
/// closing the question, so a row is treated as closed only when it marks an outcome *and* reserves
/// nothing: S2, S4 and S5 each say in the row which half is still open, and a doc calling those open
/// is agreeing with the spec rather than contradicting it. <c>docs/wiki/_meta/GAPS.md</c> calling S4
/// open is the case that must stay legal. The invariant bites only on rows that reserve nothing.
/// </para>
/// <para>
/// <strong>The one exemption.</strong> The decision log is append-only — that is its whole value, and
/// rewriting a 2026-07-24 entry to say what was learned in 2026-07-27 destroys it. So an entry that
/// carries a <c>**Superseded by:**</c> line is skipped: it is a dated record of what was decided
/// then, and the pointer is what makes it navigable. The exemption is scoped to that one file and
/// needs the marker, so it cannot be used to park a live claim.
/// </para>
/// </remarks>
public sealed partial class SpikeClosureTests
{
    /// <summary>The word a §13 row uses to mark that the build settled something.</summary>
    private const string OutcomeMarker = "SETTLED";

    /// <summary>The decision log's pointer from a superseded entry to the one that replaced it.</summary>
    private const string SupersededMarker = "**Superseded by:**";

    /// <summary>The append-only decision log, the one file where a superseded entry may stay as written.</summary>
    private const string DecisionLog = ".claude/references/decisions.md";

    /// <summary>Directory names that hold no prose and would dominate the walk.</summary>
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.Ordinal) { ".git", ".mtk", "bin", "obj", "node_modules" };

    /// <summary>
    /// Trees whose markdown a human or an agent reads to learn what DocuMe has decided. PLAN.md is
    /// deliberately absent — it holds the table these assertions read from, and
    /// <see cref="SpikeTableTests"/> owns it. <c>tools/loop/</c> is absent for the opposite reason:
    /// it is build-loop bookkeeping, and its handoff archive is a log of past phrasings that is
    /// supposed to preserve them. Both entries here and in <see cref="ReaderFacingFiles"/> are paired
    /// with the live tree by <see cref="Every_reader_facing_entry_reaches_the_sweep"/>: the walk below
    /// skips a root that no longer resolves without a word, and the citation floor cannot notice,
    /// because one root carries most of the citations in the repo.
    /// </summary>
    private static readonly string[] ReaderFacingRoots =
        [".claude/references", ".claude/rules", "docs", "plugin"];

    /// <summary>Top-level documents in the same class as <see cref="ReaderFacingRoots"/>.</summary>
    private static readonly string[] ReaderFacingFiles =
        ["CHANGELOG.md", "CLAUDE.md", "CODE_INDEX.md", "GATES.md", "README.md"];

    private const string UnparsedMessage =
        "PLAN.md §13's spike table parsed to fewer rows than the seven spikes it is known to "
        + "carry, so the section heading or the table formatting moved and these assertions are "
        + "no longer reading the table they name.";

    private const string UnclassifiedMessage =
        "No §13 row classified as closed, so the invariant below has nothing to compare against and "
        + $"would pass by finding nothing. Either the rows stopped spelling {OutcomeMarker}, or the "
        + "reservation wording changed and every row now looks like it is holding something open.";

    private const string UnscannedMessage =
        "The reader-facing sweep found almost no line citing a spike, so the invariant below would "
        + "pass by scanning nothing. Either ReaderFacingRoots no longer resolves to the docs tree, "
        + "or the two citation spellings changed.";

    private const string UnreachedMessage =
        "A declared reader-facing entry no longer reaches the sweep, so the tree it names is read by "
        + "nothing and the loss is silent: the enumerator skips a root that does not resolve and a "
        + "file that is not there without a word. The citation floor cannot stand in for this — one "
        + "root carries most of the citations in the repo, so blinding any other entry leaves that "
        + "floor comfortably green. Point the entry at where the tree went, or drop it deliberately. "
        + "Unreached: ";

    private const string DroppedRootMessage =
        "ReaderFacingRoots names fewer trees than the four it is known to carry, so this sweep's "
        + "coverage shrank. Deleting an entry is how a stale instruction stops being read while the "
        + "pairing above stays green by having nothing left to walk. If a tree genuinely left the "
        + "repo, lower this floor in the same change and say which one.";

    private const string DroppedFileMessage =
        "ReaderFacingFiles names fewer documents than the five it is known to carry. Same failure as "
        + "the roots floor, and the floors are separate on purpose: one floor over the sum would let "
        + "an entry moved from one list to the other pay for a deletion in the other.";

    private const string StaleMessage =
        "A reader-facing doc still poses a spike as an open question after PLAN.md §13's row for it "
        + "recorded the outcome and reserved nothing. These files are what a reader — and every "
        + "agent session, for the two under .claude/ — consults first, so the stale condition reads "
        + "as work still owed rather than as history. Carry the outcome across, or, if the question "
        + "genuinely re-opened, say so in the §13 row first. Stale: ";

    [Fact]
    public void The_spike_table_parses_into_rows()
    {
        // Anti-vacuity guard: everything below reads the parsed table, so a renamed heading or a
        // reformatted table would turn it all green by finding nothing at all.
        var rows = SpikeRows();

        rows.Count.ShouldBeGreaterThanOrEqualTo(7, UnparsedMessage);
    }

    [Fact]
    public void Some_spikes_classify_as_closed()
    {
        var closed = ClosedSpikes();

        // S1, S3, S6 and S7 reserve nothing today. The floor is deliberately below the count so a
        // spike legitimately re-opening does not fail this, while the set emptying does.
        closed.Count.ShouldBeGreaterThanOrEqualTo(4, UnclassifiedMessage);
    }

    [Fact]
    public void The_reader_facing_sweep_finds_spike_citations()
    {
        var citing = CitingLines();

        citing.Count.ShouldBeGreaterThanOrEqualTo(5, UnscannedMessage);
    }

    [Fact]
    public void Every_reader_facing_entry_reaches_the_sweep()
    {
        var swept = ReaderFacingDocuments()
            .Select(file => Path.GetRelativePath(RepoRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        var unreached = ReaderFacingFiles
            .Where(name => !swept.Contains(name, StringComparer.Ordinal))
            .Concat(ReaderFacingRoots.Where(root => !swept.Any(
                path => path.StartsWith(root + "/", StringComparison.Ordinal))))
            .Order()
            .ToList();

        unreached.ShouldBeEmpty(UnreachedMessage + string.Join(", ", unreached));
    }

    [Fact]
    public void The_reader_facing_declaration_still_names_every_tree()
    {
        // Anti-vacuity guard for the pairing above: it walks the declaration, so emptying the
        // declaration turns it green by leaving it nothing to check.
        ReaderFacingRoots.Length.ShouldBeGreaterThanOrEqualTo(4, DroppedRootMessage);
        ReaderFacingFiles.Length.ShouldBeGreaterThanOrEqualTo(5, DroppedFileMessage);
    }

    [Fact]
    public void No_reader_facing_doc_poses_a_closed_spike_as_open()
    {
        var closed = ClosedSpikes();
        var stale = CitingLines()
            .Where(line => Pending().IsMatch(line.Text))
            .Where(line => CitedIds(line.Text).Any(closed.Contains))
            .Select(line => $"{line.Location} ({string.Join(", ", CitedIds(line.Text).Order())})")
            .Order()
            .ToList();

        stale.ShouldBeEmpty(StaleMessage + string.Join("; ", stale));
    }

    /// <summary>
    /// §13's table, spike id to the full row text. Read from the heading rather than the whole file
    /// so a spike mentioned in prose elsewhere in PLAN.md cannot be mistaken for a table row.
    /// </summary>
    private static Dictionary<string, string> SpikeRows()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot, "PLAN.md"));
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var inSection = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                {
                    break;
                }

                inSection = line.StartsWith("## 13.", StringComparison.Ordinal);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var row = SpikeRow().Match(line);
            if (row.Success)
            {
                rows[row.Groups["id"].Value] = line;
            }
        }

        return rows;
    }

    /// <summary>
    /// Spikes whose row records an outcome and reserves nothing further. A row that says which half
    /// is still open is excluded, because a doc echoing that half is agreeing with the spec.
    /// </summary>
    private static HashSet<string> ClosedSpikes()
    {
        var closed = SpikeRows()
            .Where(row => row.Value.Contains(OutcomeMarker, StringComparison.Ordinal))
            .Where(row => !Reservation().IsMatch(row.Value))
            .Select(row => row.Key);

        return new HashSet<string>(closed, StringComparer.Ordinal);
    }

    /// <summary>Reader-facing lines that name a spike, each with a <c>path:line</c> location.</summary>
    private static List<(string Location, string Text)> CitingLines()
    {
        var citing = new List<(string Location, string Text)>();

        foreach (var file in ReaderFacingDocuments())
        {
            var relative = Path.GetRelativePath(RepoRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            var lines = File.ReadAllLines(file);
            var exempt = string.Equals(relative, DecisionLog, StringComparison.Ordinal)
                ? SupersededLines(lines)
                : [];

            for (var index = 0; index < lines.Length; index++)
            {
                if (exempt.Contains(index + 1) || !SpikeCitation().IsMatch(lines[index]))
                {
                    continue;
                }

                citing.Add(($"{relative}:{index + 1}", lines[index]));
            }
        }

        return citing;
    }

    /// <summary>1-based line numbers sitting inside a decision-log entry marked as superseded.</summary>
    private static HashSet<int> SupersededLines(string[] lines)
    {
        var superseded = new HashSet<int>();
        var entry = new List<int>();
        var marked = false;

        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("## ", StringComparison.Ordinal))
            {
                Close(superseded, entry, marked);
                entry.Clear();
                marked = false;
            }

            entry.Add(index + 1);
            marked |= lines[index].StartsWith(SupersededMarker, StringComparison.Ordinal);
        }

        Close(superseded, entry, marked);

        return superseded;
    }

    private static void Close(HashSet<int> superseded, List<int> entry, bool marked)
    {
        if (!marked)
        {
            return;
        }

        superseded.UnionWith(entry);
    }

    /// <summary>The spike ids a line names, uppercased so <c>s1</c> and <c>S1</c> are one spike.</summary>
    private static IEnumerable<string> CitedIds(string text) =>
        SpikeCitation()
            .Matches(text)
            .Select(match => match.Groups["id"].Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> ReaderFacingDocuments()
    {
        foreach (var name in ReaderFacingFiles)
        {
            var path = Path.Combine(RepoRoot, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        foreach (var root in ReaderFacingRoots)
        {
            var directory = Path.Combine(RepoRoot, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var segments = Path.GetRelativePath(RepoRoot, file)
                    .Split(Path.DirectorySeparatorChar);
                if (segments.Any(SkippedDirectories.Contains))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>A table row: <c>| S6 | … | … |</c>.</summary>
    [GeneratedRegex(@"^\|\s*(?<id>S\d+)\s*\|", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpikeRow();

    /// <summary>
    /// The two spellings the repo uses to point at a spike: <c>spike S2</c> and <c>§13 S5</c>. A bare
    /// <c>S2</c> is deliberately not matched — it collides with ordinary prose. Same pattern as
    /// <see cref="SpikeTableTests"/>, so both hops of the propagation read citations identically.
    /// </summary>
    [GeneratedRegex(
        @"(?:spike|§13)\s+(?<id>S\d+)",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpikeCitation();

    /// <summary>
    /// Wording by which a §13 row keeps part of its question for later. Kept narrow on purpose: it
    /// decides which rows the invariant may bite on, so a loose pattern here silently disarms it.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:open|not\s+settled)\b",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Reservation();

    /// <summary>
    /// Wording by which a doc tells the reader a question is still owed. Every alternative names an
    /// outstanding obligation; words that merely describe a past state ("was evaluated", "we chose")
    /// are left out, because the point is to catch a live instruction, not a history.
    /// </summary>
    /// <remarks>
    /// Bare <c>open</c> earns its place: <c>docs/wiki/_meta/GAPS.md</c> spells it "and open as spike
    /// S4", so a vocabulary of only "still open" / "open question" would have missed the commonest
    /// phrasing in this repo. Both hyphen forms are excluded, and each for its own reason —
    /// <c>open-comment</c> (as in §6.2's guard) is a compound noun naming a feature, and
    /// <c>re-open</c> is an instruction not to revisit, which is the opposite of an open question.
    /// </remarks>
    [GeneratedRegex(
        @"\b(?:pending|unresolved|tbd)\b"
        + @"|(?<!-)\bopen\b(?!-)"
        + @"|\bbefore\s+committing\b"
        + @"|\bnot\s+yet\s+(?:settled|decided|answered|run|evaluated)\b"
        + @"|\bto\s+be\s+(?:decided|determined|evaluated|answered)\b",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pending();

    private static string RepoRoot { get; } = Locate();

    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so PLAN.md cannot be found.");
    }
}
