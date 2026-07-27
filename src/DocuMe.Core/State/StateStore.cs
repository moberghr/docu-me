using System.Text;
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

    /// <summary>
    /// What <see cref="Save"/> appends to the state path to name the sibling it writes before the rename.
    /// <c>StateStoreTests</c> names this suffix literally, so a change here turns those tests red rather
    /// than leaving them asserting nothing.
    /// </summary>
    private const string TemporarySuffix = ".tmp";

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
    /// <remarks>
    /// <para>
    /// <strong>The live file is never the write target.</strong> The JSON goes to a sibling
    /// <c>state.json.tmp</c>, is flushed to disk, and only then replaces <paramref name="path"/> in one
    /// rename. <c>File.WriteAllText</c> opens the live file with <see cref="FileMode.Create"/>, which
    /// truncates it before the first byte of the new content lands — so a run killed inside that window
    /// (a second Ctrl-C, a cancelled CI job, a full disk) left <c>_meta/state.json</c> empty or
    /// half-written, and with it every page id, approval record and feedback cursor the file held,
    /// including the ones earlier runs earned and nobody has committed yet. PLAN.md §6.2 step 8 requires
    /// the opposite: a page id earned by a create must survive the run that later died, or the next run
    /// creates the page again and Confluence rejects the duplicate title. Nothing re-derives a lost id:
    /// the publish pipeline looks a known page up by the id it recorded here, never by its title.
    /// </para>
    /// <para>
    /// The temp file is a sibling rather than something under the system temp directory, because a
    /// rename is only atomic within one volume. Its name is deterministic, so a killed run leaves one
    /// stale file that the next save overwrites instead of a growing pile, and a write that fails leaves
    /// it behind on purpose: the live state is intact and the leftover is evidence. Consumers stage
    /// <c>_meta/state.json</c> by path (<c>templates/workflows/docs-publish.yml</c>), so a leftover is
    /// never committed.
    /// </para>
    /// </remarks>
    public static void Save(string path, DocumeState state)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var stamped = state with { Version = CurrentVersion };
        var json = JsonSerializer.Serialize(stamped, DocumeJson.Options);
        var temporary = path + TemporarySuffix;

        // UTF-8 with no BOM and no trailing newline: byte-for-byte what File.WriteAllText wrote here
        // before, so a state file this save replaces is unchanged by the mechanism alone.
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(json));

            // Flushed before the rename: a rename that outlives its own content in the page cache is the
            // one crash that would leave a state file present and empty, which reads as "no pages".
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
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
