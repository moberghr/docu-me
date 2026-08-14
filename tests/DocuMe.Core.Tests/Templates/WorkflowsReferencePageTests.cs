using DocuMe.Core.Tests.Cli;
using Shouldly;
using YamlDotNet.RepresentationModel;

namespace DocuMe.Core.Tests.Templates;

/// <summary>
/// <c>docs/wiki/30-automation/workflows.md</c>'s trigger table, credential list, pull-request set,
/// branch-check note and renderer paragraph, against the triggers, credentials, permissions and steps the
/// six templates in <c>templates/workflows/</c> actually carry.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WorkflowTemplateTests"/> proves the behaviour and already knows every set this class checks —
/// its <c>ConfluenceFacing</c> list is the same three templates. Nothing tied the page to it, and at iter92
/// five of its claims named a trigger the templates do not draw. The worst was the whole Secrets section:
/// "Every workflow needs the two credential variables" is an instruction to hand the publishing token to
/// all six, when three deliberately hold neither, and two of those three are the unsupervised model runs
/// that must never carry it (rule §0.4, §1.5). A reader following the page undoes a least-privilege
/// decision the templates were split apart to make.
/// </para>
/// <para>
/// The rest were the same shape, one step less dangerous each: "the two workflows that produce repo
/// changes" named two of the four that carry <c>contents: write</c>; the branch-check note credited "each
/// template" with a <c>git ls-remote</c> guard that two of six run; the table left <c>workflow_dispatch</c>
/// off the one row whose template has it while naming it on the two others; and "every template opens with
/// the same four lines" gave <c>docs-feedback.yml</c> a <c>docume</c> invocation that lives inside its skill
/// — which the page's own last paragraph says.
/// </para>
/// <para>
/// Every set here is derived from the templates rather than listed, because the failure being guarded
/// against is a template moving sides while the prose stays still. The counts are asserted as the number
/// word the prose uses, so "the two workflows" fails on the count as well as on the names.
/// </para>
/// </remarks>
public sealed class WorkflowsReferencePageTests
{
    private const string PagePath = "docs/wiki/30-automation/workflows.md";
    private const string ActionPath = "actions/action.yml";
    private const string CredentialPrefix = "DOCUME_CONFLUENCE_";
    private const string ModelKey = "ANTHROPIC_API_KEY";
    private const string BranchCheck = "git ls-remote --heads origin 'refs/heads/docs/";
    private const string RendererConstruction = "new MermaidRenderer(";
    private const string RenderFlag = "--render-mermaid";

    /// <summary>The trigger table's header cell, and the row scan's anchor.</summary>
    private const string TriggerTableHeader = "| Workflow | Fires on |";

    /// <summary>
    /// Every <c>on:</c> key a template declares, and the phrase the row has to carry for a reader to know
    /// it fires that way.
    /// <para>
    /// The phrases are separable on purpose — no phrase is a substring of another key's phrase — which is
    /// what lets the check run both ways: a row must name every key its template has and must not name one
    /// it does not. <c>dispatch</c> rather than <c>workflow_dispatch</c> because the rows say "manual
    /// dispatch", and it still cannot be satisfied by <c>workflow_run</c>.
    /// </para>
    /// </summary>
    private static readonly (string Key, string MustName)[] Triggers =
    [
        ("push", "push"),
        ("pull_request", "pull_request"),
        ("workflow_run", "workflow_run"),
        ("schedule", "schedule"),
        ("workflow_dispatch", "dispatch"),
    ];

    [Fact]
    public void Each_row_names_every_trigger_its_template_declares_and_no_others()
    {
        var rows = TriggerRows();

        rows.Select(row => row.Template).ShouldBe(
            Templates(),
            ignoreOrder: true,
            $"{PagePath}'s table and templates/workflows/ describe different sets of workflows.");

        var wrong = new List<string>();

        foreach (var (template, cell) in rows)
        {
            var declared = TriggerKeys(template);

            foreach (var (key, mustName) in Triggers)
            {
                var named = cell.Contains(mustName, StringComparison.Ordinal);

                if (declared.Contains(key) && !named)
                {
                    wrong.Add($"{template} declares `{key}` and its row never says so");
                }

                if (!declared.Contains(key) && named)
                {
                    wrong.Add($"{template}'s row names `{key}` and the template has no such trigger");
                }
            }
        }

        const string mismatched = "A row whose trigger a reader cannot rely on is worse than no row: the "
            + "consequence of getting this wrong is a workflow somebody waits for that never fires, or one "
            + "they never think to dispatch by hand. Offenders:";

        wrong.ShouldBeEmpty(mismatched);
    }

