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
    private static readonly string[] Roots =
    [
        "PLAN.md", "README.md", "CHANGELOG.md", "docume.json", "docs/wiki", "plugin", "templates", "schema",
    ];

    private static readonly string[] Extensions = [".md", ".json", ".yml", ".yaml", ".mjs"];

    /// <summary>Every shipped artifact, in no particular order.</summary>
    internal static List<ShippedFile> Files()
    {
        var collected = new List<ShippedFile>();

        foreach (var root in Roots)
        {
            var absolute = Path.Combine(DocumeCli.RepoRoot, root.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(absolute))
            {
                collected.Add(new ShippedFile(root, absolute));
                continue;
            }

            var missing = $"The shipped root '{root}' does not exist, so this inventory no longer covers "
                + "what it claims to cover.";

            Directory.Exists(absolute).ShouldBeTrue(missing);

            var files = Directory
                .EnumerateFiles(absolute, "*", SearchOption.AllDirectories)
                .Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.Ordinal))
                .Select(file => new ShippedFile(
                    Path.GetRelativePath(DocumeCli.RepoRoot, file).Replace(Path.DirectorySeparatorChar, '/'),
                    file));

            collected.AddRange(files);
        }

        return collected;
    }
}

/// <summary>A shipped artifact, keyed by the repo-relative path a failure message quotes.</summary>
internal sealed record ShippedFile(string Relative, string Absolute);
