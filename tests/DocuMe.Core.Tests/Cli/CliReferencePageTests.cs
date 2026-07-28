using System.Text.RegularExpressions;
using DocuMe.Core.Drift;
using Shouldly;

namespace DocuMe.Core.Tests.Cli;

/// <summary>
/// Pins the prose that tells someone to run <c>docume</c> to the command surface the CLI actually
/// has: the reference page's option tables in both directions, and every <c>docume …</c> invocation
/// in a consumer-facing file.
/// </summary>
/// <remarks>
/// <para>
/// Written after the pattern that had already shipped two consumer-facing bugs: <b>when a capability
/// has two descriptions, fixing one does not fix the other, and nothing cross-checks them.</b> The
/// CLI declares its options in <c>src/DocuMe.Cli/Commands/</c>; <c>docs/wiki/20-reference/cli.md</c>
/// describes them again for a reader, and it is published to Confluence as DocuMe's own wiki. Its
/// first run found <c>--allow-protected-space</c> missing from both the <c>drift</c> and the
/// <c>dashboard</c> table — the one-run escape from the write lock that CLAUDE.md §0.1 and rule §1.4
/// rest on, undocumented on exactly the two write paths a reader would need it for.
/// </para>
/// <para>
/// The surface of record is the CLI's own <c>--help</c>, read from the process (the suite never
/// references the CLI assembly — see <see cref="DocumeCli"/>). Only the <b>option column</b> of the
/// <c>Options:</c> block counts: option names also appear in description PROSE
/// (<c>--changed-since</c> cites <c>git diff --name-only</c>, <c>--state</c> cites
/// <c>docume sync --labels</c>), and a scan of the whole help text reports both as declared options
/// that do not exist.
/// </para>
/// </remarks>
public sealed partial class CliReferencePageTests
{
    private static readonly string[] ReferencePagePath = ["docs", "wiki", "20-reference", "cli.md"];

    /// <summary>The same page as a wiki-root-relative path: the key <see cref="DriftPlanner"/> reports.</summary>
    private const string WikiPath = "20-reference/cli.md";

    /// <summary>
    /// Documented once in page-wide prose rather than in each command's table: the two
    /// <c>--config</c>/<c>--state</c> paragraphs cover every command that takes them, and the root
    /// adds <c>--help</c>/<c>--version</c>. Exempt from the per-table requirement, nothing else.
    /// </summary>
    /// <remarks>
    /// Paired with the prose it rests on by
    /// <see cref="The_options_exempted_as_page_wide_are_real_and_documented_page_wide"/>, because an
    /// exemption is only true while the page keeps its half of the bargain. Half of this list was
    /// already pinned that way from the other side — <c>Packaging.ChangelogTests.Builtins</c> requires
    /// this page to name <c>--help</c> and <c>--version</c> — and the halves nothing covered were
    /// <c>--config</c> and <c>--state</c>: the two options this list excuses from all seven tables.
    /// </remarks>
    private static readonly HashSet<string> PageWideOptions =
        new(["--config", "--state", "--help", "--version"], StringComparer.Ordinal);

    /// <summary>
    /// Files that TELL SOMEONE to run the tool, so a stale flag in one costs a consumer a failed run.
    /// The someone is not always human: <c>.claude/rules/</c> and <c>.claude/references/</c> are loaded
    /// into every agent session in this repo (CLAUDE.md, "Standards Reference"), and an agent that
    /// follows a renamed flag out of one fails exactly the way a reader does.
    /// </summary>
    /// <remarks>
    /// Deliberately not the whole repo, and the complement is <see cref="NarrativeRoots"/> rather than a
    /// sentence: scanning what narrates the build buries the real gaps under prose that happens to
    /// contain the word "docume". Paired against the tree's own invocations, both ways, by
    /// <see cref="Every_file_that_names_an_invocation_is_swept_or_declared_narrative"/>.
    /// </remarks>
    private static readonly string[] InstructionRoots =
    [
        "README.md",
        "docs/wiki",
        "plugin",
        "templates",
        "actions",
        ".github/workflows",
        "schema",
        ".claude/rules",
        ".claude/references",
    ];

    /// <summary>
    /// The complement: paths that NARRATE the build rather than instruct anyone, each classified on
    /// purpose. Not a second sweep and not a second definition of "consumer-facing" — its only job is to
    /// account, together with <see cref="InstructionRoots"/>, for every file in the tree that names a
    /// <c>docume</c> invocation, so a root can leave the sweep only by being written down as something
    /// that narrates the build.
    /// </summary>
    private static readonly string[] NarrativeRoots =
    [
        "PLAN.md",       // the build spec: it declares options that are deliberately not built yet.
        "GATES.md",      // the gate log, which quotes runs the loop is still waiting to be allowed.
        "CHANGELOG.md",  // release notes, where a flag that USED to be spelled that way is the point.
        "docs/plans",    // MTK plan artifacts, written before the surface they describe existed.
        "docs/specs",    // MTK spec artifacts, the same.
        "tasks",         // MTK's lessons and todo list: notes about building DocuMe.
        "tools",         // the build loop's own bookkeeping, archives and method notes.
        "tests",         // this suite, and the golden corpus whose sample prose cites the tool.
    ];

