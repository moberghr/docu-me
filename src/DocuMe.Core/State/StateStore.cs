using System.Text.Json;
using System.Text.Json.Nodes;
using DocuMe.Core.Json;

namespace DocuMe.Core.State;

/// <summary>
/// Reads and writes <c>_meta/state.json</c> (PLAN.md §5.3), owning the schema
/// version and the forward-migration seam. Load peeks the file's <c>version</c>
/// before deserializing so an older shape can be upgraded to the current model;
/// a newer-than-supported file is rejected rather than silently mangled.
/// </summary>
public static class StateStore
{
    public const int CurrentVersion = 1;

    /// <summary>Reads and (if needed) migrates the state file at <paramref name="path"/>.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="StateVersionException">The file is newer than this tool supports.</exception>
    public static DocumeState Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"State file not found: {path}", path);
        }

        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(
            json,
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })
            ?? throw new StateVersionException(path, 0, CurrentVersion);

        var fileVersion = node["version"]?.GetValue<int>() ?? CurrentVersion;
        node = Migrate(node, fileVersion, path);

        return node.Deserialize<DocumeState>(DocumeJson.Options)
            ?? new DocumeState();
    }

    /// <summary>Serializes <paramref name="state"/> at the current version, creating parent dirs.</summary>
    public static void Save(string path, DocumeState state)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var stamped = state with { Version = CurrentVersion };
        File.WriteAllText(path, JsonSerializer.Serialize(stamped, DocumeJson.Options));
    }

    /// <summary>
    /// Upgrades an older state shape to <see cref="CurrentVersion"/>. Today only
    /// version 1 exists, so this validates the version and returns the node
    /// unchanged; future upgrades add a step per version in the while loop.
    /// </summary>
    private static JsonNode Migrate(JsonNode node, int fromVersion, string path)
    {
        if (fromVersion > CurrentVersion)
        {
            throw new StateVersionException(path, fromVersion, CurrentVersion);
        }

        var version = fromVersion;
        while (version < CurrentVersion)
        {
            // No migration steps yet — the seam is here for the first schema bump.
            version++;
        }

        return node;
    }
}
