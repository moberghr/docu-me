namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// Locates the real <c>templates/tools/render-mermaid.mjs</c> and its npm dependency, for the tests
/// that exercise <c>beautiful-mermaid</c> itself rather than a stub.
/// </summary>
/// <remarks>
/// Shared by <see cref="MermaidRendererTests"/> and the render pass's own suite: both need the same
/// "is this machine set up to render" question answered the same way, and both skip rather than fail
/// when it is not, so a clone without <c>npm ci</c> still gets a green <c>dotnet test</c>.
/// </remarks>
internal static class BundledRenderScript
{
    /// <summary>
    /// The npm package the real script needs, and therefore the one CI has to install before it runs
    /// the suite — <c>CiMermaidToolchainTests</c> holds those two ends together.
    /// </summary>
    public const string Package = "beautiful-mermaid";

    /// <summary>Why a test skipped, and what to run so it does not.</summary>
    public const string DependencyMissingReason =
        "beautiful-mermaid is not installed: run 'npm ci' at the repo root to exercise the real "
        + "render-mermaid.mjs end to end.";

    /// <summary>
    /// How a stub script must read the diagram source, leaving it in a <c>source</c> const.
    /// </summary>
    /// <remarks>
    /// The same idiom the shipped script uses, and for the same reason: <c>readFileSync(0)</c>
    /// throws EAGAIN on the non-blocking stdin pipe .NET hands a child whenever the source has not
    /// arrived yet, so a stub that read it that way would be a fixture with a race in it.
    /// </remarks>
    public const string ReadSourceFromStdin =
        """
        const chunks = [];
        for await (const chunk of process.stdin) chunks.push(chunk);
        const source = Buffer.concat(chunks).toString('utf8');
        """;

    /// <summary>
    /// The script's path, or <c>null</c> when it or <c>beautiful-mermaid</c> is absent.
    /// </summary>
    public static string? TryFind()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            return null;
        }

        var script = Path.Combine(repoRoot, "templates", "tools", "render-mermaid.mjs");
        var dependency = Path.Combine(repoRoot, "node_modules", Package);

        return File.Exists(script) && Directory.Exists(dependency) ? script : null;
    }

    /// <summary>
    /// Walks up from the test assembly to the directory holding <c>DocuMe.slnx</c>. The real script
    /// cannot be copied beside the assembly like the goldens are: Node resolves
    /// <c>beautiful-mermaid</c> from the script's own location upwards, so it has to run from its
    /// place in the tree.
    /// </summary>
    private static string? FindRepoRoot()
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

        return null;
    }
}
