using System.Text.RegularExpressions;
using DocuMe.Core.Config;
using DocuMe.Core.Drift;
using DocuMe.Core.Feedback;
using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// <c>docs/wiki/10-concepts/lifecycle.md</c> held to the code behind the five stages it describes
/// (PLAN.md §1, §6.2, §6.3, §9).
/// </summary>
/// <remarks>
/// <para>
/// The page is the wiki's map: five stages, each naming the command or skill that moves a page to the
/// next one. <see cref="DogfoodWikiTests"/> checks that it converts and that its links resolve, and
/// <see cref="WikiIndexPageTests"/> checks that the concepts index counts five stages, but nothing
/// checked a single claim <em>inside</em> it against the code it credits.
/// </para>
/// <para>
/// The failure that motivated the class is the silent one. <c>sources</c> is what
/// <see cref="DriftPlanner"/> matches changed files against, so a stage whose code no glob covers is a
/// stage this page stops tracking the day the code moves: no failure, no warning, and a page that
/// looks maintained forever (<c>plugin/skills/docs-loop/SKILL.md</c> line 155 says exactly that).
/// Stage 4 was in that state. It describes the inbox, the per-page cursor and the reply pass in
/// detail, all of which live in <c>src/DocuMe.Core/Feedback/</c>, and the frontmatter credited
/// <c>Publishing/</c>, <c>Sync/</c> and the skills. A change under <c>Feedback/</c> reported the two
/// index pages and the root README, which carry the blanket <c>src/DocuMe.Core/**</c> glob, and not
/// the one page that explains what those files do.
/// </para>
/// <para>
/// <see cref="Every_stage_the_page_describes_is_reported_when_its_code_changes"/> is the guard for
/// that, and it asks the planner a real <c>docume drift</c> run uses rather than reading the
/// frontmatter, because reading the frontmatter is the check that would have passed before the fix.
/// </para>
/// </remarks>
public sealed partial class LifecyclePageTests
{
    private const string PagePath = "10-concepts/lifecycle.md";

    /// <summary>
    /// One representative file per stage, and the stage it belongs to. A change to any of these is a
    /// change to something this page describes, so a real <c>docume drift</c> run has to name the page.
    /// </summary>
    private static readonly (string Stage, string File)[] StageSources =
    [
        ("1. Generate", "plugin/skills/docs-loop/SKILL.md"),
        ("2. Publish", "src/DocuMe.Core/Publishing/PublishPipeline.cs"),
        ("3. Approve", "src/DocuMe.Core/Sync/LabelReader.cs"),
        ("4. Feedback", "src/DocuMe.Core/Feedback/FeedbackInbox.cs"),
        ("4. Feedback", "src/DocuMe.Core/Feedback/FeedbackReplyExecutor.cs"),
        ("5. Refresh", "plugin/skills/docs-refresh/SKILL.md"),
    ];

    /// <summary>The branch each stage's skill opens, paired with the skill that opens it.</summary>
    private static readonly (string Branch, string Skill)[] StageBranches =
    [
        ("docs/loop-", "docs-loop"),
        ("docs/feedback-", "docs-feedback"),
        ("docs/refresh-", "docs-refresh"),
    ];

    [Fact]
    public void Every_stage_the_page_describes_is_reported_when_its_code_changes()
    {
        var pages = Load().Pages;
        var blind = new List<string>();

        foreach (var (stage, file) in StageSources)
        {
            var report = DriftPlanner.Plan("baseline", "head", [file], pages);
            var reached = report.Pages.Select(page => page.Path).ToList();

            if (!reached.Contains(PagePath, StringComparer.Ordinal))
            {
                blind.Add($"{stage}: a change to {file} reports [{string.Join(", ", reached)}]");
            }
        }

        const string message =
            PagePath + " describes a stage whose code reaches no glob in its `sources`, so `docume "
            + "drift` never reports the page when that stage changes. That is the failure with no "
            + "symptom: the stage silently stops being maintained and the page goes on looking current. "
            + "The blanket `src/DocuMe.Core/**` on the index pages hides it, because the change still "
            + "reports *something*. Stages the page describes and drift does not reach:";

        blind.ShouldBeEmpty(message);
    }

