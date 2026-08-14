using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shouldly;

namespace DocuMe.Core.Tests.Packaging;

/// <summary>
/// Every shipped file that restores the pinned CLI, held to configuring the feed that CLI actually
/// lives on.
/// </summary>
/// <remarks>
/// <para>
/// <c>DocuMe.Cli</c> is published to GitHub Packages and never to nuget.org (PLAN.md §12;
/// <c>.github/workflows/release.yml</c> pushes to one <c>FEED</c> and it is not the public one), and
/// GitHub Packages authenticates every read — a public package included. The README knows this for the
/// laptop half and spends a whole step on it. The CI half shipped without it: six scaffolded workflows
/// and the composite action ran <c>dotnet tool restore</c> against whatever sources a runner has, which
/// is nuget.org alone.
/// </para>
/// <para>
/// Measured rather than reasoned about, at iter85 (<c>.mtk/paths-85/audit-tool-restore.mjs</c>): with a
/// version no local cache could supply, a restore in a freshly scaffolded repo FAILED on the public feed
/// and SUCCEEDED with a DocuMe feed configured. So every consumer's first docs job was a red check, on
/// their first push, with NuGet's own wording naming the feed it did look in and never the one it should
/// have. The README asserted the opposite in as many words: "That is how the workflows get the CLI."
/// </para>
/// <para>
/// The sweep is deliberately not a list of the seven files. A seventh workflow, or an eighth, that
/// restores the tool without adding the feed fails here without anyone remembering to add it.
/// </para>
/// </remarks>
public sealed partial class PackageFeedTests
{
    /// <summary>What a runner has by default, and the only thing a restore sees without the step.</summary>
    private const string PublicFeed = "https://api.nuget.org/v3/index.json";

    private const string RestoreCommand = "dotnet tool restore";

    private const string AddSource = "nuget add source";

