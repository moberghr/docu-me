using System.Reflection;

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
/// Content is handed out as bytes, never as re-encoded text: the point of shipping these files is
/// that a consumer gets the reviewed bytes, and a round trip through a string would let an encoding
/// or newline difference in.
/// </para>
/// </remarks>
internal static class BundledTemplates
{
    private const string WorkflowPrefix = "DocuMe.Core.Templates.workflows.";
    private const string RenderScriptResource = "DocuMe.Core.Templates.tools.render-mermaid.mjs";

    private static readonly Assembly Library = typeof(BundledTemplates).Assembly;

    /// <summary>
    /// Every bundled workflow's file name, ordinal-sorted so <c>init</c> reports them in the same
    /// order on every machine (<see cref="Assembly.GetManifestResourceNames"/> does not promise one).
    /// </summary>
    public static IReadOnlyList<string> WorkflowFileNames { get; } = LoadWorkflowFileNames();

    /// <summary>Bytes of the named workflow template, as reviewed in <c>templates/workflows/</c>.</summary>
    public static byte[] ReadWorkflow(string fileName) => Read(WorkflowPrefix + fileName);

    /// <summary>Bytes of <c>render-mermaid.mjs</c>, as reviewed in <c>templates/tools/</c>.</summary>
    public static byte[] ReadRenderScript() => Read(RenderScriptResource);

    private static string[] LoadWorkflowFileNames()
    {
        var names = Library
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(WorkflowPrefix, StringComparison.Ordinal))
            .Select(name => name[WorkflowPrefix.Length..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // An empty list means the csproj glob matched nothing — a build-time mistake that would
        // otherwise surface as `docume init` quietly scaffolding no workflows at all, in someone
        // else's repo, months later.
        if (names.Length == 0)
        {
            throw new InvalidOperationException(
                $"No workflow templates are embedded in {Library.GetName().Name}. The repo-root "
                + "templates/workflows/*.yml glob in DocuMe.Core.csproj matched nothing.");
        }

        return names;
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
