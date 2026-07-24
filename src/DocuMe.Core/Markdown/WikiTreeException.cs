namespace DocuMe.Core.Markdown;

/// <summary>
/// Thrown when a wiki tree cannot be published as it stands: a page with no resolvable
/// title, two pages competing for one Confluence title (§6.2 step 1), two asset paths
/// competing for one attachment filename, or a <c>wiki.extraPages</c> entry pointing at
/// nothing.
/// </summary>
/// <remarks>
/// Every problem in the tree is collected before throwing rather than failing on the first
/// one: a 79-page adoption run wants the whole list in one pass, not 79 runs.
/// </remarks>
public sealed class WikiTreeException(string wikiRoot, IReadOnlyList<string> errors)
    : Exception($"Wiki tree at {wikiRoot} cannot be published:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", errors)}")
{
    public string WikiRoot { get; } = wikiRoot;

    public IReadOnlyList<string> Errors { get; } = errors;
}