    /// <summary>
    /// The Secrets section names the three templates that carry a Confluence credential, and names the
    /// other three as the ones that hold none.
    /// </summary>
    /// <remarks>
    /// Split at the yaml fence rather than read whole: the section has to say both halves, and a section
    /// listing all six on either side of it would pass a check that only counted names. The fence is where
    /// "these carry it" stops and "these do not" starts.
    /// </remarks>
    [Fact]
    public void The_secrets_section_names_the_templates_that_actually_carry_a_credential()
    {
        var section = Section("## Secrets");
        var fence = section.FindIndex(line => line.StartsWith("```", StringComparison.Ordinal));

        fence.ShouldBeGreaterThanOrEqualTo(0, $"{PagePath}'s Secrets section no longer shows the two variables.");

        var carrying = TemplatesWhoseTextContains(CredentialPrefix);
        var withoutCredentials = Templates().Except(carrying, StringComparer.Ordinal).ToList();

        const string listed = "The Secrets section must name the templates that carry the credentials, not "
            + "leave a reader to assume all six do — handing the publishing token to the two model runs "
            + "undoes rule §0.4's whole posture, and it reads as the page telling them to.";

        Named(section.Take(fence)).ShouldBe(carrying, ignoreOrder: true, listed);

        const string excused = "The Secrets section must name the templates that hold neither credential. "
            + "Leaving them out is how a reader concludes the omission was an oversight and fixes it.";

        Named(section.Skip(fence)).ShouldBe(withoutCredentials, ignoreOrder: true, excused);

        string.Join('\n', section).ShouldContain(
            $"{Count(carrying.Count)} of the {Count(Templates().Count)}",
            Case.Insensitive,
            "The Secrets section no longer counts the templates that carry a credential.");

        var modelDriven = TemplatesWhoseTextContains(ModelKey);

        const string key = "The templates that run a model need a model key, and the section that lists "
            + "secrets is where a consumer looks for it.";

        modelDriven.ShouldNotBeEmpty($"No template reads {ModelKey}, so this assertion has nothing to check.");
        string.Join('\n', section).ShouldContain(ModelKey, customMessage: key);
        modelDriven.ShouldBeSubsetOf(withoutCredentials, "A model run must not hold a Confluence credential (§0.4).");
    }

    /// <summary>
    /// The pull-request section names every template that can change the repository, derived from the one
    /// permission that decides it.
    /// </summary>
    /// <remarks>
    /// <c>contents: write</c> is the grant, so it is the derivation: <c>docs-drift-pr.yml</c> writes a
    /// comment and <c>docs-drift.yml</c> a label, and neither can touch the repo whatever their steps say.
    /// Reading the <c>git push</c> lines instead would have found two, because the branch the other two
    /// push is pushed by the skill and not by the yaml.
    /// </remarks>
    [Fact]
    public void The_pull_request_section_names_every_template_that_can_change_the_repository()
    {
        var section = Section("## Everything writes through a pull request");
        var prose = section.TakeWhile(line => !line.StartsWith("> [!", StringComparison.Ordinal)).ToList();

        prose.ShouldNotBeEmpty($"{PagePath}'s pull-request section is now nothing but its note.");

        var changers = Templates()
            .Where(template => Permissions(template).Contains("contents: write"))
            .ToList();

        const string missing = "Every template that can change the repository has to appear where the page "
            + "explains that nothing reaches the default branch directly — a reader auditing the claim "
            + "counts the files, and one left out looks like the exception to it.";

        Named(prose).ShouldBe(changers, ignoreOrder: true, missing);

        string.Join('\n', prose).ShouldContain(
            $"{Count(changers.Count)} of the {Count(Templates().Count)}",
            Case.Insensitive,
            "The pull-request section no longer counts the templates that change the repository.");
    }

    /// <summary>
    /// The note about a run that opened no pull request names the two templates that actually check, and
    /// claims it of nobody else.
    /// </summary>
    [Fact]
    public void The_branch_check_note_names_the_templates_that_run_the_check()
    {
        var section = Section("## Everything writes through a pull request");
        var note = section.SkipWhile(line => !line.StartsWith("> [!", StringComparison.Ordinal)).ToList();

        note.ShouldNotBeEmpty($"{PagePath} no longer carries the note about a run that pushed nothing.");

        var checking = TemplatesWhoseTextContains(BranchCheck);

        const string credited = "The note must name the templates that run the check. Crediting all six "
            + "with a guard two of them have is a false assurance about the four, and it is the kind a "
            + "green run cannot correct.";

        checking.ShouldNotBeEmpty($"No template runs `{BranchCheck}…`, so the note describes nothing.");
        Named(note).ShouldBe(checking, ignoreOrder: true, credited);

        var text = string.Join('\n', note);

        foreach (var overreach in (string[])["each template", "every template"])
        {
            text.ShouldNotContain(
                overreach,
                Case.Insensitive,
                $"The note generalises the check to '{overreach}'.");
        }
    }

