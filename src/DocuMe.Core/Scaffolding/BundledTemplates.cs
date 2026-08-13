using System.Reflection;
using DocuMe.Core.Config;

namespace DocuMe.Core.Scaffolding;

/// <summary>
/// The consumer-facing files <c>docume init</c> copies into a repo (PLAN.md §6.1): the GitHub
/// Actions workflows of §10 and the mermaid render script of §4.
/// </summary>
/// <remarks>
/// <para>
/// They are embedded resources linked from the repo-root <c>templates/</c> directory
/// (see <c>DocuMe.Core.csproj</c>), not forked copies and not package content files. Three
/// reasons, in the order of what they cost to get wrong:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>templates/</c> is already a tested contract — <c>WorkflowTemplateTests</c> reads that exact
/// directory and asserts the command lines, credentials and gates inside it. A second copy living
/// in this project would drift away from the tested one with nothing failing.
/// </description></item>
/// <item><description>
/// A <c>dotnet tool</c> has no reliable runtime path to resolve content files from, while a
/// manifest resource is addressed by name and travels inside the assembly.
/// </description></item>
/// <item><description>
/// The embed is a glob, so a workflow added to the tree ships without anyone remembering to
/// register it here.
/// </description></item>
/// </list>
/// <para>
/// <strong>Rails.</strong> A template whose stem ends in a recognised <see cref="AgentRail"/> name —
/// <c>docs-refresh.claude.yml</c>, <c>docs-refresh.copilot.yml</c> — is a per-rail spelling of the
/// bare name in front of it, and exactly one of them ships. Everything else is rail-agnostic and
/// ships on every rail. Both kinds land in a consumer repo under the bare name, so
/// <c>.github/workflows/docs-refresh.yml</c> is what every path filter, document and existing
/// consumer keeps referring to; the rail is a fact about this repo's template tree, not about
/// theirs.
/// </para>
/// <para>
/// A stem carrying an unrecognised suffix throws rather than shipping as a rail-agnostic file with a
/// dot in its name. That strictness is the point: a typo'd <c>docs-refresh.copilott.yml</c> would
/// otherwise scaffold alongside the real one, and two nightly refresh jobs contending for one
/// <c>concurrency.group</c> is exactly the failure the rail exists to prevent. The same reasoning as
/// the empty-glob throw below — a build-time mistake surfacing in someone else's repo months later
/// is the expensive kind.
/// </para>
/// <para>
/// Content is handed out as bytes, never as re-encoded text: the point of shipping these files is
/// that a consumer gets the reviewed bytes, and a round trip through a string would let an encoding
/// or newline difference in.
/// </para>
/// </remarks>
internal static class BundledTemplates
{
    private const string WorkflowPrefix = "DocuMe.Core.Templates.workflows.";
    private const string RenderScriptResource = "DocuMe.Core.Templates.tools.render-mermaid.mjs";

    private const string WorkflowExtension = ".yml";

    private static readonly Assembly Library = typeof(BundledTemplates).Assembly;

    private static readonly IReadOnlyList<WorkflowTemplate> Workflows = LoadWorkflows();

