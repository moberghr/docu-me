using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocuMe.Core.Config;
using DocuMe.Core.Scaffolding;
using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.Config;

/// <summary>
/// <c>docs/wiki/20-reference/configuration.md</c> — the page a consumer reads to hand-write
/// <c>docume.json</c> — against the surface it describes, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConfigSchemaTests"/> pins the schema against the record and the loader; the page was the
/// leg nothing held, and at iter84 all three of its claims were wrong. It carried no <c>$schema</c> line
/// at all, though <c>docume init</c> writes one into every consumer's file and the schema's
/// <c>additionalProperties: false</c> is the ONLY place a misspelled key is ever reported (the loader
/// drops unknown keys in silence — <see cref="ConfigSchemaTests"/> pins that too). It then said
/// "everything else has the default shown above" directly beneath an example showing
/// <c>protectedSpaces: ["PROD"]</c>, whose real default is empty: a reader came away believing a write
/// lock ships enabled. And its <c>state.json</c> example omitted <c>diagramWidths</c>, the one page-state
/// field named nowhere else in the tree.
/// </para>
/// <para>
/// Every check derives both sides — from the shipped schema, from a real scaffold, and from the records
/// by reflection — because a hand-listed expectation is the same artifact as the page and rots with it.
/// </para>
/// </remarks>
public sealed class ConfigReferencePageTests : IDisposable
{
    private const string PagePath = "docs/wiki/20-reference/configuration.md";

    /// <summary>The example values the page's own <c>docume.json</c> block shows for the two required fields.</summary>
    private const string ExampleBaseUrl = "https://example.atlassian.net/wiki";
    private const string ExampleSpaceKey = "DOCS";

    private readonly string _dir = Directory.CreateTempSubdirectory("docume-config-page-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_page_shows_every_field_the_shipped_schema_declares()
    {
        var declared = SchemaPaths(Schema(), string.Empty);
        var shown = Paths(ConfigExample(), string.Empty, Shape.Flat);

        var missing = declared.Except(shown, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var phantom = shown.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        missing.ShouldBeEmpty(
            $"{PagePath}'s docume.json example omits fields docume.json really has, so a reader hand-writing "
            + $"the file never learns they exist: {string.Join(", ", missing)}");

        phantom.ShouldBeEmpty(
            $"{PagePath}'s docume.json example shows fields the schema does not declare. Copied into a real "
            + $"docume.json they are flagged by every editor and dropped by the loader: {string.Join(", ", phantom)}");
    }

    [Fact]
    public void The_page_documents_every_field_the_state_records_carry()
    {
        var shape = StateShape();
        var carried = shape.Fields;
        var shown = Paths(StateExample(), string.Empty, shape);

        // The example carries every field except the ones the page hands to another page in so many
        // words, and that sentence is where the exemption comes from — not from a list in here, which
        // would let the example quietly lose a field the prose happens to mention in passing.
        var delegated = DelegatedFields();

        var undocumented = carried
            .Where(path => !shown.Contains(path))
            .Where(path => !delegated.Contains(Leaf(path)))
            .Order(StringComparer.Ordinal)
            .ToList();

        var phantom = shown.Except(carried, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        undocumented.ShouldBeEmpty(
            $"{PagePath} is the reference for _meta/state.json and neither its example nor its prose "
            + $"mentions these fields, which a reader therefore meets first in a publish diff: "
            + $"{string.Join(", ", undocumented)}");

        phantom.ShouldBeEmpty(
            $"{PagePath}'s state.json example shows fields DocumeState does not have, so the loader drops "
            + $"them: {string.Join(", ", phantom)}");
    }

    [Fact]
    public void Every_example_value_that_is_not_the_default_is_declared_as_not_a_default()
    {
        // Scaffolded with the page's own example arguments, so the only differences left are the ones
        // the page is choosing to illustrate rather than the ones `docume init` was told.
        ProjectScaffolder.Scaffold(_dir, spaceKey: ExampleSpaceKey, baseUrl: ExampleBaseUrl);
        var written = Flatten(
            JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, ConfigLoader.DefaultFileName)))!,
            string.Empty);

        var shown = Flatten(ConfigExample(), string.Empty);

        // An absent key and a documented `null` are the same default, so only a real difference counts.
        var diverging = shown
            .Where(entry => !string.Equals(
                written.TryGetValue(entry.Key, out var actual) ? actual : "null",
                entry.Value,
                StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        const string message =
            "The page's example is not a list of defaults, and its table of which values are only "
            + "illustrations has drifted from the example above it. A field missing from the table reads as "
            + "a shipped default — which is how `protectedSpaces: [\"PROD\"]` told readers a write lock was "
            + "already on. Table rows vs fields that really differ from `docume init`'s output:";

        var tabled = NonDefaultTableFields().Order(StringComparer.Ordinal).ToList();

        tabled.ShouldBe(diverging, customMessage: message);
    }

    [Fact]
    public void The_page_names_the_schema_url_that_catches_a_misspelled_key()
    {
        ProjectScaffolder.Scaffold(_dir, spaceKey: ExampleSpaceKey, baseUrl: ExampleBaseUrl);
        var scaffolded = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, ConfigLoader.DefaultFileName)))!;
        var url = scaffolded["$schema"]!.GetValue<string>();