    /// <summary>
    /// Directory names the repo-wide walk passes over: build output, gitignored scratch, and the node
    /// install. None holds a tracked instruction, and <c>node_modules</c> alone would dominate the walk.
    /// </summary>
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.Ordinal) { ".git", ".mtk", "bin", "obj", "node_modules" };

    /// <summary>
    /// One representative file per behaviour this page describes, paired with the sentence resting on
    /// it. The test is "could this sentence become false while <c>src/DocuMe.Cli/</c> stays
    /// byte-identical?" — if yes, the claim is about <c>DocuMe.Core</c> and the page has to credit it,
    /// or a real <c>docume drift</c> run never reports the page when that behaviour moves.
    /// </summary>
    /// <remarks>
    /// Syntax claims are deliberately absent. Which options exist, what each is named and which
    /// command carries it are settled by the three tables above against the CLI's own <c>--help</c>,
    /// and they move only when a <c>Commands/</c> file does. What is listed here is the prose
    /// underneath: what a run computes, what it refuses, what it defaults to.
    /// </remarks>
    private static readonly (string Claim, string File)[] ClaimSources =
    [

        // "Every write path in that table is gated on confluence.protectedSpaces … all refuse it."
        ("the write lock every write path checks", "src/DocuMe.Core/Publishing/PublishGuard.cs"),

        // "--changed-since … including pages whose images changed. The whole tree is still loaded."
        ("what --changed-since narrows a run to", "src/DocuMe.Core/Publishing/PublishScope.cs"),

        // "--prune … Asks first, needs a terminal, refuses to run in CI."
        ("--prune's terminal-and-not-CI refusal", "src/DocuMe.Core/Publishing/PruneGuard.cs"),

        // The WARNING: "An orphan is a state entry whose markdown file is gone."
        ("the orphan definition the --prune warning gives", "src/DocuMe.Core/Publishing/PrunePlan.cs"),

        // "--no-reorder: Skip the pass that puts each parent's children in source-tree order."
        ("the child-order pass --no-reorder skips", "src/DocuMe.Core/Publishing/ChildOrderPlanner.cs"),

        // "Leave a page alone and exit non-zero … Default is to publish and warn."
        ("--block-on-open-comments and its default", "src/DocuMe.Core/Publishing/OpenCommentGuard.cs"),

        // "Exit code 0 means the corpus clears the acceptance bar."
        ("convert's exit code", "src/DocuMe.Core/Acceptance/AcceptanceReport.cs"),

        // "--accept … counted as a note instead of a warning."
        ("--accept turning a warning into a note", "src/DocuMe.Core/Acceptance/AcceptancePolicy.cs"),

        // "--render-mermaid … report the ones that fail. Off by default: one process per diagram."
        ("--render-mermaid's per-diagram render", "src/DocuMe.Core/Acceptance/MermaidAcceptance.cs"),

        // "Idempotent: an existing file is never overwritten, and every skip is reported" (rule §9.4).
        ("init's idempotence and its reported skips", "src/DocuMe.Core/Scaffolding/ProjectScaffolder.cs"),

        // "--adopt: Build _meta/state.json from the wiki this repo already has, one entry per page."
        ("--adopt's one entry per page", "src/DocuMe.Core/Scaffolding/WikiAdopter.cs"),

        // "--legacy-map: Path to a JSON `page path → page id` map."
        ("--legacy-map's page-path-to-id map", "src/DocuMe.Core/Scaffolding/LegacyPageMap.cs"),

        // "--reply: Post a reply under every triaged inbox item and resolve the inline comments it answers."
        ("--reply's post-and-resolve", "src/DocuMe.Core/Feedback/FeedbackReplyPlanner.cs"),

        // "--output-dir … Defaults to <wiki.root>/_meta/feedback/inbox."
        ("--output-dir's default inbox path", "src/DocuMe.Core/Feedback/FeedbackInbox.cs"),

        // "--comments: Ingest page comments into the feedback inbox."
        ("--comments' ingest into the inbox", "src/DocuMe.Core/Feedback/FeedbackInboxPlanner.cs"),

        // "--labels: Reconcile the approved/stale labels into state", and the dashboard section's "the
        // labels are reconciled in memory here; docume sync --labels is what writes them into state".
        ("which half writes labels into state", "src/DocuMe.Core/Sync/LabelSyncPlanner.cs"),

        // "Reports which pages derive from code changed between two revisions."
        ("what drift reports", "src/DocuMe.Core/Drift/DriftPlanner.cs"),

        // "--format <shape>: table, json, or github-comment."
        ("the github-comment shape", "src/DocuMe.Core/Drift/DriftComment.cs"),

        // "--mark: Add the stale label to affected pages, set stale: true in state, refresh the dashboard."
        ("--mark's label, state flag and dashboard refresh", "src/DocuMe.Core/Drift/DriftMarkPlanner.cs"),

        // "Regenerates the status page from state plus the live labels."
        ("what the dashboard is built from", "src/DocuMe.Core/Dashboard/DashboardPublisher.cs"),

        // "Reports what is published, what drifted, and whether this repo is set up to publish at all."
        ("what status reports", "src/DocuMe.Core/Status/StatusModel.cs"),

        // "--offline: Skip the single Confluence request — the token and space probe." A COUNTED claim:
        // a second probe added here makes "the single request" false and nothing else notices.
        ("--offline's single skipped request", "src/DocuMe.Core/Status/StatusProbes.cs"),

        // "--json: Print the report as JSON and nothing else, for a pull-request body or a CI step."
        ("--json's report shape", "src/DocuMe.Core/Status/StatusReport.cs"),

        // "pages touched since a commit" and drift's "between two revisions" are both a git diff.
        ("the revision pair the two commands diff", "src/DocuMe.Core/Git/GitRepository.cs"),

        // "--baseline <rev>: Revision to diff from. Defaults to state.baselineSha" — a field name.
        ("--baseline's state.baselineSha default", "src/DocuMe.Core/State/DocumeState.cs"),
    ];

    [Fact]
    public void The_reference_page_has_a_section_for_every_command_and_no_others()
    {
        var documented = PageSections().Keys.ToHashSet(StringComparer.Ordinal);
        const string because = "docs/wiki/20-reference/cli.md documents a different set of commands "
            + "than `docume --help` offers. A reader of DocuMe's own wiki gets a command that does "
            + "not exist, or never learns about one that does.";

        documented.ShouldBe(ShippedCommands(), ignoreOrder: true, customMessage: because);
    }

    [Fact]
    public void Every_declared_option_is_in_its_commands_option_table()
    {
        var sections = PageSections();
        var missing = new List<string>();

        foreach (var command in ShippedCommands().OrderBy(name => name, StringComparer.Ordinal))
        {
            var tabled = TabledOptions(sections[command]);

            var undocumented = DeclaredOptions(command)
                .Where(option => !PageWideOptions.Contains(option))
                .Where(option => !tabled.Contains(option))
                .OrderBy(option => option, StringComparer.Ordinal);

            missing.AddRange(undocumented.Select(option => $"{command} {option}"));
        }

        var because = "These options are declared by the CLI and absent from their command's option "
            + $"table in docs/wiki/20-reference/cli.md: {string.Join(", ", missing)}. That page is "
            + "published to Confluence as DocuMe's own reference, so a reader cannot discover them.";

        missing.ShouldBeEmpty(because);
    }

    [Fact]
    public void Every_option_the_reference_page_documents_is_one_the_command_declares()
    {
        var sections = PageSections();
        var phantom = new List<string>();

        foreach (var command in ShippedCommands().OrderBy(name => name, StringComparer.Ordinal))
        {
            var declared = DeclaredOptions(command);

            var invented = TabledOptions(sections[command])
                .Where(option => !declared.Contains(option))
                .OrderBy(option => option, StringComparer.Ordinal);

            phantom.AddRange(invented.Select(option => $"{command} {option}"));
        }

        var because = "The reference page documents options the CLI does not have: "
            + $"{string.Join(", ", phantom)}. Either the page is stale or the option was renamed "
            + "without it; a reader following the page gets a parse error.";

        phantom.ShouldBeEmpty(because);
    }

    /// <summary>
    /// The exemption above is the one way an option may be missing from its command's table, and it
    /// buys that with a promise about the page: the option is documented once, page-wide, instead.
    /// Nothing collected on that promise, so the widest exemption in this class rested on prose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both directions were silent, measured rather than argued
    /// (<c>.mtk/paths-197/mutate-page-wide-exemption.py</c>, 8 cells, full suite each). Deleting the
    /// <c>--config</c> sentence from the page left the flag exempted from all seven tables and named
    /// nowhere on a reference page published to Confluence: green. Adding <c>--dry-run</c> to the list
    /// while deleting its row from <c>publish</c>'s table — silencing a real undocumented flag by
    /// widening the exemption — was green too, and so was an entry for an option no command declares.
    /// </para>
    /// <para>
    /// The same mutation against <c>--help</c> was CAUGHT, by
    /// <c>Packaging.ChangelogTests.The_flags_exempted_as_built_ins_are_built_in</c>: the sibling list
    /// covering two of these four flags was pinned to this page and this one was not. Page-wide is
    /// scoped to the text above the first command section on purpose — a mention inside one command's
    /// section documents that command, which is what the exemption claims not to need.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_options_exempted_as_page_wide_are_real_and_documented_page_wide()
    {
        var declared = ShippedCommands()
            .SelectMany(DeclaredOptions)
            .Concat(RootOptions())
            .ToHashSet(StringComparer.Ordinal);

        var dead = PageWideOptions
            .Where(option => !declared.Contains(option))
            .Order(StringComparer.Ordinal)
            .ToList();

        dead.ShouldBeEmpty(
            $"[{string.Join(", ", dead)}] are exempted from every command's option table as page-wide "
            + "options, and neither the root nor any command declares them. The exemption describes a "
            + "surface that has moved, and it reads to a reviewer as though it still covered something.");

        var preamble = PageWidePreamble();

        var undocumented = PageWideOptions
            .Where(option => !preamble.Contains($"`{option}`", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        undocumented.ShouldBeEmpty(
            $"[{string.Join(", ", undocumented)}] are excused from every command's option table "
            + "because docs/wiki/20-reference/cli.md documents them page-wide instead, and its "
            + "page-wide prose does not name them. A reader of DocuMe's own reference now finds them "
            + "in no table and in no paragraph. Restore the sentence, or drop the exemption so the "
            + "option has to appear in each command's table like every other one.");
    }

    [Fact]
    public void Every_documented_invocation_names_a_real_command_with_real_options()
    {
        var shipped = ShippedCommands();
        var declared = shipped.ToDictionary(
            command => command,
            DeclaredOptions,
            StringComparer.Ordinal);

        var gaps = new List<string>();

        foreach (var (path, invocations) in DocumentedInvocations())
        {
            foreach (var (line, invocation) in invocations)
            {
                var tokens = invocation.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var command = tokens[0];

                if (!shipped.Contains(command))
                {
                    gaps.Add($"{path}:{line} — no `{command}` command (`docume {invocation}`)");
                    continue;
                }

                var unknown = tokens.Skip(1)
                    .Where(token => token.StartsWith("--", StringComparison.Ordinal))
                    .Select(token => token.Split('=')[0])
                    .Where(option => !IsPlaceholder(option))
                    .Where(option => !declared[command].Contains(option));

                gaps.AddRange(unknown.Select(option =>
                    $"{path}:{line} — `{command}` has no `{option}` (`docume {invocation}`)"));
            }
        }

        var because = "These files tell a reader or an agent to run something the CLI cannot parse:"
            + Environment.NewLine + string.Join(Environment.NewLine, gaps);

        gaps.ShouldBeEmpty(because);
    }

    /// <summary>
    /// The sweep above passes trivially if the scan matches nothing, and its regex has to survive
    /// escaped backticks, line continuations and yaml block scalars. This fails when it goes blind.
    /// </summary>
    [Fact]
    public void The_invocation_scan_reaches_the_files_that_carry_the_instructions()
    {
        var found = DocumentedInvocations();
        var scanned = found.Keys.ToHashSet(StringComparer.Ordinal);

        // Each of these tells someone to run the tool, in a different syntax the scan has to survive:
        // markdown fences, a SKILL.md's prose, and a yaml `run:` block scalar.
        string[] mustReach =
        [
            "README.md",
            "docs/wiki/20-reference/cli.md",
            "plugin/skills/docs-loop/SKILL.md",
            "templates/workflows/docs-publish.yml",
        ];

        foreach (var path in mustReach)
        {
            var blind = $"The invocation scan found no `docume …` in {path}, which is full of them. "
                + "Its regex has gone blind, and the sweep that depends on it is now passing on an "
                + "empty set.";

            scanned.ShouldContain(path, blind);
        }

        var total = found.Values.Sum(list => list.Count);
        var collapsed = $"The invocation scan found only {total} invocations across the repo's "
            + "consumer-facing files. It found 132 when this test was written; a collapse that large "
            + "means the scan broke, not that the docs shrank.";

        total.ShouldBeGreaterThan(100, collapsed);
    }

    /// <summary>
    /// The silent failure the three table checks above cannot see. They police the option <em>names</em>
    /// against <c>--help</c>, which is the CLI's surface; the prose describing what each option
    /// <em>does</em> is <c>DocuMe.Core</c> behaviour, and <c>sources</c> is the only thing tying the page
    /// to it. A subsystem this page describes but does not credit is one the page stops tracking the day
    /// its code moves — no error, no warning, and a reference page that looks maintained forever.
    /// </summary>
    /// <remarks>
    /// Asked of <see cref="DriftPlanner"/>, the planner a real <c>docume drift</c> run uses, never of the
    /// frontmatter: reading the frontmatter is the check that passed while nine subsystems were blind.
    /// The blanket <c>src/DocuMe.Core/**</c> on the index pages is what hid it — every change reported
    /// <em>something</em>, so <see cref="Acceptance.DogfoodWikiTests"/>'s "every shipped path reaches some
    /// page's globs" sweep stayed green. This is the shape
    /// <see cref="State.ApprovalAndDriftPageTests.Every_subsystem_the_page_explains_is_reported_when_its_code_changes"/>
    /// established.
    /// </remarks>
    [Fact]
    public void Every_behaviour_the_page_describes_is_reported_when_its_code_changes()
    {
        var pages = DocuMe.Core.Markdown.WikiTree.Load(Path.Combine(RepoRoot, "docs", "wiki")).Pages;
        var blind = new List<string>();

        pages.ShouldNotBeEmpty("The wiki loaded no pages, so every trial below would report blind.");

        foreach (var (claim, file) in ClaimSources)
        {
            var report = DriftPlanner.Plan("baseline", "head", [file], pages);
            var reached = report.Pages.Select(page => page.Path).ToList();

            if (!reached.Contains(WikiPath, StringComparer.Ordinal))
            {
                blind.Add($"{claim}: a change to {file} reports [{string.Join(", ", reached)}]");
            }
        }

        const string because =
            "docs/wiki/20-reference/cli.md describes behaviour whose code reaches no glob in its "
            + "`sources`, so `docume drift` never reports the page when that code moves. Nothing "
            + "fails and no reader sees anything wrong; the page just silently stops being "
            + "maintained. Claims the page makes and drift does not reach:";

        blind.ShouldBeEmpty(because);
    }

    /// <summary>
    /// Every file in the tree that names an invocation is accounted for by the two declared lists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="InstructionRoots"/> is a literal that bounds which files the sweep above reads, and
    /// nothing paired it with the tree it describes. Both directions were silent. A consumer-facing file
    /// under a root the literal does not name was simply never read; and a root deleted from the literal
    /// took its files out of the sweep while the <c>&gt; 100</c> floor in
    /// <see cref="The_invocation_scan_reaches_the_files_that_carry_the_instructions"/> still held —
    /// measured for <c>actions</c>, <c>schema</c> and <c>.github/workflows</c>, which together carry 7 of
    /// the 146 invocations, so dropping any one of them cost nothing.
    /// </para>
    /// <para>
    /// The authority is not a second literal: it is every <c>docume …</c> invocation in the tree, found by
    /// the same scan the sweep runs. <see cref="NarrativeRoots"/> is what keeps the assertion a
    /// classification rather than a demand that the whole repo be consumer-facing.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_file_that_names_an_invocation_is_swept_or_declared_narrative()
    {
        var carriers = InvocationCarriers();

        // Each direction below is the other's vacuity guard. This floor covers the case none of them
        // does: a walk that found nothing satisfies all three (iter175 — assert the denominator).
        carriers.Count.ShouldBeGreaterThan(InstructionRoots.Length + NarrativeRoots.Length);

        const string unclassified =
            "A file names a `docume` invocation and is under neither InstructionRoots nor "
            + "NarrativeRoots. The sweep above reads InstructionRoots and passes over everything else "
            + "without a word, so nothing checks this file's invocations against the CLI: a flag renamed "
            + "out from under it costs whoever follows it a failed run. Add its root to InstructionRoots "
            + "if it instructs someone, or to NarrativeRoots with the reason. Unswept and undeclared:";

        carriers
            .Where(file => !Covered(file, InstructionRoots))
            .Where(file => !Covered(file, NarrativeRoots))
            .ShouldBeEmpty(unclassified);

        const string vanished =
            "A declared root is not on disk, so the list holding it describes a tree that has moved. An "
            + "instruction root that vanished takes its files out of the sweep; a narrative one launders "
            + "an exemption nothing needs any more. Declared but absent:";

        InstructionRoots
            .Concat(NarrativeRoots)
            .Where(root => !OnDisk(root))
            .ShouldBeEmpty(vanished);

        const string laundered =
            "A narrative root is at or inside an instruction root, which excuses nothing and reads to a "
            + "reviewer as though it did: InstructionFiles() enumerates the instruction root, so those "
            + "files are swept either way. (The reverse nesting is deliberate and allowed — an "
            + "instruction root inside a narrative one is a subtree that does instruct.) Decoration:";

        Laundered().ShouldBeEmpty(laundered);
    }

    /// <summary>
    /// The sweep reads a file only where the extension filter matches, so that pattern is an exemption:
    /// a file it does not match leaves <c>DocumentedInvocations()</c> without a word. Nothing collected
    /// on it, and the one check that would have —
    /// <see cref="Every_file_that_names_an_invocation_is_swept_or_declared_narrative"/> — bounds its own
    /// walk with the same literal, so a branch taken out of the pattern leaves both nets looking through
    /// the same hole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both silent directions were measured rather than argued, full suite per cell
    /// (<c>.mtk/paths-198/mutate-extension-bound.py</c>, 6 cells, twice). Taking <c>json</c> out of the
    /// alternation was green: <c>schema</c> is a declared instruction root, its schema is the only
    /// <c>.json</c> carrier, and the <c>docume drift --mark</c> it names simply left the sweep. And
    /// <c>templates/tools/render-mermaid.mjs</c> — the renderer <c>docume init</c> writes into every
    /// consumer repo — names <c>docume publish</c> twice; giving one of those a flag the CLI does not
    /// have was green too, because <c>.mjs</c> was not in the pattern. Dropping <c>md</c> or <c>yml</c>
    /// was already caught by the floor above, so only the small branches were free.
    /// </para>
    /// <para>
    /// Scoped to the nine declared roots rather than the whole tree on purpose: those are the paths this
    /// class has already called consumer-facing, so a file sitting there and escaping the sweep on its
    /// extension alone is the harm worth failing on. A carrier under an undeclared root is the sibling
    /// check's job, and it is still bounded by this same filter — recorded, not fixed here.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_carrier_inside_a_declared_instruction_root_is_one_the_sweep_reads()
    {
        var (read, escaped) = RootCarriers();

        // The denominator: both assertions below pass on a walk that found nothing (iter175).
        read.Count.ShouldBeGreaterThan(InstructionRoots.Length);

        const string unswept =
            "A file inside a declared instruction root names a `docume` invocation and the sweep does "
            + "not read it: the extension filter does not match it, so DocumentedInvocations() drops it "
            + "and the population check that audits that sweep drops it too, both bounded by that one "
            + "pattern. Whoever follows the invocation in this file gets no warning the day the flag is "
            + "renamed out from under it. Add the extension to Instructional(), or move the file out of "
            + "InstructionRoots. Unswept:";

        escaped.ShouldBeEmpty(unswept);

        const string drifted =
            "The extension-blind walk of the instruction roots and the sweep itself no longer agree on "
            + "which files carry invocations. One of the two has gone blind, and whichever it is, the "
            + "assertion above is now measuring the other one's population instead of its own.";

        read.ShouldBe(DocumentedInvocations().Keys, ignoreOrder: true, customMessage: drifted);
    }

    /// <summary>
    /// The claim table is hand-listed, so it rots in two directions: a file renamed out from under a row
    /// makes that row's trial report blind for a reason that has nothing to do with the page, and a row
    /// quietly deleted shrinks the sweep without failing it.
    /// </summary>
    [Fact]
    public void The_claim_table_still_names_files_that_exist()
    {
        const string missing = "A file in the claim table no longer exists, so its trial in "
            + nameof(Every_behaviour_the_page_describes_is_reported_when_its_code_changes)
            + " reports blind for a reason that has nothing to do with the page's `sources`.";

        ClaimSources.Length.ShouldBe(25);
        ClaimSources.ShouldAllBe(claim => File.Exists(Path.Combine(RepoRoot, Native(claim.File))), missing);
    }

    /// <summary>An <c>ALL-CAPS</c> or bracketed stand-in in prose claims nothing about the surface.</summary>
    private static bool IsPlaceholder(string option) =>
        option.Length > 2 && (char.IsUpper(option[2]) || option[2] is '<' or '{' or '$');

    /// <summary>The subcommands the root help offers, which is the set a reader can actually reach.</summary>
    private static HashSet<string> ShippedCommands()
    {
        var run = Help();
        var block = Section(run, "Commands:");

        var names = block
            .Select(line => line.Trim().Split(' ', 2)[0])
            .Where(name => name.Length > 0);

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>
    /// The options one command declares, read out of the option column of its own <c>--help</c>.
    /// Alias lists (<c>-?, -h, --help</c>) contribute only their long forms; the page tables and every
    /// documented invocation use those.
    /// </summary>
    private static HashSet<string> DeclaredOptions(string command) =>
        ParsedOptions(Help(command), $"docume {command} --help");

    /// <summary>
    /// The root's own options. Read separately because <c>--version</c> is declared here and by no
    /// subcommand, so a check that only asked the subcommands would call it a dead exemption.
    /// </summary>
    private static HashSet<string> RootOptions() => ParsedOptions(Help(), "docume --help");

    private static HashSet<string> ParsedOptions(CliRun run, string invocation)
    {
        var options = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in Section(run, "Options:"))
        {
            // Two-or-more spaces separate the option column from its description. A wrapped
            // description line contributes nothing: none of its words start with "--".
            var column = OptionColumn().Match(line);

            if (!column.Success)
            {
                continue;
            }

            var aliases = column.Groups["aliases"].Value.Split(',');

            foreach (var alias in aliases)
            {
                var name = alias.Trim().Split(' ')[0];

                if (name.StartsWith("--", StringComparison.Ordinal))
                {
                    options.Add(name);
                }
            }
        }

        options.ShouldNotBeEmpty($"Parsed no options at all out of `{invocation}`.");

        return options;
    }

    /// <summary>
    /// The page-wide prose: everything above the first command section, which is the only text on the
    /// page that speaks for every command at once.
    /// </summary>
    private static string PageWidePreamble()
    {
        var page = File.ReadAllText(Path.Combine([RepoRoot, .. ReferencePagePath]));
        var first = page.IndexOf("\n## `docume ", StringComparison.Ordinal);

        const string because = "No \"## `docume <command>`\" heading in docs/wiki/20-reference/cli.md, "
            + "so the whole page would count as page-wide prose and every exemption below would be "
            + "satisfied by a mention inside a single command's section.";

        first.ShouldBeGreaterThan(0, because);

        return page[..first];
    }

    /// <summary>
    /// The reference page split by its <c>## `docume &lt;command&gt;`</c> headings, so an option found
    /// in one command's table cannot count as documenting another's.
    /// </summary>
    private static Dictionary<string, string> PageSections()
    {
        var page = File.ReadAllText(Path.Combine([RepoRoot, .. ReferencePagePath]));
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var match in CommandSection().Matches(page).Cast<Match>())
        {
            sections[match.Groups["name"].Value] = match.Groups["body"].Value;
        }

        const string because = "Parsed no \"## `docume <command>`\" headings out of "
            + "docs/wiki/20-reference/cli.md, so every assertion against it would pass on nothing.";

        sections.ShouldNotBeEmpty(because);

        return sections;
    }

    /// <summary>The options named in the leading cell of a markdown table row inside one section.</summary>
    private static HashSet<string> TabledOptions(string section)
    {
        var names = TableRowOption().Matches(section)
            .Cast<Match>()
            .Select(match => match.Groups["name"].Value);

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every <c>docume …</c> invocation in a consumer-facing file, as repo-relative path to
    /// (line, argument string).
    /// </summary>
    private static Dictionary<string, List<(int Line, string Invocation)>> DocumentedInvocations()
    {
        var found = new Dictionary<string, List<(int, string)>>(StringComparer.Ordinal);

        foreach (var file in InstructionFiles())
        {
            var relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
            var kept = KeptInvocations(File.ReadAllLines(file));

            if (kept.Count > 0)
            {
                found[relative] = kept;
            }
        }

        return found;
    }

    /// <summary>
    /// The invocations one file's lines claim about the CLI's surface. Shared with the population check
    /// below on purpose: two definitions of "a real invocation" would drift, which is the defect shape
    /// this whole class exists for.
    /// </summary>
    private static List<(int Line, string Invocation)> KeptInvocations(string[] lines)
    {
        var kept = new List<(int, string)>();

        foreach (var (line, text) in LogicalLines(lines))
        {
            foreach (var match in Invocation().Matches(text).Cast<Match>())
            {
                var invocation = Normalize(match.Groups["args"].Value);

                // "docume <command>" and a bare "docume --help" claim nothing about the surface.
                if (invocation.Length == 0
                    || invocation[0] is '<' or '{' or '[' or '$'
                    || invocation.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                kept.Add((line, invocation));
            }
        }

        return kept;
    }

    /// <summary>
    /// Repo-relative paths of every file in the tree naming at least one invocation — the authority the
    /// population check pairs the two declared lists against.
    /// </summary>
    private static List<string> InvocationCarriers()
    {
        var carriers = new List<string>();

        foreach (var file in WalkedFiles())
        {
            if (!Instructional().IsMatch(Path.GetExtension(file)))
            {
                continue;
            }

            if (KeptInvocations(File.ReadAllLines(file)).Count > 0)
            {
                carriers.Add(Path.GetRelativePath(RepoRoot, file).Replace('\\', '/'));
            }
        }

        return carriers;
    }

    /// <summary>
    /// The whole tree, minus <see cref="SkippedDirectories"/>. Pruned while descending rather than
    /// filtered afterwards: <c>node_modules</c> and <c>.git</c> are most of the files on disk.
    /// </summary>
    private static IEnumerable<string> WalkedFiles()
    {
        var pending = new Stack<string>();
        pending.Push(RepoRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            var descend = Directory.EnumerateDirectories(directory)
                .Where(child => !SkippedDirectories.Contains(Path.GetFileName(child)));

            foreach (var child in descend)
            {
                pending.Push(child);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                yield return file;
            }
        }
    }

    /// <summary>Is this repo-relative file at or under one of the declared roots?</summary>
    private static bool Covered(string file, string[] roots) =>
        roots.Any(root => string.Equals(file, root, StringComparison.Ordinal)
            || file.StartsWith(root + "/", StringComparison.Ordinal));

    private static bool OnDisk(string root)
    {
        var native = Path.Combine(RepoRoot, Native(root));

        return File.Exists(native) || Directory.Exists(native);
    }

    /// <summary>Narrative roots that cover files an instruction root already sweeps.</summary>
    private static List<string> Laundered() =>
        NarrativeRoots
            .Where(narrative => Covered(narrative, InstructionRoots))
            .ToList();

    /// <summary>Trailing shell comment dropped, and markdown/prose punctuation off the last token.</summary>
    private static string Normalize(string arguments)
    {
        var text = arguments.Trim();
        var comment = text.IndexOf('#', StringComparison.Ordinal);

        if (comment >= 0)
        {
            text = text[..comment];
        }

        return WhitespaceRun().Replace(text, " ").Trim().TrimEnd(']', '.', ',', ':', ')');
    }

    /// <summary>
    /// Physical lines folded on the shell's <c>\</c> continuation, since one <c>docume</c> call in a
    /// workflow's <c>run:</c> block is spread over several of them. The line number reported is the
    /// first physical line, which is where a reader has to look.
    /// </summary>
    private static IEnumerable<(int Line, string Text)> LogicalLines(string[] lines)
    {
        var index = 0;

        while (index < lines.Length)
        {
            var text = lines[index];
            var start = index + 1;

            while (text.EndsWith('\\') && index + 1 < lines.Length)
            {
                index++;
                text = string.Concat(text.AsSpan(0, text.Length - 1), " ", lines[index].Trim());
            }

            index++;

            yield return (start, text);
        }
    }

    /// <summary>The files the sweep reads: the declared roots, filtered by extension.</summary>
    private static IEnumerable<string> InstructionFiles() =>
        RootFiles().Where(file => Instructional().IsMatch(Path.GetExtension(file)));

    /// <summary>
    /// Every file under a declared instruction root, whatever its extension. The one enumeration of
    /// those roots: the sweep above is this filtered by extension, so the check that the filter
    /// excuses nothing cannot end up reading a different tree than the sweep does.
    /// </summary>
    private static IEnumerable<string> RootFiles()
    {
        foreach (var root in InstructionRoots)
        {
            var path = Path.Combine(RepoRoot, Native(root));

            if (File.Exists(path))
            {
                yield return path;
                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Every file inside a declared instruction root that names an invocation, split by whether the
    /// extension filter lets the sweep read it.
    /// </summary>
    private static (List<string> Read, List<string> Escaped) RootCarriers()
    {
        var read = new List<string>();
        var escaped = new List<string>();

        foreach (var file in RootFiles())
        {
            if (KeptInvocations(File.ReadAllLines(file)).Count == 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');

            if (Instructional().IsMatch(Path.GetExtension(file)))
            {
                read.Add(relative);
                continue;
            }

            escaped.Add(relative);
        }

        return (read, escaped);
    }

    /// <summary>The indented lines under a named help header, up to the first that is not one.</summary>
    private static List<string> Section(CliRun run, string header)
    {
        var lines = run.Output.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var index = lines.FindIndex(line => line.StartsWith(header, StringComparison.Ordinal));

        index.ShouldBeGreaterThanOrEqualTo(
            0,
            $"No \"{header}\" section in the help.{Environment.NewLine}{run.Diagnostics}");

        return lines.Skip(index + 1)
            .TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToList();
    }

    private static CliRun Help(params string[] command)
    {
        var run = DocumeCli.Invoke(RepoRoot, [.. command, "--help"]);

        run.Code.ShouldBe(0, run.Diagnostics);

        return run;
    }

    private static string RepoRoot => DocumeCli.RepoRoot;

    private static string Native(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    [GeneratedRegex(@"^ {2}(?<aliases>\S[^\n]*?)(?: {2,}|$)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionColumn();

    [GeneratedRegex(@"\n## `docume (?<name>[a-z-]+)[^`]*`\n(?<body>[\s\S]*?)(?=\n## |$)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CommandSection();

    [GeneratedRegex(@"^\|\s*`(?<name>--[a-z0-9-]+)", RegexOptions.ExplicitCapture | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TableRowOption();

    // Stops at any shell separator, a closing quote or paren, an html tag, or a backslash: the
    // workflows cite the tool inside double-quoted `echo` strings as \`docume init\`, and that
    // escaped backtick ends the citation rather than continuing the command.
    [GeneratedRegex(@"(?:^|[\s`(""'>*-])docume (?<args>[^\n`|;&)""'<\\]*)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Invocation();

    // `.mjs` is here for one shipped file: templates/tools/render-mermaid.mjs, which `docume init`
    // scaffolds into a consumer repo and which names `docume publish` in its own header comments.
    [GeneratedRegex(@"^\.(md|ya?ml|json|mjs)$", RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Instructional();

    [GeneratedRegex(@"\s+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceRun();
}
