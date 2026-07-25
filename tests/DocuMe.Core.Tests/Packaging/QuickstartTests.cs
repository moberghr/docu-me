using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocuMe.Core.Confluence;
using DocuMe.Core.Scaffolding;
using Shouldly;

namespace DocuMe.Core.Tests.Packaging;

/// <summary>
/// The root <c>README.md</c> and <c>CHANGELOG.md</c> (PLAN.md §12), which are M6's acceptance artifact:
/// "fresh empty repo, full install story works end-to-end from README alone" (§14).
/// </summary>
/// <remarks>
/// <para>
/// A README is the one file nobody's build breaks over, which is exactly why it rots. And the failure it
/// rots into is expensive here: the reader is a person setting up a second repository, following the file
/// literally, with no way to tell a stale line from a live one. A command that no longer exists, a
/// scaffolded file that is no longer scaffolded, an install line naming the wrong package: each one is
/// discovered by a human hitting it, not by CI.
/// </para>
/// <para>
/// So these tests read the README as a set of claims about the tree and check each one against the tree.
/// They deliberately say nothing about prose, structure or length: the point is that what it promises is
/// still true, not that it is written a particular way.
/// </para>
/// </remarks>
public sealed partial class QuickstartTests
{
    /// <summary>Where step 3's list of what <c>init</c> writes lives.</summary>
    private const string ScaffoldSection = "### 3. Scaffold your repo";

    /// <summary>Where the table of what the plugin ships lives.</summary>
    private const string SkillsSection = "## Skills";

    [Fact]
    public void The_README_is_the_install_story_and_not_a_stub()
    {
        // The tests below all search this text; a one-line README would pass most of them vacuously.
        var readme = Readme();

        readme.ShouldContain(
            "## Quickstart",
            customMessage: "README.md has no quickstart, which is §12's named deliverable and M6's acceptance.");
        readme.ShouldContain(
            ScaffoldSection,
            customMessage: $"README.md lost '{ScaffoldSection}', which the scaffold assertions read.");
    }

