namespace DocuMe.Core.Scaffolding;

/// <summary>Whether the scaffolder wrote a file, added to one, or left an existing one untouched.</summary>
public enum ScaffoldAction
{
    Created,
    Skipped,

    /// <summary>
    /// An existing file gained what DocuMe needs, and nothing it already held was lost. Rule §9.4
    /// forbids overwriting a consumer file, but two of <c>init</c>'s targets are shared rather than
    /// owned — a repo's <c>.gitignore</c> and its <c>.config/dotnet-tools.json</c> both hold the
    /// consumer's own entries alongside ours — and for those, skipping outright would leave the
    /// scaffold incomplete in a way only a failing CI run would reveal. <c>--adopt</c> reports it for a
    /// third case: filling in the empty <c>state.json</c> a plain <c>init</c> wrote, whose
    /// <c>baselineSha</c> and <c>lastPublishedSha</c> are carried through untouched
    /// (<see cref="WikiAdopter"/>).
    /// </summary>
    Updated,
}

/// <summary>Outcome for a single scaffolded file, relative to the target directory.</summary>
/// <param name="RelativePath">Where the file went, relative to the scaffolded directory.</param>
/// <param name="Action">Whether it was written, added to, or left alone.</param>
/// <param name="Note">
/// What the run needs to say about this file, or <c>null</c> when there is nothing: why the target
/// path is not the obvious one, what a merge added, what an adoption found, or why an adoption did
/// nothing. <c>init</c> is the command a consumer runs to get <em>out</em> of a broken setup, so it
/// can neither crash on a file it cannot use nor fall back in silence — this is where it says so.
/// </param>
public sealed record ScaffoldResult(string RelativePath, ScaffoldAction Action, string? Note = null);
