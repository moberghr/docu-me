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
        // the floors sit below what the tree carries today: 157 files scanned, 23 references across 15 of
        // them, measured at iter132 (.mtk/paths-132/find-slug-offenders.py runs the same walk).
        //
        // The floors also have to be tight enough to catch the pattern matching too LITTLE, which is the
        // way this sweep dies quietly: iter132 narrowed the regex to exclude filesystem paths, and a
        // careless narrowing there drops every `github.com/…` reference at once — 10 files, which the
        // previous floor of 8 waved through. Raise these with the tree; do not lower them to pass.
        references.ShouldNotBeEmpty("The walk found no reference to this repository at all.");
        references
            .Count
            .ShouldBeGreaterThan(18, "The walk is matching fewer references than the tree carries.");
        references
            .Select(reference => reference.File)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBeGreaterThan(12, "The walk is reading fewer files than the tree has.");

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
    /// The classification itself, on one line each of every shape the tree carries.
    /// </summary>
    /// <remarks>
    /// The sweep above only reports disagreement, so it cannot say whether a shape was read correctly or
    /// skipped entirely — a pattern that matched nothing would pass it on the vacuous-pass floors alone.
    /// These are the shapes measured in the tree at iter132: a GitHub URL, a raw-content URL, a bare slug
    /// in prose or YAML, and the local filesystem path that must not read as an owner.
    /// </remarks>
    [Theory]

    // Real references: the owner must be read, whole, whatever precedes it.
    [InlineData("<RepositoryUrl>https://github.com/moberghr/docu-me</RepositoryUrl>", "moberghr")]
    [InlineData("\"$schema\": \"https://raw.githubusercontent.com/moberghr/docu-me/main/schema.json\"", "moberghr")]
    [InlineData("/plugin marketplace add moberghr/docu-me", "moberghr")]
    [InlineData("          repository: moberghr/docu-me", "moberghr")]
    [InlineData("- A composite action, `moberghr/docu-me/actions@v1`", "moberghr")]

    // The wrong owner is still caught in every one of those positions.
    [InlineData("https://github.com/moberg/docu-me", "moberg")]
    [InlineData("/plugin marketplace add moberg/docu-me", "moberg")]

    // Not references: a local path segment is not a GitHub owner.
    [InlineData("projects[\"/Users/mirkobudimir/Dev/docu-me\"].hasTrustDialogAccepted", null)]
    [InlineData("cd ~/Dev/docu-me && dotnet build", null)]
    public void Every_shape_the_tree_carries_is_classified_the_way_a_reader_would(
        string line,
        string? expectedOwner)
    {
        var owners = Slug().Matches(line)
            .Select(match => match.Groups["owner"].Value)
            .ToList();

        if (expectedOwner is null)
        {
            owners.ShouldBeEmpty($"`{line}` names no repository, so it must contribute no owner.");
            return;
        }

        owners.ShouldBe([expectedOwner], $"`{line}` should read as exactly one owner.");
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
    //
    // The second lookbehind excludes a local filesystem path. An absolute path to this checkout ends
    // `…/Dev/docu-me`, and the first boundary alone read `Dev` as an owner — so quoting a path in any
    // scanned file failed this test with an owner nobody wrote. It cost a red suite for two commits
    // (a87d6d8, 35d7529) after iter131 pasted the CLI's untrusted-workspace warning, which names
    // `/Users/mirkobudimir/Dev/docu-me`, into GATES.md. A path segment cannot break a consumer's runner,
    // which is the only thing this sweep exists to catch.
    //
    // So: reject an owner preceded by `/`, unless that `/` closes a GitHub host. Both host shapes the tree
    // uses stay in scope (`github.com/…` and `raw.githubusercontent.com/…`), as does every bare
    // `owner/docu-me` in prose, a fence, or a YAML value. Variable-length lookbehind is a .NET regex
    // feature; Every_shape_the_tree_carries_is_classified_the_way_a_reader_would pins each case.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9-])(?<!(?<!github(?:usercontent)?\.com)/)(?<owner>[A-Za-z0-9][A-Za-z0-9-]*)/docu-me\b",
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