    /// <summary>
    /// The paragraph explaining <c>mermaid: auto</c> names every command that renders diagrams, and the
    /// action's decision step provisions for the same set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from the commands that construct a <c>MermaidRenderer</c>, which is the only thing that
    /// makes a command render. At iter92 the page said publish was the only one, which was the sharper
    /// half of the finding: <c>convert --render-mermaid</c> renders too and exits 1 when the toolchain is
    /// missing, and <c>auto</c> keyed on the subcommand alone, so a consumer's convert job failed with the
    /// renderer's own "cannot load beautiful-mermaid" — the exact bug the action carries those two steps to
    /// prevent. The sentence claiming it could not happen is what kept it invisible.
    /// </para>
    /// <para>
    /// The action is asserted alongside the page because the sentence is a promise about the action's
    /// behaviour, and a third rendering command would have to reach both.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_renderer_paragraph_names_every_command_that_renders_diagrams()
    {
        var rendering = RenderingCommands();
        var paragraph = Paragraph("`mermaid: auto`");
        var decision = File.ReadAllText(Path.Combine(DocumeCli.RepoRoot, ActionPath));

        foreach (var command in rendering)
        {
            var unnamed = $"`docume {command}` renders diagrams and the paragraph explaining which "
                + "invocations get a renderer never mentions it, so a consumer running it under "
                + "`mermaid: auto` learns what it needs from a failed job.";

            paragraph.ShouldContain(command, Case.Insensitive, unnamed);

            var ungated = $"`docume {command}` renders diagrams and {ActionPath} does not provision for it, "
                + "so the action fails it with the renderer's exit 3 on any wiki holding one diagram.";

            decision.ShouldContain(command, Case.Insensitive, ungated);
        }

        // Which of them renders unconditionally is the distinction the prose turns on, and the flag is
        // what makes the other one conditional. One, or the "always / only when asked" split is fiction.
        var flagged = rendering.Where(DeclaresRenderFlag).ToList();

        var split = $"{string.Join(", ", flagged)} declare `{RenderFlag}`; the page describes exactly one "
            + "command whose rendering is opt-in.";

        flagged.Count.ShouldBe(1, split);

        const string unflagged = $"The paragraph must name `{RenderFlag}`: without it a reader cannot tell "
            + "which convert invocations `auto` provisions for, and a bare convert needs no renderer at all.";

        paragraph.ShouldContain(RenderFlag, Case.Sensitive, unflagged);

        decision.ShouldContain(
            RenderFlag,
            Case.Sensitive,
            $"{ActionPath} decides on the subcommand alone, so `convert {RenderFlag}` gets no renderer.");
    }

    /// <summary>
    /// The commands that construct a <see cref="Core.Markdown.MermaidRenderer"/>, by the name a consumer
    /// types. Their class-name prefix, matched case-insensitively downstream rather than lowered, so the
    /// derivation stays a substring of the file it came from.
    /// </summary>
    private static List<string> RenderingCommands()
    {
        var commands = CommandSources()
            .Where(file => File.ReadAllText(file).Contains(RendererConstruction, StringComparison.Ordinal))
            .Select(NameOf)
            .Order(StringComparer.Ordinal)
            .ToList();

        commands.ShouldNotBeEmpty(
            $"No command constructs a {RendererConstruction[4..^1]}, so nothing here describes rendering.");

        return commands;
    }

    private static bool DeclaresRenderFlag(string command) => CommandSources()
        .Where(file => string.Equals(NameOf(file), command, StringComparison.Ordinal))
        .Any(file => File.ReadAllText(file).Contains($"\"{RenderFlag}\"", StringComparison.Ordinal));

    private static IEnumerable<string> CommandSources() => Directory.EnumerateFiles(
        Path.Combine(DocumeCli.RepoRoot, "src", "DocuMe.Cli", "Commands"),
        "*Command.cs");

    /// <summary><c>ConvertCommand.cs</c> → <c>Convert</c>.</summary>
    private static string NameOf(string source)
    {
        var file = Path.GetFileNameWithoutExtension(source);

        return file[..^"Command".Length];
    }

