namespace DocuMe.Core.State;

/// <summary>
/// Thrown when a <c>state.json</c> declares a schema version newer than the
/// running tool understands — the tool must not silently mangle state written
/// by a newer DocuMe. Upgrade the tool.
/// </summary>
public sealed class StateVersionException(string path, int fileVersion, int supportedVersion)
    : Exception(
        $"State file {path} is version {fileVersion}, but this tool supports up to version {supportedVersion}. Upgrade DocuMe.")
{
    public string Path { get; } = path;

    public int FileVersion { get; } = fileVersion;

    public int SupportedVersion { get; } = supportedVersion;
}
