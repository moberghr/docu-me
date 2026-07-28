using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Fixtures;

/// <summary>
/// The files a consumer or an agent reads, repo-relative with forward slashes — the scope every
/// tree-wide claim sweep runs over.
/// </summary>
/// <remarks>
/// <para>
/// Single-sourced rather than restated per sweep. Two classes hold inventories over "what ships"
/// (<c>PlanDataContractTests</c> for the dead config knobs, <c>ConfigFieldSurfaceTests</c> for the live
/// ones); if their root lists drifted apart, both would keep passing while covering different trees, and
/// an inventory that quietly narrows is worse than none.
/// </para>
/// <para>
/// Rooted at <c>docs/wiki</c> rather than <c>docs</c> so the dated plan records under <c>docs/plans/</c>
/// are out of scope structurally, not by a filter a later edit can drop: they are history, and correcting
/// a promise must not mean rewriting what was planned on the day. <c>src/</c> is excluded too — a claim
/// there is code, and the suite holds code against behaviour elsewhere.
/// </para>
/// </remarks>
internal static class ShippedTree
{
    /// <summary>The trees and files every claim sweep runs over.</summary>
    /// <remarks>
    /// Internal because the literal bounding a sweep has to be paired with the tree it describes, and
    /// nothing did that until <c>ShippedTreeCoverageTests</c>: both consumers guard with a single floor
    /// over the union of these roots, so an entry deleted here narrowed both sweeps at once without
    /// moving either floor.
    /// </remarks>
    internal static readonly string[] Roots =
    [
        "PLAN.md", "README.md", "CHANGELOG.md", "docume.json", "docs/wiki", "plugin", "templates", "schema",
    ];

    /// <summary>The file types a claim can be written in.</summary>
    internal static readonly string[] Extensions = [".md", ".json", ".yml", ".yaml", ".mjs"];

    /// <summary>
    /// The complement, over exactly the population nothing else classifies: top-level <em>files</em>,
    /// the children of a partially covered directory, and the directories
    /// <c>DogfoodWikiTests.ShippedRoots</c> calls shipped that this inventory does not read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a claim that these say nothing about configuration — three of them do, measured — but that
    /// what they say is build-loop bookkeeping or dated history rather than a promise to a consumer.
    /// Those exemptions are load-bearing: admitting one turns the config sweep red on a loop state key
    /// that was never a <c>DocumeConfig</c> field.
    /// </para>
    /// <para>
    /// Top-level <em>directories</em> are deliberately absent. <c>DogfoodWikiTests</c> already holds
    /// every one of them to <c>ShippedRoots</c> or <c>UnshippedRoots</c> — measured at iteration 192,
    /// where a planted unclassified directory was caught by that fact and would have been caught twice
    /// had this list repeated it. A directory it calls unshipped is already a written classification,
    /// so only the ones it calls shipped need an answer here.
    /// </para>
    /// </remarks>
    internal static readonly Dictionary<string, string> OutsideTheQuestion = new(StringComparer.Ordinal)
    {
        [".claude-plugin"] = "shipped: the marketplace manifest a consumer resolves. It names no "
            + "config field and no dead knob, so reading it would add nothing.",
        ["actions"] = "shipped: the composite action. Names no config field and no dead knob.",
        ["src"] = "shipped: a claim here is code, and the suite holds code against behaviour elsewhere.",
        ["docs/plans"] = "dated implementation records. CARRIES A CLAIM, and correcting a promise must "
            + "not mean rewriting what was planned on the day.",
        ["docs/specs"] = "dated specifications, excluded for the same reason as docs/plans. CARRIES A CLAIM.",
        ["CLAUDE.md"] = "engineering standards for agents working on DocuMe, not on a consumer's repo.",
        ["CODE_INDEX.md"] = "a generated map of this repository's own source.",
        ["GATES.md"] = "human-gate instructions for Mirko. CARRIES A CLAIM: three `confluence.*` loop "
            + "state keys, none of them a DocumeConfig field.",
        ["global.json"] = "the pinned SDK and test runner — toolchain, not product configuration.",
        ["package.json"] = "the mermaid renderer's npm dependency.",
        ["package-lock.json"] = "the resolved lock for the above.",
    };

    /// <summary>Every shipped artifact, in no particular order.</summary>
    internal static List<ShippedFile> Files() => Roots.SelectMany(FilesUnder).ToList();

    /// <summary>
    /// What one root contributes. Split out of <see cref="Files"/> so the per-root reach fact asks the
    /// sweep's own enumerator rather than a second walk that could drift from it.
    /// </summary>
    internal static List<ShippedFile> FilesUnder(string root)
    {
        var absolute = Path.Combine(DocumeCli.RepoRoot, root.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(absolute))
        {
            return [new ShippedFile(root, absolute)];
        }

        var missing = $"The shipped root '{root}' does not exist, so this inventory no longer covers "
            + "what it claims to cover.";

        Directory.Exists(absolute).ShouldBeTrue(missing);

        return Directory
            .EnumerateFiles(absolute, "*", SearchOption.AllDirectories)
            .Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.Ordinal))
            .Select(file => new ShippedFile(
                Path.GetRelativePath(DocumeCli.RepoRoot, file).Replace(Path.DirectorySeparatorChar, '/'),
                file))
            .ToList();
    }
}

/// <summary>A shipped artifact, keyed by the repo-relative path a failure message quotes.</summary>
internal sealed record ShippedFile(string Relative, string Absolute);
