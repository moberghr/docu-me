using System.Text.RegularExpressions;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// PLAN.md §13's spike table held to what the build already knows (PLAN.md §13; the table is the
/// spec's record of which design questions are still open).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure this exists to catch.</strong> A spike gets run, its answer is written into
/// the remarks of the type that acted on it, and nobody carries it back to the table — so the spec
/// keeps posing a question the build settled weeks ago. That is how S1, S2, S5 and S6 came to read
/// as open at M6 feature-complete: `ConfluenceClient` had recorded Dapplo's version, its `/rest/api`
/// root and why it fails §4, and the table still asked whether Dapplo would do. The cost is not
/// cosmetic — a reader planning work from §13 re-opens a decision that shipped code depends on.
/// </para>
/// <para>
/// <strong>Why "cited in <c>src/</c>" is the trigger.</strong> A citation is the build committing to
/// an answer: code that says "per spike S2's default" has acted, so the row owes the reader what was
/// decided. A brand-new spike nobody has built on yet may sit open — correctly, because it is open.
/// The invariant only bites the moment production code starts leaning on the outcome, which is
/// exactly when the table going stale starts costing something.
/// </para>
/// <para>
/// Recording an outcome does not mean the whole question is closed. S5 is settled in mechanism and
/// open in its numbers, S2's fallback ships while the affirmative answer waits for a real space;
/// both say so. What the table may not do is stay silent.
/// </para>
/// </remarks>
public sealed partial class SpikeTableTests
{
    /// <summary>
    /// The word a row uses to mark that the build settled something. Rows spell out which half when
    /// only a half is settled, so the marker is a floor rather than a claim of completeness.
    /// </summary>
    private const string OutcomeMarker = "SETTLED";

    /// <summary>Directory names that hold no source and would dominate the walk.</summary>
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.Ordinal) { "bin", "obj" };

    private const string UnparsedMessage =
        "PLAN.md §13's spike table parsed to fewer rows than the seven spikes it is known to "
        + "carry, so the section heading or the table formatting moved and these assertions are "
        + "no longer reading the table they name.";

    [Fact]
    public void The_spike_table_parses_into_rows()
    {
        // Anti-vacuity guard: every other assertion here reads the parsed table, so a renamed
        // heading or a reformatted table would turn them all green by finding nothing at all.
        var rows = SpikeRows();

        rows.Count.ShouldBeGreaterThanOrEqualTo(7, UnparsedMessage);
    }

    [Fact]
    public void Every_spike_the_code_cites_has_a_row_in_the_table()
    {
        var rows = SpikeRows();
        var undefined = CitedSpikes()
            .Where(cited => !rows.ContainsKey(cited.Key))
            .Select(cited => $"{cited.Key} (cited by {string.Join(", ", cited.Value.Order())})")
            .Order()
            .ToList();

        undefined.ShouldBeEmpty(
            "Production code cites a spike PLAN.md §13 does not define, so the reasoning behind "
            + "shipped behaviour points at a row that is not there. Add the row, or correct the "
            + $"citation. Undefined: {string.Join("; ", undefined)}");
    }

    [Fact]
    public void Every_spike_the_code_cites_records_an_outcome()
    {
        var rows = SpikeRows();
        var silent = CitedSpikes()
            .Where(cited => rows.TryGetValue(cited.Key, out var row)
                && !row.Contains(OutcomeMarker, StringComparison.Ordinal))
            .Select(cited => $"{cited.Key} (cited by {string.Join(", ", cited.Value.Order())})")
            .Order()
            .ToList();

        silent.ShouldBeEmpty(
            $"Production code acts on a spike whose PLAN.md §13 row still reads as an open "
            + $"question. A citation means the build committed to an answer, so the row owes the "
            + $"reader what was decided — mark it {OutcomeMarker}, naming which half if only a "
            + $"half is settled and who owns the rest. Silent: {string.Join("; ", silent)}");
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

    /// <summary>Spike ids cited by name in shipped source, each mapped to the files citing it.</summary>
    private static Dictionary<string, List<string>> CitedSpikes()
    {
        var source = Path.Combine(RepoRoot, "src");
        var cited = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoRoot, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(SkippedDirectories.Contains))
            {
                continue;
            }

            foreach (Match match in SpikeCitation().Matches(File.ReadAllText(file)))
            {
                var id = match.Groups["id"].Value.ToUpperInvariant();
                if (!cited.TryGetValue(id, out var files))
                {
                    files = [];
                    cited[id] = files;
                }

                if (!files.Contains(relative, StringComparer.Ordinal))
                {
                    files.Add(relative);
                }
            }
        }

        return cited;
    }

    /// <summary>A table row: <c>| S6 | … | … |</c>.</summary>
    [GeneratedRegex(@"^\|\s*(?<id>S\d+)\s*\|", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpikeRow();

    /// <summary>
    /// The two spellings the codebase uses to point at a spike: <c>spike S2</c> and <c>§13 S5</c>.
    /// A bare <c>S2</c> is deliberately not matched — it collides with ordinary prose.
    /// </summary>
    [GeneratedRegex(
        @"(?:spike|§13)\s+(?<id>S\d+)",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpikeCitation();

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
