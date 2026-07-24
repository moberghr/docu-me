namespace DocuMe.Core.Scaffolding;

/// <summary>Whether the scaffolder wrote a file or left an existing one untouched.</summary>
public enum ScaffoldAction
{
    Created,
    Skipped,
}

/// <summary>Outcome for a single scaffolded file, relative to the target directory.</summary>
public sealed record ScaffoldResult(string RelativePath, ScaffoldAction Action);
