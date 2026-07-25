namespace DocuMe.Core.Scaffolding;

/// <summary>Whether the scaffolder wrote a file, added to one, or left an existing one untouched.</summary>
public enum ScaffoldAction
{
    Created,
    Skipped,

    /// <summary>
    /// An existing consumer file gained the entries DocuMe needs, and nothing else changed. Rule §9.4
    /// forbids overwriting a consumer file, but two of <c>init</c>'s targets are shared rather than
    /// owned — a repo's <c>.gitignore</c> and its <c>.config/dotnet-tools.json</c> both hold the
    /// consumer's own entries alongside ours — and for those, skipping outright would leave the
    /// scaffold incomplete in a way only a failing CI run would reveal.
    /// </summary>
    Updated,
}

/// <summary>Outcome for a single scaffolded file, relative to the target directory.</summary>
/// <param name="RelativePath">Where the file went, relative to the scaffolded directory.</param>
/// <param name="Action">Whether it was written or left alone.</param>
/// <param name="Note">
/// Why the target path is not the obvious one, or <c>null</c> when it is. The scaffolder chooses
/// the render script's location from an existing <c>docume.json</c>, so it needs a way to say "your
/// config said something I could not use" without either crashing an idempotent command or falling
/// back in silence.
/// </param>
public sealed record ScaffoldResult(string RelativePath, ScaffoldAction Action, string? Note = null);
