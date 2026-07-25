using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shouldly;

namespace DocuMe.Core.Tests.Packaging;

/// <summary>
/// Every place in the tree that names this repository, held to the one spelling
/// <c>plugin/.claude-plugin/plugin.json</c> declares.
/// </summary>
/// <remarks>
/// <para>
/// The owner is <c>moberghr</c> and <c>moberg</c> is a real Moberg org, so the wrong slug is a plausible
/// string that resolves to nothing. It has shipped twice: the <c>$schema</c> URL <c>docume init</c>
/// writes into every consumer's <c>docume.json</c> (iter67, pinned by
/// <see cref="Config.ConfigSchemaTests"/>), and the <c>actions/checkout</c> step in <c>docs-refresh.yml</c>
/// and <c>docs-feedback.yml</c> that fetches the plugin (iter80, pinned by
/// <see cref="Templates.WorkflowTemplateTests"/>). Both were caught by a person reading a line, and both
/// failed the same way: nowhere near the commit that introduced them, in somebody else's repository.
/// </para>
/// <para>
/// So this is the sweep the two targeted tests cannot be: it does not know which files are allowed to
/// name the repository, only that whichever do must agree. A third instance in a file nobody has thought
/// of fails here.
/// </para>
/// </remarks>
public sealed partial class RepositorySlugTests
{
    /// <summary>The repository name, as the second segment of every slug this checks.</summary>
    private const string Repository = "docu-me";

    /// <summary>Directory names holding no authored text, or too much of it to be worth walking.</summary>
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.Ordinal) { ".git", ".mtk", "bin", "obj", "node_modules" };

    /// <summary>
    /// The two trees that quote the wrong slug on purpose: the tests that pin it have to name what they
    /// caught, and the loop's own state file records Mirko's authorization in his words. Both are read by
    /// people, neither is copied into a consumer repo, and a sweep that failed on them would push the next
    /// editor to describe the bug less precisely.
    /// </summary>
    private static readonly string[] SkippedTrees = ["tests/", "tools/"];

    /// <summary>Extensions carrying text a consumer or a runner acts on.</summary>
    private static readonly HashSet<string> ScannedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".yml", ".yaml", ".json", ".props", ".targets", ".mjs", ".js", ".cs", ".slnx", ".sh",
        };

    [Fact]
    public void Every_reference_to_this_repository_names_the_owner_the_plugin_manifest_declares()
    {
        var declared = DeclaredOwner();
        var references = References().ToList();

        // Vacuous-pass guards. The walk finding nothing would pass this test while proving nothing, and
        // both floors sit below what the tree carries today: 155 files scanned, 20 references across 13
        // of them, measured at iter80 (.mtk/paths-80/measure-slugs.mjs runs the same walk).
        references.ShouldNotBeEmpty("The walk found no reference to this repository at all.");
        references
            .Select(reference => reference.File)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBeGreaterThan(8, "The walk is reading fewer files than the tree has.");

        var wrong = references
            .Where(reference => !string.Equals(reference.Owner, declared, StringComparison.Ordinal))
            .Select(reference => $"{reference.File}:{reference.Line} → {reference.Owner}/{Repository}")
            .ToList();

        wrong.ShouldBeEmpty(
            $"plugin.json declares the owner `{declared}`. A reference naming another one points at a "
                + "repository that does not exist, and it fails in a consumer's runner or editor rather "
                + "than here. Offenders:");
    }

    /// <summary>
    /// Every <c>owner/docu-me</c> in the scanned tree, with where it was found.
    /// </summary>
    /// <remarks>
    /// Whole-file rather than code-spans-only, unlike <see cref="QuickstartTests"/>: prose that names the
    /// repository is telling a reader where to look, so a wrong owner in a sentence is as dead as one in
    /// a fence. The two trees that discuss the wrong spelling are excluded by path instead.
    /// </remarks>
    private static IEnumerable<Reference> References()
    {
        foreach (var file in ScannedFiles())
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot, file));

            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var match in Slug().Matches(lines[index]).Cast<Match>())
                {
                    yield return new Reference(file, index + 1, match.Groups["owner"].Value);
                }
            }
        }
    }

    private static List<string> ScannedFiles()
    {
        var files = new List<string>();
        Walk(new DirectoryInfo(RepoRoot), string.Empty, files);

        return files
            .Where(file => ScannedExtensions.Contains(Path.GetExtension(file)))
            .Where(file => !SkippedTrees.Any(tree => file.StartsWith(tree, StringComparison.Ordinal)))
            .ToList();
    }

    private static void Walk(DirectoryInfo directory, string prefix, List<string> files)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            files.Add(prefix + file.Name);
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            if (!SkippedDirectories.Contains(child.Name))
            {
                Walk(child, $"{prefix}{child.Name}/", files);
            }
        }
    }

    private static string DeclaredOwner()
    {
        var manifest = Path.Combine(RepoRoot, "plugin", ".claude-plugin", "plugin.json");
        var url = JsonNode.Parse(File.ReadAllText(manifest))!["repository"]!.GetValue<string>();

        const string prefix = "https://github.com/";
        url.ShouldStartWith(prefix, Case.Sensitive, "plugin.json's repository is not a github.com URL.");

        var owner = url[prefix.Length..].TrimEnd('/').Split('/')[0];
        owner.ShouldNotBeEmpty("plugin.json's repository URL carries no owner segment.");

        return owner;
    }

    // A leading boundary so `moberghr/docu-me` is one match with the whole owner, not a suffix of it: the
    // two spellings differ by two characters at the end, which is exactly what a sloppy pattern hides.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9-])(?<owner>[A-Za-z0-9][A-Za-z0-9-]*)/docu-me\b",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Slug();

    /// <summary>One <c>owner/docu-me</c>, and the file and line it was read from.</summary>
    private sealed record Reference(string File, int Line, string Owner);

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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the tree cannot be walked.");
    }
}
