using DocuMe.Core.Tests.Cli;
using Shouldly;
using YamlDotNet.RepresentationModel;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// Rule §9.6's "never runs in CI" half, asserted over every file CI can execute rather than over the
/// six that run in nobody's.
/// </summary>
/// <remarks>
/// <para>
/// The rule has two halves and they are held in different places. The code half is a computation:
/// <c>PruneGuard.Refusal</c> refuses once the CI variables are set, and
/// <see cref="PruneConfirmationCoverageTests"/> holds every caller in <c>src/</c> to it. The
/// shipped-file half is this one — no file this repo ships may <em>tell</em> CI to run a prune,
/// because a step written against a refusal is a step whose author discovers the rule from a red
/// check in somebody else's repository.
/// </para>
/// <para>
/// That half had exactly one assertion, <c>WorkflowTemplateTests.No_template_passes_prune</c>, whose
/// population is the six files under <c>templates/workflows/</c>. Those are scaffolding: they run in
/// nobody's CI until a consumer copies them. Measured before this class existed, with the whole suite
/// run once per edit: adding <c>--prune</c> to a runnable line of <c>.github/workflows/ci.yml</c>, to
/// <c>.github/workflows/release.yml</c>, or as a <c>default:</c> on <c>actions/action.yml</c>'s
/// <c>args</c> input left every test green. Those three run in this repo's CI on every push, on every
/// release tag, and — the action being the shipped entry point whose whole body is
/// <c>dotnet tool run docume $ARGS</c> — in every consumer that wires it. So the net punished the one
/// file that runs in no CI and ignored the three that do.
/// </para>
/// <para>
/// The population is derived by SHAPE, not declared: GitHub decides what it will execute by reading
/// the file, so a workflow is a mapping carrying <c>jobs:</c> and an action one carrying <c>runs:</c>.
/// That is deliberately not a second list of paths — a set of roots and a set of exceptions can be
/// kept consistent with each other while both drift away from the tree, and moving a file between
/// them costs nothing.
/// </para>
/// <para>
/// It is paired against GitHub's <em>other</em> convention, the path one: anything named
/// <c>action.yml</c>, anything under a directory named <c>workflows</c>. Two independent authorities,
/// neither of them a list maintained here. The direction asserted is convention ⊆ shape, which is the
/// safety property — a conventionally placed workflow that the shape scan has stopped reading is a
/// hole in the sweep. The reverse is deliberately not asserted: a workflow found by shape outside the
/// conventional directories is unusual rather than wrong, and it is already swept.
/// </para>
/// <para>
/// Deliberately NOT extended to the <c>SKILL.md</c> files, though CI runs two of them headlessly
/// (<c>docs-refresh.yml</c> and <c>docs-feedback.yml</c> each run <c>claude -p</c>). Measured rather
/// than assumed: that same edit inside a skill's bash block already fails
/// <c>SkillContractTests.No_skill_writes_to_Confluence_from_its_own_commands</c>, since rule §0.4
/// forbids a skill publishing at all and <c>--prune</c> exists only on <c>publish</c>. A second net
/// over that surface would remove nothing, and a net that removes nothing should not ship.
/// </para>
/// </remarks>
public sealed class PruneCiReachTests
{
    /// <summary>The flag rule §9.6 keeps out of CI. It exists only on <c>publish</c>.</summary>
    private const string Prune = "--prune";

    /// <summary>The two keys that tell GitHub a YAML file is something it executes.</summary>
    private static readonly string[] ExecutableKeys = ["jobs", "runs"];

    /// <summary>The extensions GitHub reads a workflow or an action from.</summary>
    private static readonly string[] YamlExtensions = [".yml", ".yaml"];

    /// <summary>The filename GitHub requires of a composite action's manifest.</summary>
    private static readonly string[] ActionManifests = ["action.yml", "action.yaml"];

    /// <summary>The directory name GitHub reads workflows from, which this repo also uses for the templates.</summary>
    private const string WorkflowDirectory = "workflows";

