namespace DocuMe.Core.Scaffolding;

/// <summary>Whether the scaffolder wrote a file or left an existing one untouched.</summary>
public enum ScaffoldAction
{
    Created,
    Skipped,
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