    [Fact]
    public void The_five_stages_and_the_diagram_name_the_same_stages_in_the_same_order()
    {
        var page = Page();

        var headings = StageHeading()
            .Matches(page)
            .Select(match => match.Groups["name"].Value.ToLowerInvariant())
            .ToList();

        var diagram = DiagramBlock().Match(page);
        diagram.Success.ShouldBeTrue(PagePath + " no longer opens with a mermaid flowchart.");

        // First-appearance order, which is the order the arrows put them in: a node is labelled once
        // and referenced again bare, so `publish --> approve[...]` contributes only `approve` here.
        var nodes = DiagramNode()
            .Matches(diagram.Groups["body"].Value)
            .Select(match => match.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        const string count =
            PagePath + " opens with \"five stages\". A sixth section, or a fifth that was dropped, "
            + "leaves the sentence counting something that is not there.";
        const string mismatch =
            "The flowchart and the numbered sections are two spellings of one list, and the diagram is "
            + "what a reader looks at first. A stage renamed on one side and not the other publishes a "
            + "picture that disagrees with the page under it.";

        headings.Count.ShouldBe(5, count);
        page.ShouldContain("five stages", Case.Sensitive, count);
        nodes.ShouldBe(headings, mismatch);
    }

    [Fact]
    public void Every_option_the_page_hangs_on_a_command_exists_on_that_command()
    {
        var options = CommandOptions();

        var invocations = Invocation()
            .Matches(Page())
            .Select(match => (Command: match.Groups["command"].Value, Option: match.Groups["option"].Value))
            .Distinct()
            .ToList();

        // A page that names no `docume <cmd> --opt` at all would pass this vacuously. Six today, and
        // the TIP alone carries two of them.
        invocations.Count.ShouldBeGreaterThanOrEqualTo(4, PagePath + " names no option on any command.");

        var wrong = invocations
            .Where(invocation => !options.TryGetValue(invocation.Command, out var declared)
                || !declared.Contains(invocation.Option, StringComparer.Ordinal))
            .Select(invocation => $"docume {invocation.Command} {invocation.Option}")
            .Order(StringComparer.Ordinal)
            .ToList();

        const string message =
            PagePath + " tells a reader to run an invocation the CLI does not accept. An option that "
            + "exists on another command still exits 1 with a usage block, which reads like the "
            + "reader's mistake rather than the page's. Rejected:";

        wrong.ShouldBeEmpty(message);
    }

    [Fact]
    public void Sync_reply_is_the_only_half_the_cli_describes_as_writing_to_confluence()
    {
        var sync = File.ReadAllText(CommandPath("Sync"));

        var writing = SyncOption()
            .Matches(sync)
            .Where(match => match.Groups["description"].Value.Contains(
                "Writes to Confluence",
                StringComparison.Ordinal))
            .Select(match => match.Groups["option"].Value)
            .ToList();

        const string moved =
            PagePath + "'s opening says only `publish` and `sync --reply` write to the space, while "
            + "`sync --labels` and `sync --comments` read. A second writing half makes the sentence "
            + "false in the direction that matters: a reader deciding whether a command is safe to "
            + "point at a space they do not own.";

        writing.ShouldBe(["--reply"], moved);
    }

    [Fact]
    public void The_inbox_and_the_label_the_page_names_are_the_ones_the_code_uses()
    {
        var page = Page();

        const string inbox =
            PagePath + " sends the reader to the directory `sync --comments` fills. A path that is not "
            + "the one the inbox writes leaves them looking at an empty directory for the item they "
            + "were told is there.";
        const string label =
            PagePath + " names the label a reviewer adds to approve a page. Approval is a human typing "
            + "that word into Confluence, so the page and the reader that reads it back have to agree "
            + "on it exactly.";

        page.ShouldContain($"`{FeedbackInbox.RelativeDirectory}/`", Case.Sensitive, inbox);
        page.ShouldContain($"`{new LabelsConfig().Approved}` label", Case.Sensitive, label);
    }

    [Fact]
    public void Every_branch_the_page_promises_is_the_one_its_skill_opens()
    {
        var page = Page();
        var wrong = new List<string>();

        foreach (var (branch, skill) in StageBranches)
        {
            var source = File.ReadAllText(
                Path.Combine(RepoRoot, "plugin", "skills", skill, "SKILL.md"));

            if (!source.Contains($"git checkout -b \"{branch}$date\"", StringComparison.Ordinal))
            {
                wrong.Add($"/{skill} does not check out {branch}<date>");
                continue;
            }

            if (!page.Contains($"`{branch}<date>`", StringComparison.Ordinal))
            {
                wrong.Add($"{PagePath} stopped naming {branch}<date> for /{skill}");
            }
        }

        const string message =
            "Each generative stage promises the branch its pull request arrives on, and that name is "
            + "what a reviewer filters on and what the workflows key off. Pinned to the `git checkout "
            + "-b` line each skill actually runs, not to its prose. Mismatches:";

        wrong.ShouldBeEmpty(message);
    }

    /// <summary>Every long option each <c>Commands/&lt;Name&gt;Command.cs</c> declares, keyed by command.</summary>
    private static Dictionary<string, HashSet<string>> CommandOptions()
    {
        var directory = Path.Combine(RepoRoot, "src", "DocuMe.Cli", "Commands");

        var options = Directory
            .EnumerateFiles(directory, "*Command.cs")
            .ToDictionary(
                file => Path.GetFileNameWithoutExtension(file)
                    .Replace("Command", string.Empty, StringComparison.Ordinal)
                    .ToLowerInvariant(),
                file => new HashSet<string>(
                    OptionDeclaration()
                        .Matches(File.ReadAllText(file))
                        .Select(match => match.Groups["option"].Value),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

        options.ShouldNotBeEmpty("No `<Name>Command.cs` found, so the option scan is broken rather than clean.");

        return options;
    }

    private static string CommandPath(string name) =>
        Path.Combine(RepoRoot, "src", "DocuMe.Cli", "Commands", $"{name}Command.cs");

    private static string Page() =>
        File.ReadAllText(Path.Combine(
            RepoRoot,
            "docs",
            "wiki",
            PagePath.Replace('/', Path.DirectorySeparatorChar)));

    private static WikiTree Load() => WikiTree.Load(Path.Combine(RepoRoot, "docs", "wiki"));

    /// <summary>A numbered stage section: <c>## 4. Feedback</c>.</summary>
    [GeneratedRegex(
        @"^## \d+\. (?<name>[A-Za-z]+)\s*$",
        RegexOptions.ExplicitCapture | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex StageHeading();

    /// <summary>The body of the page's mermaid fence, so a markdown link is never read as a node.</summary>
    [GeneratedRegex(
        @"^```mermaid\r?\n(?<body>.*?)^```",
        RegexOptions.ExplicitCapture | RegexOptions.Multiline | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DiagramBlock();

    /// <summary>
    /// A labelled flowchart node: the id before its <c>[</c>. Only the labelled ones, so the
    /// back-edge <c>refresh --&gt; publish</c> does not read as a sixth stage.
    /// </summary>
    [GeneratedRegex(
        @"(?<id>[a-z]+)\[",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DiagramNode();

    /// <summary>An option hung off a command in the prose: <c>`publish --dry-run`</c>, <c>`sync --reply`</c>.</summary>
    [GeneratedRegex(
        @"(?:docume )?(?<command>publish|sync|drift|convert|status|dashboard|init) (?<option>--[a-z-]+)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Invocation();

    /// <summary>A <c>new Option&lt;T&gt;("--name")</c> declaration in a command file.</summary>
    [GeneratedRegex(
        @"new Option<[^>]+>\(""(?<option>--[a-z-]+)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionDeclaration();

    /// <summary>A sync option declaration together with the <c>Description</c> that follows it.</summary>
    [GeneratedRegex(
        @"new Option<[^>]+>\(""(?<option>--[a-z-]+)""\)\s*\{\s*Description = (?<description>.*?),\s*\};",
        RegexOptions.ExplicitCapture | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SyncOption();

    private static string RepoRoot { get; } = Locate();

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>: the wiki ships in the tree and is not
    /// copied beside the test assembly, so the shipped copy is the one under test.
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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so docs/wiki cannot be found.");
    }
}
