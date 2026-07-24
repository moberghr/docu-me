using System.Text.Json;
using DocuMe.Core.Json;

namespace DocuMe.Core.Config;

/// <summary>
/// Loads and validates a consumer repo's <c>docume.json</c> (PLAN.md §5.1).
/// Validation is model-level (required-field checks) rather than a JSON Schema
/// library dependency — see the M0 spec decision.
/// </summary>
public static class ConfigLoader
{
    public const string DefaultFileName = "docume.json";

    /// <summary>
    /// Reads, deserializes and validates the config at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ConfigNotFoundException">The file does not exist.</exception>
    /// <exception cref="ConfigValidationException">The config is missing required fields.</exception>
    public static DocumeConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigNotFoundException(path);
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<DocumeConfig>(json, DocumeJson.Options)
            ?? throw new ConfigValidationException(path, ["config deserialized to null (empty or 'null' file)"]);

        var errors = Validate(config);
        if (errors.Count > 0)
        {
            throw new ConfigValidationException(path, errors);
        }

        return config;
    }

    /// <summary>
    /// Returns the list of validation errors for <paramref name="config"/>;
    /// empty when the config is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(DocumeConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Confluence.BaseUrl))
        {
            errors.Add("confluence.baseUrl is required");
        }

        if (string.IsNullOrWhiteSpace(config.Confluence.SpaceKey))
        {
            errors.Add("confluence.spaceKey is required");
        }

        if (string.IsNullOrWhiteSpace(config.Wiki.Root))
        {
            errors.Add("wiki.root is required");
        }

        return errors;
    }
}