    /// <summary>
    /// The consumer-facing file names a repo on <paramref name="rail"/> receives: every
    /// rail-agnostic template plus that rail's spelling of each railed one, all under their bare
    /// names. Ordinal-sorted so <c>init</c> reports them in the same order on every machine
    /// (<see cref="Assembly.GetManifestResourceNames"/> does not promise one).
    /// </summary>
    public static IReadOnlyList<string> WorkflowFileNames(AgentRail rail) => Workflows
        .Where(template => template.ShipsOn(rail))
        .Select(template => template.FileName)
        .OrderBy(fileName => fileName, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Bytes of the named workflow as that rail spells it, reviewed in <c>templates/workflows/</c>.
    /// </summary>
    /// <remarks>
    /// Takes the bare consumer-facing name, not the template's own file name, so a caller never has
    /// to know whether a given workflow is railed — that is this type's business, and the whole
    /// point of both sides landing on the bare name.
    /// </remarks>
    public static byte[] ReadWorkflow(string fileName, AgentRail rail)
    {
        var match = Workflows.SingleOrDefault(
            template => template.ShipsOn(rail)
                && string.Equals(template.FileName, fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"No workflow template named '{fileName}' ships on the {rail} rail. Ask "
                + $"{nameof(WorkflowFileNames)}({rail}) what does rather than composing the name.");

        return Read(match.ResourceName);
    }

    /// <summary>
    /// Whether the named workflow is spelled differently per rail, so a repo that switches rails would
    /// get a different file for it. The four that only run <c>docume</c> and <c>git</c> are not.
    /// </summary>
    public static bool IsRailed(string fileName) => Workflows.Any(
        template => template.Rail is not null
            && string.Equals(template.FileName, fileName, StringComparison.Ordinal));

    /// <summary>Bytes of <c>render-mermaid.mjs</c>, as reviewed in <c>templates/tools/</c>.</summary>
    public static byte[] ReadRenderScript() => Read(RenderScriptResource);

    private static WorkflowTemplate[] LoadWorkflows()
    {
        var templates = Library
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(WorkflowPrefix, StringComparison.Ordinal))
            .Select(Classify)
            .ToArray();

        // An empty list means the csproj glob matched nothing — a build-time mistake that would
        // otherwise surface as `docume init` quietly scaffolding no workflows at all, in someone
        // else's repo, months later.
        if (templates.Length == 0)
        {
            throw new InvalidOperationException(
                $"No workflow templates are embedded in {Library.GetName().Name}. The repo-root "
                + "templates/workflows/*.yml glob in DocuMe.Core.csproj matched nothing.");
        }

        RequireEveryRailedWorkflowOnEveryRail(templates);

        return templates;
    }

    /// <summary>
    /// Splits an embedded resource name into the consumer-facing file name and the rail it is
    /// written for, throwing on a stem this type cannot account for.
    /// </summary>
    private static WorkflowTemplate Classify(string resourceName)
    {
        var shipped = resourceName[WorkflowPrefix.Length..];
        var stem = shipped.EndsWith(WorkflowExtension, StringComparison.Ordinal)
            ? shipped[..^WorkflowExtension.Length]
            : shipped;

        var infix = stem.LastIndexOf('.');

        if (infix < 0)
        {
            return new WorkflowTemplate(shipped, Rail: null, resourceName);
        }

        var suffix = stem[(infix + 1)..];

        if (!Enum.TryParse<AgentRail>(suffix, ignoreCase: true, out var rail))
        {
            throw new InvalidOperationException(
                $"Workflow template '{shipped}' has the stem suffix '.{suffix}', which is not an "
                + $"{nameof(AgentRail)}. A dotted stem is how a per-rail spelling is declared, so an "
                + "unrecognised one is either a typo in a rail name or a file name that needs "
                + $"rewording. Known rails: {string.Join(", ", Enum.GetNames<AgentRail>())}.");
        }

        return new WorkflowTemplate(stem[..infix] + WorkflowExtension, rail, resourceName);
    }

    /// <summary>
    /// A workflow that exists on one rail must exist on all of them.
    /// </summary>
    /// <remarks>
    /// The asymmetric case is the quiet one: a repo scaffolded on the rail that is missing a variant
    /// gets no error and no file, so the nightly job it was promised simply never runs and nobody
    /// finds out until the docs are stale and no PR ever appeared. Caught here, at load, where the
    /// message can name the file that was not written.
    /// </remarks>
    private static void RequireEveryRailedWorkflowOnEveryRail(IReadOnlyList<WorkflowTemplate> templates)
    {
        var rails = Enum.GetValues<AgentRail>();

        var incomplete = templates
            .Where(template => template.Rail is not null)
            .GroupBy(template => template.FileName, StringComparer.Ordinal)
            .Select(group => new
            {
                FileName = group.Key,
                Missing = rails.Except(group.Select(template => template.Rail!.Value)).ToArray(),
            })
            .Where(entry => entry.Missing.Length > 0)
            .Select(entry => $"{entry.FileName} (missing: {string.Join(", ", entry.Missing)})")
            .OrderBy(message => message, StringComparer.Ordinal)
            .ToArray();

        if (incomplete.Length > 0)
        {
            throw new InvalidOperationException(
                "Every railed workflow needs a template for every rail, and these do not have one: "
                + $"{string.Join("; ", incomplete)}. A consumer on the missing rail would be "
                + "scaffolded without that workflow and told nothing.");
        }
    }

    /// <summary>
    /// One embedded template: what a consumer's copy is called, which rail it is written for
    /// (<see langword="null"/> when it is rail-agnostic), and where to read its bytes.
    /// </summary>
    private sealed record WorkflowTemplate(string FileName, AgentRail? Rail, string ResourceName)
    {
        public bool ShipsOn(AgentRail rail) => Rail is null || Rail == rail;
    }

    private static byte[] Read(string resourceName)
    {
        using var stream = Library.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded template '{resourceName}' is missing from {Library.GetName().Name}. "
                + "The repo-root templates/ directory is linked in at build time (DocuMe.Core.csproj).");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
