using DocuMe.Core.Markdown;

namespace DocuMe.Core.Acceptance;

/// <summary>One page handed to <see cref="ConversionAcceptance"/>.</summary>
/// <param name="Path">How the page is identified in the report (wiki-root-relative path).</param>
/// <param name="Body">The frontmatter-free markdown body.</param>
/// <param name="Resolvers">The page's three converter lookups (<see cref="WikiTree.ResolversFor"/>).</param>
public sealed record AcceptancePage(string Path, string Body, PageResolvers Resolvers);

/// <summary>
/// The PLAN.md §4.4 acceptance runner: converts every page of a corpus and reports what happened,
/// grouped by construct and by dialect.
/// </summary>
/// <remarks>
/// <para>
/// Read-only by construction. It renders storage format and throws the result away — no
/// Confluence call, no mermaid render, nothing written back to the pages it reads. What it
/// produces is a count: which constructs fail loud and on how many pages, and which dialects
/// degrade and how often (<see cref="AcceptanceReport"/>).
/// </para>
/// <para>
/// <see cref="Run"/> is the primitive and takes pages, not a directory, for two reasons. It keeps
/// the runner testable against an in-memory corpus, and it keeps it usable on a tree that
/// <see cref="WikiTree.Load"/> would reject: a flat fixture directory whose files have no titles
/// is not publishable, but converting it is exactly how the runner itself gets tested. A real
/// §4.4 run goes through <see cref="RunTree"/>, where the whole-tree validation is wanted.
/// </para>
/// </remarks>
public static class ConversionAcceptance
{
    /// <summary>Converts every page in <paramref name="pages"/> and groups the outcome.</summary>
    /// <param name="pages">The corpus. Enumerated once.</param>
    /// <param name="policy">
    /// Which diagnostic codes count against the bar; <see cref="AcceptancePolicy.Strict"/> when
    /// omitted.
    /// </param>
    public static AcceptanceReport Run(IEnumerable<AcceptancePage> pages, AcceptancePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(pages);

        return AcceptanceReport.From([.. pages.Select(ConvertOne)], policy ?? AcceptancePolicy.Strict);
    }

    /// <summary>
    /// Converts every page of a loaded wiki, each with its own resolvers — the real §4.4 run
    /// (PLAN.md §4.4, §6.2 steps 1-4 minus the publish).
    /// </summary>
    public static AcceptanceReport RunTree(WikiTree tree, AcceptancePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return Run(
            tree.Pages.Select(page => new AcceptancePage(page.Path, page.Parsed.Body, tree.ResolversFor(page.Path))),
            policy);
    }

    /// <summary>
    /// Converts every <c>*.md</c> file under <paramref name="directory"/> with one shared set of
    /// resolvers, deliberately skipping <see cref="WikiTree.Load"/>'s whole-tree validation.
    /// </summary>
    /// <remarks>
    /// For a corpus that is not a publishable wiki: a fixture directory, or a triage pass over a
    /// tree whose titles do not yet resolve. Because the resolvers are shared rather than bound
    /// per page, relative links resolve against the corpus root, not the linking page's
    /// directory — fine for a flat directory, wrong for a nested wiki, which is what
    /// <see cref="RunTree"/> is for.
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException"><paramref name="directory"/> does not exist.</exception>
    public static AcceptanceReport RunDirectory(
        string directory,
        PageResolvers resolvers,
        AcceptancePolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(resolvers);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directory}");
        }

        var paths = Directory
            .EnumerateFiles(directory, "*.md", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

        return Run(paths.Select(path => new AcceptancePage(path, ReadBody(directory, path), resolvers)), policy);
    }

    private static string ReadBody(string directory, string relativePath)
    {
        var full = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return FrontmatterParser.Parse(File.ReadAllText(full)).Body;
    }

    private static PageConversionResult ConvertOne(AcceptancePage page)
    {
        var diagnostics = new List<ConversionDiagnostic>();
        var diagrams = new List<string>();

        // The diagram resolver already sees every mermaid fence the converter renders, so wrapping
        // it hands the render pass its work list with no second parse of the markdown.
        string? Collect(string mermaidSource)
        {
            diagrams.Add(mermaidSource);
            return page.Resolvers.Diagram(mermaidSource);
        }

        try
        {
            _ = ConfluenceStorageConverter.Convert(
                page.Body,
                page.Resolvers.Link,
                page.Resolvers.Attachment,
                Collect,
                diagnostics);

            return new PageConversionResult(page.Path, null, diagnostics, diagrams);
        }
        catch (NotSupportedException ex)
        {
            // The only exception the converter throws by design (its fail-loud contract), so it
            // is the only one this runner turns into a report row. Anything else is a converter
            // bug and keeps crashing the run — a bug quietly tallied as an acceptance finding
            // would be read as "that page needs different markdown".
            return new PageConversionResult(page.Path, Describe(ex.Message), diagnostics, diagrams);
        }
    }

    /// <summary>
    /// Splits a fail-loud message into its grouping key and the specific token that triggered it
    /// (see <see cref="ConversionFailure"/>).
    /// </summary>
    private static ConversionFailure Describe(string message)
    {
        // The converter's fail-loud sites quote the offending token with ' — a fence dialect, a
        // node type, an attribute name — so the message minus its quoted tokens is the construct.
        var (kind, token) = QuotedTokens.Normalize(message, '\'');
        return new ConversionFailure(kind, token, message);
    }
}