    /// <summary>The secret the scaffolded workflows read, named here so the README cannot drift off it.</summary>
    private const string TokenSecret = "DOCUME_PACKAGES_TOKEN";

    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.Ordinal) { ".git", ".mtk", "bin", "obj", "node_modules" };

    /// <summary>
    /// Trees exempted from the walk because they quote a feed for a reader rather than for a runner.
    /// Empty, and <see cref="Every_skipped_tree_earns_its_exemption"/> is why: it held
    /// <c>tests/</c> and <c>tools/</c> from the day this class was written, copied from
    /// <see cref="RepositorySlugTests"/> along with a justification that is true there and false here.
    /// A slug is quoted wrong on purpose all over both trees; a <c>nuget.pkg.github.com</c> URL and a
    /// <c>dotnet tool restore</c> appear in neither, so the pair suppressed nothing and hid 49 files.
    /// A test that has to quote a broken feed re-adds its tree here and earns it in the same change.
    /// </summary>
    private static readonly string[] SkippedTrees = [];

    private static readonly HashSet<string> ScannedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".yml", ".yaml", ".json", ".mjs", ".sh" };

    [Fact]
    public void Every_shipped_file_that_restores_the_pinned_tool_adds_the_feed_first()
    {
        var restoring = ExecutableFiles()
            .Select(file => (File: file, Lines: File.ReadAllLines(Path.Combine(RepoRoot, file))))
            .Where(candidate => candidate.Lines.Any(Runs))
            .ToList();

        // Vacuous-pass guard. Six templates plus the composite action carry a restore today; a walk that
        // found fewer is reading less of the tree than it thinks, which would pass while proving nothing.
        restoring
            .Count
            .ShouldBeGreaterThanOrEqualTo(
                7,
                "Fewer files restore the pinned tool than the tree ships, so this sweep is not reading them.");

        var unsourced = new List<string>();

        foreach (var (file, lines) in restoring)
        {
            if (Unsourced(lines))
            {
                unsourced.Add($"{file}:{Array.FindIndex(lines, Runs) + 1}");
            }
        }

        var message = $"These restore the pinned DocuMe.Cli with no package feed configured first. "
            + $"DocuMe.Cli is not on {PublicFeed}, so the restore resolves the pin against a feed that "
            + $"has never held it and the job dies on its first run:\n  {string.Join("\n  ", unsourced)}";

        unsourced.ShouldBeEmpty(message);
    }

    /// <summary>
    /// Every literal GitHub Packages URL in the tree names the owner the plugin manifest declares.
    /// </summary>
    /// <remarks>
    /// <see cref="RepositorySlugTests"/> cannot see these: its pattern matches <c>owner/docu-me</c> and a
    /// feed URL is <c>owner/index.json</c>. The wrong slug is the same plausible typo it was there —
    /// <c>moberg</c> is a real Moberg org — and it now appears in eight more places than it did.
    /// </remarks>
    [Fact]
    public void Every_feed_url_names_the_owner_the_plugin_manifest_declares()
    {
        var declared = DeclaredOwner();
        var found = FeedUrls().ToList();

        found.ShouldNotBeEmpty("The walk found no GitHub Packages URL at all.");

        var wrong = found
            .Where(url => !string.Equals(url.Owner, declared, StringComparison.Ordinal))
            .Select(url => $"{url.File}:{url.Line} → {url.Owner}")
            .ToList();

        var message = $"A GitHub Packages feed under an owner that is not `{declared}` resolves to "
            + $"nothing, and it fails in somebody else's CI:\n  {string.Join("\n  ", wrong)}";

        wrong.ShouldBeEmpty(message);
    }

    /// <summary>
    /// The README tells a consumer to add the feed by hand; the workflows add it themselves. Both spell
    /// the same URL, or one of them is teaching a feed nothing restores from.
    /// </summary>
    [Fact]
    public void The_readme_and_the_runners_name_the_same_feed()
    {
        var spellings = FeedUrls()
            .Select(url => url.Url)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var message = "The install story names more than one GitHub Packages feed, so the one a consumer "
            + $"adds by hand and the one their CI adds are different:\n  {string.Join("\n  ", spellings)}";

        spellings.Count.ShouldBe(1, message);

        FeedUrls()
            .Select(url => url.File)
            .Distinct(StringComparer.Ordinal)
            .ShouldContain("README.md", "The README no longer names the feed a consumer has to add.");
    }

    /// <summary>
    /// The feed is authenticated, so every step adding it carries a credential — and none of them may
    /// carry it as a literal (rule §1.1). A token belongs in a secret or an action input, read through
    /// the environment.
    /// </summary>
    [Fact]
    public void No_shipped_feed_step_inlines_its_token()
    {
        var inlined = new List<string>();

        // Executable files only. README.md spells `--password <a PAT with read:packages>` on purpose,
        // teaching the reader where their own token goes; that is the opposite of a leak.
        foreach (var file in ExecutableFiles())
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot, file));

            for (var index = 0; index < lines.Length; index++)
            {
                var password = Password().Match(lines[index]);

                if (!password.Success)
                {
                    continue;
                }

                var value = password.Groups["value"].Value;
                var indirect = value.StartsWith("\"$", StringComparison.Ordinal)
                    || value.StartsWith('$');

                if (!indirect)
                {
                    inlined.Add($"{file}:{index + 1} → {value}");
                }
            }
        }

        var message = "A `--password` reads something other than a variable, so a credential is spelled "
            + $"into a file that ships to consumers (rule §1.1):\n  {string.Join("\n  ", inlined)}";

        inlined.ShouldBeEmpty(message);
    }

    /// <summary>
    /// The one secret a consumer outside Moberg has to create. The workflows fall back to the job's own
    /// <c>GITHUB_TOKEN</c>, which reads a package in its own organisation and not one in another, so a
    /// cross-org consumer whose README never named this secret has a failing job and no lead.
    /// </summary>
    [Fact]
    public void The_readme_names_the_secret_the_scaffolded_workflows_read()
    {
        // `secrets.` and not the bare name: every template also NAMES the secret in the comment above
        // its feed step, so a file that stopped reading it would still mention it. Caught by the
        // mutation round, which deleted the read from docs-drift-pr.yml and stayed green.
        var read = $"secrets.{TokenSecret}";

        var templates = ShippedFiles()
            .Where(file => file.StartsWith("templates/workflows/", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(Path.Combine(RepoRoot, file))
                .Contains(read, StringComparison.Ordinal))
            .ToList();

        templates.Count.ShouldBe(8, $"Not every shipped workflow template reads {read}.");

        var readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));
        const string Names = $"The scaffolded workflows read a `{TokenSecret}` secret and README.md "
            + "never tells anyone to create it.";

        readme.ShouldContain(TokenSecret, customMessage: Names);
        readme.ShouldContain(
            "read:packages",
            customMessage: "README.md does not say which scope that token needs.");
    }

    /// <summary>
    /// Every tree in <see cref="SkippedTrees"/> suppresses at least one finding this class would
    /// otherwise report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An exemption is a blind spot bought on purpose, and it is only worth the price if it removes
    /// something. Nothing paired this list with the tree, so the two entries it carried could sit
    /// there being false: they were copied from <see cref="RepositorySlugTests"/>, whose regex those
    /// trees really do offend, but this class matches a feed URL and a restore command and iter190
    /// measured zero of either anywhere beneath <c>tests/</c> or <c>tools/</c>.
    /// </para>
    /// <para>
    /// The assertion runs one way only. It never says which trees may be exempted — a tree that
    /// genuinely quotes a broken feed stays exempt for as long as it does — it says an exemption that
    /// removes nothing must not exist, because that one only hides the next real offender.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_skipped_tree_earns_its_exemption()
    {
        var declared = DeclaredOwner();
        var walked = WalkedFiles();

        // Anti-vacuity guard: a walk that read nothing makes every tree look unearned, which fails
        // loudly, but a walk that read nothing is also why the message below would be wrong.
        walked.ShouldNotBeEmpty("The walk found no file at all, so no exemption can be judged.");

        var unearned = SkippedTrees
            .Where(tree => !Suppresses(tree, walked, declared))
            .ToList();

        var message = "A tree is held out of this sweep without carrying anything the sweep would "
            + "report, so it buys no accuracy and hides every feed URL and every unsourced restore "
            + "written under it from now on. Delete the entry, or name the file that needed it:\n  "
            + string.Join("\n  ", unearned);

        unearned.ShouldBeEmpty(message);
    }

    /// <summary>Whether exempting <paramref name="tree"/> removes a finding this class would report.</summary>
    private static bool Suppresses(string tree, List<string> walked, string declared)
    {
        var inside = walked
            .Where(file => file.StartsWith(tree, StringComparison.Ordinal))
            .ToList();

        var wrongOwner = FeedUrls(inside)
            .Any(url => !string.Equals(url.Owner, declared, StringComparison.Ordinal));

        if (wrongOwner)
        {
            return true;
        }

        return inside
            .Where(Executable)
            .Select(file => File.ReadAllLines(Path.Combine(RepoRoot, file)))
            .Any(Unsourced);
    }

    /// <summary>Whether these lines run the pinned restore with no feed configured before it.</summary>
    private static bool Unsourced(string[] lines)
    {
        var restore = Array.FindIndex(lines, Runs);

        if (restore < 0)
        {
            return false;
        }

        var source = Array.FindIndex(
            lines,
            line => !Prose(line) && line.Contains(AddSource, StringComparison.Ordinal));

        return source < 0 || source > restore;
    }

    /// <summary>
    /// The files a runner executes, as opposed to the ones that describe them. CHANGELOG.md, PLAN.md and
    /// the wiki all quote <c>dotnet tool restore</c> in prose, and release.yml quotes it inside the
    /// release notes it writes; none of them runs it, and a sweep that cannot tell the difference reports
    /// five findings where there are none.
    /// </summary>
    private static IEnumerable<string> ExecutableFiles() => ShippedFiles().Where(Executable);

    private static bool Executable(string file)
        => file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a line runs the restore rather than mentioning it. A yaml comment and a backticked
    /// mention inside a heredoc both read as the command and neither is one.
    /// </summary>
    private static bool Runs(string line)
        => !Prose(line) && line.Contains(RestoreCommand, StringComparison.Ordinal);

    private static bool Prose(string line)
        => line.TrimStart().StartsWith('#') || line.Contains('`', StringComparison.Ordinal);

    /// <summary>Every authored file a consumer or a runner acts on, exempted trees aside.</summary>
    private static List<string> ShippedFiles()
        => WalkedFiles()
            .Where(file => !SkippedTrees.Any(tree => file.StartsWith(tree, StringComparison.Ordinal)))
            .ToList();

    /// <summary>
    /// The same walk with <see cref="SkippedTrees"/> not yet applied, so the exemption list can be
    /// held to removing something rather than taken at its word.
    /// </summary>
    private static List<string> WalkedFiles()
    {
        var files = new List<string>();
        Walk(new DirectoryInfo(RepoRoot), string.Empty, files);

        return files
            .Where(file => ScannedExtensions.Contains(Path.GetExtension(file)))
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

    private static IEnumerable<FeedUrl> FeedUrls() => FeedUrls(ShippedFiles());

    private static IEnumerable<FeedUrl> FeedUrls(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot, file));

            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var match in Feed().Matches(lines[index]).Cast<Match>())
                {
                    yield return new FeedUrl(file, index + 1, match.Value, match.Groups["owner"].Value);
                }
            }
        }
    }

    private static string DeclaredOwner()
    {
        var manifest = Path.Combine(RepoRoot, "plugin", ".claude-plugin", "plugin.json");
        var url = JsonNode.Parse(File.ReadAllText(manifest))!["repository"]!.GetValue<string>();

        const string Prefix = "https://github.com/";
        url.ShouldStartWith(Prefix, Case.Sensitive, "plugin.json's repository is not a github.com URL.");

        return url[Prefix.Length..].TrimEnd('/').Split('/')[0];
    }

    // Only a literal owner: release.yml derives its own from `github.repository_owner`, which is correct
    // there and is not a spelling anything could get wrong.
    [GeneratedRegex(
        @"https://nuget\.pkg\.github\.com/(?<owner>[A-Za-z0-9][A-Za-z0-9-]*)/index\.json",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Feed();

    [GeneratedRegex(
        @"--password\s+(?<value>\S+)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Password();

    /// <summary>One GitHub Packages URL, and where it was read from.</summary>
    private sealed record FeedUrl(string File, int Line, string Url, string Owner);

    private static string RepoRoot { get; } = Locate();

    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the tree cannot be walked.");
    }
}
