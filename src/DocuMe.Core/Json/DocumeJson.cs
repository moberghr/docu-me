using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocuMe.Core.Json;

/// <summary>
/// Shared JSON options for every DocuMe data file (docume.json, state.json).
/// Reading tolerates comments and trailing commas (docume.json is hand-editable);
/// writing is indented and camelCased and drops null-valued members to keep
/// machine-owned files clean.
/// </summary>
public static class DocumeJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