    /// <summary>
    /// The contiguous block of prose holding <paramref name="anchor"/>. Paragraph rather than section: the
    /// page explains the action across several of them, and a section-wide search would let a sentence
    /// about `drift` satisfy a claim about rendering.
    /// </summary>
    private static string Paragraph(string anchor)
    {
        var lines = PageLines();
        var at = lines.FindIndex(line => line.Contains(anchor, StringComparison.Ordinal));

        at.ShouldBeGreaterThanOrEqualTo(0, $"{PagePath} no longer explains {anchor}.");

        var start = at;

        while (start > 0 && lines[start - 1].Length > 0)
        {
            start--;
        }

        var block = lines.Skip(start).TakeWhile(line => line.Length > 0);

        return string.Join('\n', block);
    }

    /// <summary>The lines of <paramref name="header"/>'s section, up to the next one.</summary>
    private static List<string> Section(string header)
    {
        var lines = PageLines();
        var start = lines.FindIndex(line => string.Equals(line, header, StringComparison.Ordinal));

        start.ShouldBeGreaterThanOrEqualTo(0, $"{PagePath} no longer has a '{header}' section.");

        return lines
            .Skip(start + 1)
            .TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>The template filenames mentioned anywhere in <paramref name="lines"/>.</summary>
    private static List<string> Named(IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);

        return Templates()
            .Where(template => text.Contains(template, StringComparison.Ordinal))
            .ToList();
    }

    private static List<(string Template, string Cell)> TriggerRows()
    {
        var lines = PageLines();
        var header = lines.FindIndex(line => line.StartsWith(TriggerTableHeader, StringComparison.Ordinal));

        header.ShouldBeGreaterThanOrEqualTo(0, $"{PagePath} no longer has the trigger table.");

        return lines
            .Skip(header + 2)
            .TakeWhile(line => line.StartsWith('|'))
            .Select(row => row.Split('|'))
            .Where(cells => cells.Length > 3)
            .Select(cells => (Template: cells[1].Trim().Trim('`'), Cell: cells[2]))
            .ToList();
    }

    /// <summary>The keys of a template's <c>on:</c> block — the triggers a runner acts on.</summary>
    private static HashSet<string> TriggerKeys(string template)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(TemplateText(template));

        stream.Load(reader);

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var on = (YamlMappingNode)root.Children
            .Single(child => string.Equals(((YamlScalarNode)child.Key).Value, "on", StringComparison.Ordinal))
            .Value;

        var keys = on.Children.Select(child => ((YamlScalarNode)child.Key).Value ?? string.Empty);

        return new HashSet<string>(keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// A template's <c>permissions:</c> entries, as the <c>scope: level</c> lines a reader of the yaml
    /// sees. The block is top-level in all six, so this reads it as text rather than walking two
    /// possible positions.
    /// </summary>
    private static List<string> Permissions(string template)
    {
        var lines = TemplateText(template).Split('\n');
        var at = Array.FindIndex(lines, line => line.StartsWith("permissions:", StringComparison.Ordinal));

        at.ShouldBeGreaterThanOrEqualTo(0, $"{template} declares no permissions block.");

        return lines
            .Skip(at + 1)
            .TakeWhile(line => line.StartsWith(' '))
            .Select(line => line.Trim())
            .ToList();
    }

    private static List<string> TemplatesWhoseTextContains(string token) => Templates()
        .Where(template => TemplateText(template).Contains(token, StringComparison.Ordinal))
        .ToList();

    /// <summary>
    /// The number word the page's prose uses for <paramref name="count"/>. Asserted as a word because a
    /// wrong count is how the claim failed: "the two workflows that produce repo changes" named two of
    /// four, and a check on the names alone would let the next edit re-introduce the miscount.
    /// </summary>
    private static string Count(int count) => count switch
    {
        1 => "one",
        2 => "two",
        3 => "three",
        4 => "four",
        5 => "five",
        6 => "six",
        8 => "eight",
        _ => throw new ArgumentOutOfRangeException(
            nameof(count),
            count,
            "The page counts these sets in words, and this one is outside the range it spells out."),
    };

    private static List<string> Templates() => Directory
        .EnumerateFiles(TemplateDirectory, "*.yml")
        .Select(Path.GetFileName)
        .OfType<string>()
        .Order(StringComparer.Ordinal)
        .ToList();

    private static string TemplateText(string template)
        => File.ReadAllText(Path.Combine(TemplateDirectory, template));

    private static List<string> PageLines()
        => File.ReadAllLines(Path.Combine(DocumeCli.RepoRoot, PagePath)).ToList();

    private static string TemplateDirectory
        => Path.Combine(DocumeCli.RepoRoot, "templates", "workflows");
}