        const string message =
            $"{PagePath} shows a $schema line that is not the one `docume init` writes. A reader who copies "
            + "the page's block gets no completion and no typo check, which is the only warning the loader "
            + "never gives them.";

        ConfigExample()["$schema"]?.GetValue<string>().ShouldBe(url, message);
    }

    [Fact]
    public void The_page_scan_reached_both_examples_and_the_defaults_table()
    {
        // Four of the checks above pass vacuously if the extraction finds nothing, and the mutation round
        // proved it: blinding the fenced-block regex left them all green.
        Page().Length.ShouldBeGreaterThan(2000, $"{PagePath} is far shorter than the page these tests scan.");
        JsonBlocks().Count.ShouldBe(2, $"{PagePath} should carry exactly two json examples: docume.json and state.json.");
        Paths(ConfigExample(), string.Empty, Shape.Flat).Count.ShouldBeGreaterThan(20);
        Paths(StateExample(), string.Empty, StateShape()).Count.ShouldBeGreaterThan(15);
        StateShape().WildcardAt.ShouldContain("pages");
        StateShape().OpaqueAt.ShouldContain("pages.*.diagramWidths");
        DelegatedFields().ShouldNotBeEmpty("The paragraph delegating state fields to another page was not found.");
        NonDefaultTableFields().Count.ShouldBeGreaterThan(0, "The 'not a list of defaults' table was not found on the page.");
    }

    private static string Page() => File.ReadAllText(Path.Combine(RepoRoot, PagePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Every fenced <c>json</c> block on the page, in order.</summary>
    private static List<JsonNode> JsonBlocks()
    {
        var blocks = new List<JsonNode>();
        var page = Page();

        for (var index = page.IndexOf("```json", StringComparison.Ordinal); index >= 0;)
        {
            var start = page.IndexOf('\n', index) + 1;
            var end = page.IndexOf("```", start, StringComparison.Ordinal);
            blocks.Add(JsonNode.Parse(page[start..end])!);
            index = page.IndexOf("```json", end + 3, StringComparison.Ordinal);
        }

        return blocks;
    }

    private static JsonNode ConfigExample() => JsonBlocks()[0];

    private static JsonNode StateExample() => JsonBlocks()[1];

    private static JsonNode Schema() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "schema", "docume.schema.json")))!;

    /// <summary>
    /// The field names in the paragraph where the page says some keys live on another page, which is
    /// the only way a state field is allowed to be missing from the example.
    /// </summary>
    private static HashSet<string> DelegatedFields()
    {
        var page = Page();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var marker = page.IndexOf("than the example shows", StringComparison.Ordinal);

        if (marker < 0)
        {
            return found;
        }

        var start = page.LastIndexOf("\n\n", marker, StringComparison.Ordinal) + 2;
        var end = page.IndexOf("\n\n", marker, StringComparison.Ordinal);
        var paragraph = page[start..(end < 0 ? page.Length : end)];

        foreach (var chunk in paragraph.Split('`').Where((_, index) => index % 2 == 1))
        {
            found.Add(chunk);
        }

        return found;
    }

    /// <summary>
    /// The first column of the markdown table under the page's "not a list of defaults" sentence.
    /// </summary>
    private static List<string> NonDefaultTableFields()
    {
        var page = Page();
        var fields = new List<string>();
        var marker = page.IndexOf("not a list of defaults", StringComparison.Ordinal);

        if (marker < 0)
        {
            return fields;
        }

        var started = false;

        foreach (var line in page[marker..].Split('\n').Select(line => line.Trim()))
        {
            if (!line.StartsWith('|'))
            {
                if (started)
                {
                    break;
                }

                continue;
            }

            started = true;
            var cell = line.Split('|')[1].Trim();

            if (cell.StartsWith('`') && cell.EndsWith('`'))
            {
                fields.Add(cell.Trim('`'));
            }
        }

        return fields;
    }

    /// <summary>
    /// Which paths of a documented example are an open dictionary rather than a field, derived from the
    /// records so the example's sample keys are never mistaken for field names.
    /// </summary>
    /// <param name="Fields">Every field path the records carry.</param>
    /// <param name="WildcardAt">Dictionaries whose keys are data and whose values are records (<c>pages</c>).</param>
    /// <param name="OpaqueAt">Dictionaries of scalars, documented as a shape (<c>attachments</c>).</param>
    private sealed record Shape(
        HashSet<string> Fields,
        HashSet<string> WildcardAt,
        HashSet<string> OpaqueAt)
    {
        /// <summary>A shape with no dictionaries in it — <c>docume.json</c> has none.</summary>
        public static Shape Flat { get; } = new([], [], []);
    }

    private static Shape StateShape()
    {
        var shape = new Shape(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        Walk(typeof(DocumeState), string.Empty, shape);

        return shape;
    }

    /// <summary>Dotted paths of a JSON example, with <paramref name="shape"/>'s dictionaries collapsed.</summary>
    private static HashSet<string> Paths(JsonNode node, string prefix, Shape shape)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        if (node is JsonArray array)
        {
            if (array.Count > 0 && array[0] is JsonObject)
            {
                found.UnionWith(Paths(array[0]!, $"{prefix}[]", shape));
            }

            return found;
        }

        if (node is not JsonObject blocks || shape.OpaqueAt.Contains(prefix))
        {
            return found;
        }

        foreach (var (key, child) in blocks)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            var named = shape.WildcardAt.Contains(prefix) ? $"{prefix}.*" : path;

            found.Add(named);

            if (child is not null)
            {
                found.UnionWith(Paths(child, named, shape));
            }
        }

        return found;
    }

    /// <summary>Dotted paths of every property the schema declares.</summary>
    private static HashSet<string> SchemaPaths(JsonNode node, string prefix)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var properties = node["properties"]?.AsObject();

        if (properties is null)
        {
            return found;
        }

        foreach (var (key, child) in properties)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            found.Add(path);

            if (child?["properties"] is not null)
            {
                found.UnionWith(SchemaPaths(child, path));
            }
            else if (child?["items"]?["properties"] is not null)
            {
                found.UnionWith(SchemaPaths(child["items"]!, $"{path}[]"));
            }
        }

        return found;
    }

    /// <summary>
    /// Collects a state record's field paths into <paramref name="shape"/>. A dictionary keyed by page
    /// path descends through <c>*</c>; a dictionary of scalars and a list of records are leaves, because
    /// the page documents those as shapes rather than field by field.
    /// </summary>
    private static void Walk(Type type, string prefix, Shape shape)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";

            shape.Fields.Add(path);

            if (IsStateRecord(property.PropertyType))
            {
                Walk(property.PropertyType, path, shape);
                continue;
            }

            var value = DictionaryValue(property.PropertyType);

            if (value is null)
            {
                continue;
            }

            if (!IsStateRecord(value))
            {
                shape.OpaqueAt.Add(path);
                continue;
            }

            shape.WildcardAt.Add(path);
            shape.Fields.Add($"{path}.*");
            Walk(value, $"{path}.*", shape);
        }
    }

    /// <summary>A dictionary property's value type, or <c>null</c> when the property is not one.</summary>
    private static Type? DictionaryValue(Type propertyType)
    {
        if (!typeof(IEnumerable).IsAssignableFrom(propertyType) || !propertyType.IsGenericType)
        {
            return null;
        }

        var arguments = propertyType.GetGenericArguments();

        return arguments.Length == 2 ? arguments[1] : null;
    }

    private static bool IsStateRecord(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsClass
            && underlying != typeof(string)
            && string.Equals(underlying.Namespace, typeof(DocumeState).Namespace, StringComparison.Ordinal);
    }

    /// <summary>Dotted path to the JSON text of every scalar and array leaf.</summary>
    private static Dictionary<string, string> Flatten(JsonNode node, string prefix)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        if (node is not JsonObject blocks)
        {
            found[prefix] = node.ToJsonString();
            return found;
        }

        foreach (var (key, child) in blocks)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";

            if (child is null)
            {
                found[path] = "null";
                continue;
            }

            foreach (var (nested, value) in Flatten(child, path))
            {
                found[nested] = value;
            }
        }

        return found;
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('.') + 1)..];

    private static string RepoRoot { get; } = Locate();

    /// <summary>Walks up to the directory holding <c>DocuMe.slnx</c>: the page ships in the tree.</summary>
    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so {PagePath} cannot be found.");
    }
}
