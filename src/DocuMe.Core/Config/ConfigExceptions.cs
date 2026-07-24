namespace DocuMe.Core.Config;

/// <summary>Thrown when the configured <c>docume.json</c> path does not exist.</summary>
public sealed class ConfigNotFoundException(string path)
    : Exception($"Config file not found: {path}")
{
    public string Path { get; } = path;
}

/// <summary>
/// Thrown when a <c>docume.json</c> is syntactically valid JSON but fails
/// model-level validation (missing or empty required fields).
/// </summary>
public sealed class ConfigValidationException(string path, IReadOnlyList<string> errors)
    : Exception($"Invalid config {path}:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", errors)}")
{
    public string Path { get; } = path;

    public IReadOnlyList<string> Errors { get; } = errors;
}
