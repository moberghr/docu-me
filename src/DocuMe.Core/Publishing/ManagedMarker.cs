using System.Text.Json;
using DocuMe.Core.Json;

namespace DocuMe.Core.Publishing;

/// <summary>
/// The content property every DocuMe-written page carries: key <c>docume</c>, value
/// <c>{"managed":true,"path":"&lt;wiki-relative path&gt;"}</c> (PLAN.md §6.2,
/// docs/specs/2026-08-18-managed-marker.md).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it protects.</strong> Presence in <c>state.json</c> is the prune's evidence that DocuMe
/// wrote a page, and that evidence is weaker than it looks: <c>init --adopt</c> seeds ids for pages
/// DocuMe never created, and state.json is a committed, hand-editable file, so one wrong id is one
/// confirmation away from trashing a page a human owns. The marker is the page's own testimony, stamped
/// by the run that created it. Adoption and hand edits are exactly where the two records diverge, and
/// there the marker wins: <see cref="PruneExecutor"/> reads it live before every delete and refuses a
/// page that does not carry it.
/// </para>
/// <para>
/// <strong>What it enables later.</strong> A repo whose state.json is lost or corrupted can be rebuilt
/// by asking the space which pages carry this property. The marker is the registry a future
/// <c>state rebuild</c> queries; nothing depends on that yet, but every page stamped now is one that
/// rebuild will find.
/// </para>
/// <para>
/// The value carries the path as well as the flag because page properties are visible in Confluence: a
/// human inspecting the page sees not just that a tool manages it but which file owns it.
/// <see cref="IsManaged"/> reads only the flag, on purpose. The path is for people (and for the future
/// rebuild), and a check that also matched paths would refuse every page the repo has since renamed.
/// </para>
/// </remarks>
public static class ManagedMarker
{
    /// <summary>The property key. Short and unnamespaced, like the labels §8 uses.</summary>
    public const string Key = "docume";

    /// <summary>
    /// The house options with indentation off: a property value is a wire payload, not a file a human
    /// diffs, and the compact spelling is what the spec pins.
    /// </summary>
    private static readonly JsonSerializerOptions Compact = new(DocumeJson.Options)
    {
        WriteIndented = false,
    };

    /// <summary>
    /// The value to stamp for the page owned by <paramref name="path"/> (wiki-relative markdown path,
    /// the same key state.json uses), as compact JSON.
    /// </summary>
    public static string ValueFor(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return JsonSerializer.Serialize(new MarkerValue(true, path), Compact);
    }

    /// <summary>
    /// Whether <paramref name="rawValue"/> is this tool's marker: <c>true</c> only when the JSON parses
    /// and says <c>managed: true</c>. Everything else reads as unmanaged, including malformed JSON and
    /// somebody else's property under the same key, because the caller is about to delete a page and
    /// "cannot tell" must land on the side that deletes nothing.
    /// </summary>
    public static bool IsManaged(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<MarkerValue>(rawValue, Compact) is { Managed: true };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The value's JSON shape.</summary>
    /// <param name="Managed">The claim <see cref="IsManaged"/> checks.</param>
    /// <param name="Path">The owning file, for a human reading the page's properties. Tolerated absent
    /// on the way in: the flag is the contract, the path is the courtesy.</param>
    private sealed record MarkerValue(bool Managed, string? Path);
}