    /// <summary>
    /// Directory names the walk passes over: build output, gitignored scratch and the node install.
    /// None holds a file CI executes, and <c>node_modules</c> alone would dominate the walk.
    /// </summary>
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.Ordinal) { ".git", ".mtk", "bin", "obj", "node_modules" };

    /// <summary>
    /// No file CI can execute passes <c>--prune</c> — the assertion a prune step in this repo's own
    /// CI, or in the shipped action, fails.
    /// </summary>
    [Fact]
    public void No_file_CI_can_execute_passes_prune()
    {
        var executable = CiExecutableFiles();

        // Vacuous-pass guard, and the one that matters most here: the two facts in this class share a
        // derivation, so a walk that stopped reading the tree would leave the pairing below comparing
        // two empty sets and reporting the whole CI surface clean.
        const string blind = "Nothing in this tree parses as a file GitHub Actions executes, which cannot be "
            + "true while .github/workflows/ and actions/action.yml ship. The scan has stopped reading "
            + "the tree rather than found the workflows gone.";

        executable.ShouldNotBeEmpty(blind);

        var offenders = new List<string>();

        foreach (var file in executable)
        {
            var passing = Runnable(File.ReadAllText(Path.Combine(DocumeCli.RepoRoot, Native(file))))
                .Where(line => line.Contains(Prune, StringComparison.Ordinal))
                .Select(line => $"{file}: {line.Trim()}");

            offenders.AddRange(passing);
        }

        // Whole-line comments are exempt, for the reason WorkflowTemplateTests gives and because in a
        // yaml file the prose and the code share one file: docs-publish.yml says out loud that the flag
        // is absent and must stay absent, and that sentence is why the next editor does not add it back.
        const string reached = "A file CI executes passes `--prune`. Rule §9.6: orphan deletion is confirmed "
            + "interactively and never runs in CI, so PruneGuard refuses it there — the step would be "
            + "written against a refusal, and a CI run is the most expensive place to learn that. If the "
            + "orphans really should go, run the prune from a terminal. Passing it:";

        offenders.ShouldBeEmpty(reached);
    }

    /// <summary>
    /// Every file GitHub's path convention names is one the shape scan found — the assertion a sweep
    /// that has gone blind fails instead of passing quietly.
    /// </summary>
    [Fact]
    public void Every_file_GitHubs_path_convention_names_is_one_the_shape_scan_found()
    {
        var found = CiExecutableFiles();
        var conventional = ConventionalFiles();

        const string missing = "No file in this tree sits where GitHub Actions looks for one, which cannot be "
            + "true while .github/workflows/ ships. This pairing has stopped finding its own population.";

        conventional.ShouldNotBeEmpty(missing);

        var unread = conventional.Except(found, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        const string escaped = "A file sits where GitHub Actions executes it, and the shape scan in this class "
            + "did not recognise it — so it is not being swept for `--prune` and rule §9.6's CI half does "
            + "not cover it. Either the file no longer parses (GitHub would fail it too, which is worth "
            + "knowing) or the scan needs to learn its shape. Unswept:";

        unread.ShouldBeEmpty(escaped);
    }

    /// <summary>
    /// Every file in the tree that GitHub Actions would execute, as repo-relative paths, judged by the
    /// shape of the file rather than by where it sits.
    /// </summary>
    private static List<string> CiExecutableFiles() => YamlFiles()
        .Where(file => IsActionsFile(File.ReadAllText(file)))
        .Select(Relative)
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Every YAML file the path convention marks as something GitHub executes: a composite action's
    /// manifest by name, a workflow by the directory holding it.
    /// </summary>
    private static List<string> ConventionalFiles() => YamlFiles()
        .Where(IsConventional)
        .Select(Relative)
        .Order(StringComparer.Ordinal)
        .ToList();

    private static bool IsConventional(string file) =>
        ActionManifests.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
        || string.Equals(
            Path.GetFileName(Path.GetDirectoryName(file)),
            WorkflowDirectory,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="text"/> is a file GitHub Actions executes. A workflow declares
    /// <c>jobs:</c>; a composite action declares <c>runs:</c>.
    /// </summary>
    /// <remarks>
    /// A file that does not parse is not one GitHub would run either, so it is not an Actions file for
    /// this purpose. It does not vanish silently: if it sits where the convention says a workflow
    /// lives, the pairing above names it.
    /// </remarks>
    private static bool IsActionsFile(string text)
    {
        var root = RootNode(text);

        return root is not null && root.Children.Any(child => IsExecutableKey(child.Key));
    }

    private static bool IsExecutableKey(YamlNode key) =>
        key is YamlScalarNode scalar
        && scalar.Value is not null
        && ExecutableKeys.Contains(scalar.Value, StringComparer.Ordinal);

    /// <summary>The mapping at the root of <paramref name="text"/>, or <c>null</c> when it is not one.</summary>
    private static YamlMappingNode? RootNode(string text)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(text);

        try
        {
            stream.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }

        return stream.Documents.Count == 1 ? stream.Documents[0].RootNode as YamlMappingNode : null;
    }

    /// <summary>
    /// The lines of <paramref name="text"/> a runner acts on: everything but a whole-line comment.
    /// </summary>
    private static IEnumerable<string> Runnable(string text) => text
        .Split('\n')
        .Where(line => !line.TrimStart().StartsWith('#'));

    /// <summary>Every YAML file in the tree, build output and scratch excluded.</summary>
    private static IEnumerable<string> YamlFiles()
    {
        var pending = new Stack<string>();
        pending.Push(DocumeCli.RepoRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (YamlExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>The repo-relative, forward-slashed path of <paramref name="file"/>.</summary>
    private static string Relative(string file) =>
        Path.GetRelativePath(DocumeCli.RepoRoot, file).Replace('\\', '/');

    /// <summary>The platform spelling of a repo-relative path.</summary>
    private static string Native(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);
}