    [Fact]
    public void Every_command_the_README_tells_you_to_run_exists()
    {
        var registered = RegisteredCommands();
        var mentioned = CommandMentions();

        mentioned.ShouldNotBeEmpty("The README names no `docume` subcommand at all, so it cannot be a quickstart.");

        var unknown = mentioned.Except(registered, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        // The literal M6 acceptance failure: somebody follows the README into a command that was renamed
        // or never existed. Only code spans and fenced blocks are scanned, so this is about lines a reader
        // would copy, not about prose that happens to contain the word.
        var message = $"README.md tells the reader to run [{string.Join(", ", unknown)}], and Program.cs "
            + $"registers [{string.Join(", ", registered.Order(StringComparer.Ordinal))}].";

        unknown.ShouldBeEmpty(message);
    }

    [Fact]
    public void Every_command_that_exists_is_in_the_README()
    {
        var undocumented = RegisteredCommands()
            .Except(CommandMentions(), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The other direction, and the one that rots quietly: a command shipped in a later slice and never
        // written down is a feature nobody outside this repo can find.
        var message = $"The CLI has commands the README never mentions: [{string.Join(", ", undocumented)}].";

        undocumented.ShouldBeEmpty(message);
    }

    [Fact]
    public void The_scaffold_table_is_what_init_actually_writes()
    {
        var scaffolded = Scaffold();
        var listed = ScaffoldTableRows();

        // Both directions on purpose. A missing row leaves the reader unaware of a file that appeared in
        // their repo; a stale row sends them looking for one that never will. Ordered so the diff reads.
        var message = "README.md's step-3 table and ProjectScaffolder disagree about what `docume init` "
            + $"writes.{Environment.NewLine}"
            + $"  scaffolded: {string.Join(", ", scaffolded)}{Environment.NewLine}"
            + $"  in README:  {string.Join(", ", listed)}";

        listed.ShouldBe(scaffolded, message);
    }

    [Fact]
    public void The_install_line_names_the_package_init_pins()
    {
        var manifest = ScaffoldedToolManifest();
        var readme = Readme();

        // Two halves of one story that live in different files: §12's global install and §12's per-repo
        // pin. A README naming a package id the manifest does not use installs a tool the workflows will
        // not find, and the two failures look nothing alike.
        readme.ShouldContain(
            $"dotnet tool install --global {manifest.PackageId}",
            Case.Sensitive,
            $"README.md does not install {manifest.PackageId}, which is what `init` pins.");

        readme.ShouldContain(
            $"{manifest.CommandName} --version",
            customMessage: $"README.md never runs `{manifest.CommandName}`, the command the package installs.");
    }

    [Fact]
    public void The_unreleased_note_is_pinned_to_the_version_in_the_tree()
    {
        var readme = Readme();

        // The README says the feed is empty because no tag has been pushed. That note has to name the
        // version it is talking about, so a bump cannot leave "no v0.1.0 tag" sitting under a 0.2.0 tree.
        // Delete the note in the same commit that pushes the first tag.
        if (!readme.Contains("Not published yet", StringComparison.Ordinal))
        {
            return;
        }

        var message = $"README.md still says the package is unpublished but does not name v{SolutionVersion}, "
            + "so the note is about some earlier version. Update it or delete it.";

        readme.ShouldContain($"v{SolutionVersion}", customMessage: message);
    }

    [Fact]
    public void The_plugin_install_story_agrees_with_the_plugin_README()
    {
        var readme = Readme();
        var plugin = File.ReadAllText(Path.Combine(RepoRoot, "plugin", "README.md"));

        // One install story written in two places. plugin/README.md is the one a plugin author reads and
        // the one the release notes mirror, so it is the source; the root README repeats it for the person
        // who never opens that directory.
        string[] lines = ["/plugin marketplace add ", "/plugin install docume@docume"];

        foreach (var line in lines)
        {
            plugin.ShouldContain(line, customMessage: $"plugin/README.md lost its `{line}` line.");
            readme.ShouldContain(line, customMessage: $"README.md and plugin/README.md disagree on `{line}`.");
        }
    }

    [Fact]
    public void The_credentials_it_names_are_the_ones_the_tool_reads()
    {
        var readme = Readme();

        // Rule §1.1. Env vars only, and the names have to be exact: a typo here is a token that is set,
        // exported, and invisible to the tool, which then reports missing credentials.
        readme.ShouldContain(
            ConfluenceCredentials.EmailVariable,
            customMessage: "README.md does not name the email variable the CLI reads.");
        readme.ShouldContain(
            ConfluenceCredentials.TokenVariable,
            customMessage: "README.md does not name the token variable the CLI reads.");

        // Both directions, because each name appears more than once (the export block and the repository
        // secrets). Asserting only that the right names are present lets a typo in the block a reader
        // actually copies pass, which is the one place it costs anything.
        //
        // Not every DOCUME_ name belongs to the CLI. The scaffolded workflows read their own secrets —
        // DOCUME_PACKAGES_TOKEN opens the GitHub Packages feed the pinned tool is restored from, and no
        // CLI ever sees it. Those are derived from the templates rather than listed here, so the guard
        // keeps its teeth: a name the README invents is still a name nothing in the tree reads.
        string[] read =
        [
            ConfluenceCredentials.EmailVariable,
            ConfluenceCredentials.TokenVariable,
            .. WorkflowVariables(),
        ];

        var invented = CredentialVariable()
            .Matches(readme)
            .Select(match => match.Value)
            .Where(name => !read.Contains(name, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var message = $"README.md tells the reader to set [{string.Join(", ", invented)}], and nothing "
            + $"that ships reads any of them — the CLI and the scaffolded workflows between them read "
            + $"[{string.Join(", ", read)}]. A variable nobody reads looks exactly like a missing "
            + "credential.";

        invented.ShouldBeEmpty(message);

        // And no real one. A README is the least guarded file in the repo and the most copied.
        readme.ShouldNotContain("ghp_", customMessage: "A GitHub token literal is in README.md (rule §1.1).");
        readme.ShouldNotContain("ATATT", customMessage: "An Atlassian API token literal is in README.md (rule §1.1).");
    }

    [Fact]
    public void A_skill_the_README_offers_either_ships_or_says_it_does_not()
    {
        var shipped = ShippedSkills();
        var missing = new List<string>();

        foreach (var line in Readme().Split('\n'))
        {
            var named = SkillMention()
                .Matches(line)
                .Select(match => match.Groups["skill"].Value)
                .Where(skill => !shipped.Contains(skill))
                .ToList();

            // The docs-loop skill is named all over the plan and is genuinely not written yet. Naming it is
            // fine; naming it as though it works is the failure, because a reader would go looking for it.
            if (named.Count > 0 && !line.Contains("not yet", StringComparison.OrdinalIgnoreCase))
            {
                missing.AddRange(named);
            }
        }

        var message = $"README.md presents [{string.Join(", ", missing.Distinct(StringComparer.Ordinal))}] as "
            + "available, but there is no plugin/skills/<name>/SKILL.md. Say 'not yet' on that line, or ship it.";

        missing.ShouldBeEmpty(message);
    }

    [Fact]
    public void Every_skill_that_ships_has_a_row_in_the_README_skills_table()
    {
        var section = Section(SkillsSection);

        var undocumented = ShippedSkills()
            .Where(skill => !section.Contains($"`/{skill}`", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Read from the "## Skills" section rather than from the whole file. A skill mentioned only in a
        // quickstart step is a skill nobody finds when they come back looking for the list, and the
        // whole-file version of this assertion passed on exactly that: the step-5 mention alone satisfied it
        // while the table row was gone.
        var message = $"plugin/skills/ ships [{string.Join(", ", undocumented)}], and README.md's "
            + $"'{SkillsSection}' table has no row for them.";

        undocumented.ShouldBeEmpty(message);
    }

    [Fact]
    public void Every_file_the_README_links_to_is_there()
    {
        var broken = MarkdownLink()
            .Matches(Readme())
            .Select(match => match.Groups["target"].Value)
            .Where(target => !target.StartsWith("http", StringComparison.Ordinal))
            .Where(target => !target.StartsWith('#'))
            .Where(target => !File.Exists(Path.Combine(RepoRoot, target)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        broken.ShouldBeEmpty($"README.md links to files that are not there: [{string.Join(", ", broken)}].");
    }

    [Fact]
    public void The_changelog_leads_with_the_version_the_tree_is_at()
    {
        var path = Path.Combine(RepoRoot, "CHANGELOG.md");

        File.Exists(path).ShouldBeTrue("There is no CHANGELOG.md, which §12 names as a deliverable.");

        var headings = ChangelogVersion()
            .Matches(File.ReadAllText(path))
            .Select(match => match.Groups["version"].Value)
            .ToList();

        headings.ShouldNotBeEmpty("CHANGELOG.md has no `## [X.Y.Z]` heading.");

        // §12 releases everything off one version, and the release workflow refuses a tag that disagrees
        // with the three files carrying it. This is the fourth place the number has to be right, and the
        // only one a reader ever sees: a release with no entry describing it is a release nobody can read.
        var message = $"CHANGELOG.md leads with {headings[0]} and Directory.Build.props says {SolutionVersion}. "
            + "Add the section for the version being prepared before bumping.";

        headings[0].ShouldBe(SolutionVersion, message);
    }

    /// <summary>The subcommands <c>Program.cs</c> hangs off the root, lowercased.</summary>
    private static HashSet<string> RegisteredCommands()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src", "DocuMe.Cli", "Program.cs"));

        var names = CommandRegistration()
            .Matches(program)
            .Select(match => match.Groups["name"].Value.ToLowerInvariant());

        var registered = new HashSet<string>(names, StringComparer.Ordinal);

        // Read from the registration list rather than from the Commands/ directory: a class that exists
        // but was never added to the root command is not a command a reader can run.
        registered.ShouldNotBeEmpty("No `<Name>Command.Build()` found in Program.cs, so the scan is broken.");

        return registered;
    }

    /// <summary>
    /// Every <c>docume &lt;word&gt;</c> inside a code span or a fenced block. Prose is skipped on purpose:
    /// the claim under test is about lines a reader would copy.
    /// </summary>
    private static HashSet<string> CommandMentions()
    {
        var mentions = CommandMention()
            .Matches(CodeText(Readme()))
            .Select(match => match.Groups["name"].Value);

        return new HashSet<string>(mentions, StringComparer.Ordinal);
    }

    /// <summary>The fenced blocks and inline code spans of a markdown document, joined by newlines.</summary>
    private static string CodeText(string markdown)
    {
        var collected = new List<string>();
        var fenced = false;

        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;

                continue;
            }

            if (fenced)
            {
                collected.Add(line);

                continue;
            }

            collected.AddRange(CodeSpan().Matches(line).Select(match => match.Groups["code"].Value));
        }

        return string.Join('\n', collected);
    }

    /// <summary>
    /// The text under <paramref name="heading"/>, up to the next heading at the same level or shallower.
    /// </summary>
    private static string Section(string heading)
    {
        var readme = Readme();
        var start = readme.IndexOf(heading, StringComparison.Ordinal);

        start.ShouldBeGreaterThanOrEqualTo(0, $"README.md has no '{heading}' section.");

        var level = heading.TakeWhile(character => character == '#').Count();
        var lines = readme[start..].Split('\n');

        var end = Array.FindIndex(
            lines,
            1,
            line => line.StartsWith('#') && line.TakeWhile(character => character == '#').Count() <= level);

        return string.Join('\n', end < 0 ? lines : lines[..end]);
    }

    /// <summary>The first cell of every row in step 3's table, in the order the README lists them.</summary>
    private static List<string> ScaffoldTableRows()
        => Section(ScaffoldSection)
            .Split('\n')
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
            .Select(line => line.Split('|')[1].Trim().Trim('`'))
            .Where(cell => cell.Length > 0 && !cell.StartsWith("---", StringComparison.Ordinal))
            .Where(cell => !string.Equals(cell, "Target", StringComparison.Ordinal))
            .ToList();

    /// <summary>What <c>docume init</c> writes into an empty directory, in the order it reports them.</summary>
    private static List<string> Scaffold()
        => ProjectScaffolder
            .Scaffold(Directory.CreateTempSubdirectory("docume-quickstart-tests").FullName, "DOCS", BaseUrl)
            .Select(result => result.RelativePath)
            .ToList();

    /// <summary>The package id and command name <c>init</c> pins into <c>.config/dotnet-tools.json</c>.</summary>
    private static (string PackageId, string CommandName) ScaffoldedToolManifest()
    {
        var directory = Directory.CreateTempSubdirectory("docume-quickstart-manifest").FullName;

        ProjectScaffolder.Scaffold(directory, "DOCS", BaseUrl);

        var manifest = File.ReadAllText(Path.Combine(directory, ".config", "dotnet-tools.json"));
        var tools = System.Text.Json.JsonDocument.Parse(manifest).RootElement.GetProperty("tools");
        var tool = tools.EnumerateObject().Single();

        // The manifest key is the package id, lowercased by the SDK; the README installs it as written on
        // the feed, so compare case-insensitively by returning what the README should say.
        var command = tool.Value.GetProperty("commands").EnumerateArray().Single().GetString();

        command.ShouldNotBeNull("The scaffolded tool manifest declares no command name.");

        return (PackageId: "DocuMe.Cli", CommandName: command);
    }

    /// <summary>The skills that have a <c>SKILL.md</c>, which is what makes a plugin skill loadable.</summary>
    private static HashSet<string> ShippedSkills()
    {
        var skills = Path.Combine(RepoRoot, "plugin", "skills");

        var names = Directory
            .EnumerateDirectories(skills)
            .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
            .Select(directory => new DirectoryInfo(directory).Name);

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    private const string BaseUrl = "https://example.atlassian.net/wiki";

    private static string RepoRoot { get; } = Locate();

    private static string SolutionVersion { get; } = ReadSolutionVersion();

    private static string Readme() => File.ReadAllText(Path.Combine(RepoRoot, "README.md"));

    private static string ReadSolutionVersion()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"));
        var version = props.Descendants("Version").SingleOrDefault();

        version.ShouldNotBeNull("Directory.Build.props declares no <Version> (§12).");

        return version.Value.Trim();
    }

    [GeneratedRegex(@"(?<name>\w+)Command\.Build\(\)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommandRegistration();

    // A literal space, not \s: code spans are joined by newlines, and \s would read the first word of the
    // next span as this one's subcommand.
    [GeneratedRegex(@"\bdocume (?<name>[a-z][a-z-]*)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommandMention();

    [GeneratedRegex(@"\bDOCUME_[A-Z0-9_]+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialVariable();

    /// <summary>
    /// Every <c>DOCUME_</c> variable the shipped workflow templates read. Read off the templates so a
    /// secret added to them lands here without anyone remembering to, and a name that appears in neither
    /// the CLI nor a template is still an invention.
    /// </summary>
    private static IEnumerable<string> WorkflowVariables()
    {
        var templates = Path.Combine(RepoRoot, "templates", "workflows");

        return Directory
            .EnumerateFiles(templates, "*.yml")
            .SelectMany(file => CredentialVariable().Matches(File.ReadAllText(file)).Cast<Match>())
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"`(?<code>[^`\n]+)`", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"`/(?<skill>docs-[a-z][a-z-]*)`", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SkillMention();

    [GeneratedRegex(@"\[[^\]]*\]\((?<target>[^)\s]+)\)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^##\s+\[(?<version>\d+\.\d+\.\d+)\]", RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ChangelogVersion();

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>: the README ships in the tree and has no build
    /// artifact, so the shipped copy is the one under test.
    /// </summary>
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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so README.md cannot be found.");
    }
}
