using DocuMe.Core.Tests.Cli;
using DocuMe.Core.Tests.Fixtures;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// <see cref="ShippedTree"/>'s root list held to the tree it describes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure this exists to catch.</strong> <c>ShippedTree.Roots</c> bounds the population of
/// both sweeps that ask what a shipped artifact promises — <see cref="Config.ConfigFieldSurfaceTests"/>'s
/// config-field scan and <see cref="PlanDataContractTests"/>'s dead-knob inventory — and each guards
/// itself with a single floor over the <em>union</em> of the eight roots: "no shipped artifact was
/// scanned at all" and "no shipped artifact names a single config field path". One floor over a union
/// cannot notice one entry leaving. Measured against HEAD: deleting <c>README.md</c> or
/// <c>CHANGELOG.md</c> from the literal moves neither floor and fires nothing else in the suite.
/// </para>
/// <para>
/// <strong>Why the other six looked safe and are not.</strong> They are caught today only by
/// <c>PlanDataContractTests.DeadFields</c>, whose <c>Places</c> arrays happen to name a file under each
/// of them, so dropping a root trips the "records these and none of them names it any more" direction.
/// That is coverage by coincidence, and it is scheduled for deletion: <c>Places</c> is an inventory of
/// two knobs whose settle instruction (<c>state.json → decisions.planSection5Deviations</c>, open) is to
/// strike them in all six places and empty <c>Places</c> in the same change. The day that decision is
/// answered, the accidental net disappears and all eight roots become deletable in silence.
/// </para>
/// <para>
/// The complement is declared rather than inferred because five of its entries genuinely name
/// config-shaped paths — <c>.claude</c>, <c>tools</c>, <c>GATES.md</c>, <c>docs/plans</c> and
/// <c>docs/specs</c> — so each of those exemptions removes a real finding, and admitting one would turn
/// the config sweep red on a loop state key that was never a <c>DocumeConfig</c> field.
/// </para>
/// <para>
/// Deliberately not asserted, so that reading nothing is a recorded choice rather than assumed
/// coverage: that every file type under a root is swept. Measured at iteration 192 — every file beneath
/// the eight roots already carries one of the five declared extensions, so the fact would guard nothing
/// today while adding friction to the next <c>.gitkeep</c>. <c>.yaml</c> matching no file is likewise
/// left alone: it is the dual spelling of <c>.yml</c>, and an exemption that removes no finding is worse
/// than the gap it papers over.
/// </para>
/// </remarks>
public sealed class ShippedTreeCoverageTests
{
    /// <summary>Directory names that hold no shipped artifact and would dominate the top-level walk.</summary>
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.Ordinal) { ".git", ".mtk", "bin", "obj", "node_modules" };

    [Fact]
    public void Every_declared_root_reaches_the_sweep()
    {
        // ONE FLOOR PER ROOT, NEVER ONE OVER THE SUM. Both consumers ask only whether the union came
        // back non-empty, which `docs/wiki` alone satisfies fourteen times over — so a root that stops
        // contributing is invisible to them and has to be caught here, entry by entry.
        ShippedTree.Roots.ShouldNotBeEmpty(
            "ShippedTree declares no roots at all, so both sweeps that bound their population by it "
            + "pass over the whole repository.");

        var barren = ShippedTree.Roots
            .Where(root => ShippedTree.FilesUnder(root).Count == 0)
            .ToList();

        barren.ShouldBeEmpty(
            $"These roots resolve but contribute no file to the sweep: {string.Join(", ", barren)}. "
            + "An entry that reaches nothing is an inventory claiming coverage it does not have — "
            + "either the directory emptied, or its files stopped carrying a swept extension.");
    }

    [Fact]
    public void Every_top_level_entry_is_a_root_or_declared_outside_the_question()
    {
        var classified = Population();

        // ANTI-VACUITY. Both directions below are set differences, so an enumerator that returned
        // nothing would report a clean top level while asserting that the repository is empty.
        const string empty = "The population walk found almost nothing, so this pairing is reading a "
            + "tree that is not this repository rather than finding it correctly classified.";

        classified.Count.ShouldBeGreaterThan(10, empty);

        var unclassified = classified
            .Where(entry => !IsRoot(entry))
            .Where(entry => !ShippedTree.OutsideTheQuestion.ContainsKey(entry))
            .Order(StringComparer.Ordinal)
            .ToList();

        unclassified.ShouldBeEmpty(
            $"[{string.Join(", ", unclassified)}] is neither a ShippedTree root nor declared outside "
            + "the question. Both sweeps bound their population by Roots and pass over everything else "
            + "without a word, so an unclassified artifact is exempt from both at once and exempt "
            + "silently. Add it to Roots, or declare why what it says is not a promise to a consumer.");

        var vanished = ShippedTree.OutsideTheQuestion.Keys
            .Where(entry => !Exists(entry))
            .Order(StringComparer.Ordinal)
            .ToList();

        vanished.ShouldBeEmpty(
            $"OutsideTheQuestion declares [{string.Join(", ", vanished)}], which is not in the tree. A "
            + "stale exemption suppresses nothing and hides that the top level changed.");

        var both = ShippedTree.OutsideTheQuestion.Keys
            .Where(IsRoot)
            .Order(StringComparer.Ordinal)
            .ToList();

        both.ShouldBeEmpty(
            $"[{string.Join(", ", both)}] is declared outside the question and swept as a root at the "
            + "same time. The declaration is the record of a decision, so it may not contradict it.");
    }

    /// <summary>
    /// Exactly what nothing else classifies: every top-level <em>file</em> the extension filter could
    /// read, the children of a partially covered directory, and the directories
    /// <see cref="DogfoodWikiTests.ShippedRoots"/> calls shipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Top-level directories at large are <see cref="DogfoodWikiTests"/>'s, not this class's. Repeating
    /// its walk here would be a second net that removes nothing — measured, an unclassified directory
    /// planted at iteration 192 was caught by that fact with this class reverted.
    /// </para>
    /// <para>
    /// A file the extension filter cannot reach is excluded structurally rather than by a judgement
    /// call: <c>.editorconfig</c> and <c>DocuMe.slnx</c> could not enter the sweep however they were
    /// classified. A directory holding a root is descended into, so <c>docs</c> resolves to
    /// <c>docs/plans</c>, <c>docs/specs</c> and the <c>docs/wiki</c> root rather than to one entry
    /// that is half covered.
    /// </para>
    /// </remarks>
    private static List<string> Population()
    {
        var collected = DogfoodWikiTests.ShippedRoots
            .Select(root => root.TrimEnd('/'))
            .ToList();

        foreach (var entry in Directory.EnumerateFileSystemEntries(DocumeCli.RepoRoot))
        {
            var name = Path.GetFileName(entry);

            if (SkippedDirectories.Contains(name))
            {
                continue;
            }

            if (File.Exists(entry))
            {
                if (ShippedTree.Extensions.Contains(Path.GetExtension(name), StringComparer.Ordinal))
                {
                    collected.Add(name);
                }

                continue;
            }

            if (Partial(name))
            {
                collected.AddRange(Directory
                    .EnumerateDirectories(entry)
                    .Select(child => $"{name}/{Path.GetFileName(child)}"));
            }
        }

        return collected.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether a root sits inside this directory rather than being it.</summary>
    private static bool Partial(string name) => ShippedTree.Roots
        .Any(root => root.StartsWith($"{name}/", StringComparison.Ordinal));

    private static bool IsRoot(string entry) =>
        ShippedTree.Roots.Contains(entry, StringComparer.Ordinal);

    private static bool Exists(string entry)
    {
        var absolute = Path.Combine(DocumeCli.RepoRoot, entry.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(absolute) || Directory.Exists(absolute);
    }
}
