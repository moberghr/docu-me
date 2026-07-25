using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocuMe.Core.Config;
using DocuMe.Core.Scaffolding;
using Shouldly;

namespace DocuMe.Core.Tests.Config;

/// <summary>
/// <c>schema/docume.schema.json</c> against the three things that can disagree with it: the
/// <c>$schema</c> URL <c>docume init</c> writes into every consumer's <c>docume.json</c>, the
/// <see cref="DocumeConfig"/> record the loader actually binds, and
/// <see cref="ConfigLoader.Validate"/>'s required fields.
/// </summary>
/// <remarks>
/// <para>
/// The schema exists because the loader ignores what it does not recognize (pinned below): a
/// misspelled key in <c>docume.json</c> is silently dropped, so <c>additionalProperties: false</c>
/// here is the only place a consumer ever learns about the typo. That makes schema drift worse than
/// no schema — a field the record gained and the schema lacks reads as an error in the editor — which
/// is why the parity check derives both sides rather than listing them.
/// </para>
/// <para>
/// The URL check earns its place: iter67 found the scaffolded <c>$schema</c> pointing at
/// <c>moberg/docu-me</c> (the org is <c>moberghr</c>) at a <c>schema/</c> path that did not exist in
/// the repo at all, copied verbatim out of PLAN.md §5.1's example. Nothing referenced <c>$schema</c>
/// anywhere in the suite, and both halves fail exactly the way dead config fails: silently.
/// </para>
/// </remarks>
public sealed class ConfigSchemaTests : IDisposable
{
    /// <summary>Where a raw.githubusercontent URL's repo-relative path starts.</summary>
    private const string RefSegment = "/main/";

    private readonly string _dir = Directory.CreateTempSubdirectory("docume-schema-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_scaffolded_schema_url_points_at_a_file_this_repo_ships()
    {
        var url = ScaffoldedSchemaUrl();

        var refIndex = url.IndexOf(RefSegment, StringComparison.Ordinal);
        refIndex.ShouldBeGreaterThan(0, $"The $schema URL has no {RefSegment} ref segment: {url}");

        var repoRelative = url[(refIndex + RefSegment.Length)..];
        var onDisk = Path.Combine(RepoRoot, repoRelative.Replace('/', Path.DirectorySeparatorChar));

        const string message =
            "`docume init` writes this URL into every consumer's docume.json. A path this repo does "
            + "not ship 404s, and an editor's silent loss of completion is how nobody notices:";

        File.Exists(onDisk).ShouldBeTrue($"{message} {repoRelative}");
    }

    [Fact]
    public void The_scaffolded_schema_url_names_the_repository_the_plugin_manifest_names()
    {
        // Derived from the manifest rather than retyped, because retyping it a third time is what
        // produced the `moberg`/`moberghr` split in the first place.
        var manifest = JsonNode.Parse(
            File.ReadAllText(Path.Combine(RepoRoot, "plugin", ".claude-plugin", "plugin.json")))!;
        var repository = manifest["repository"]!.GetValue<string>();

        const string prefix = "https://github.com/";
        repository.ShouldStartWith(prefix);
        var ownerAndRepo = repository[prefix.Length..].TrimEnd('/');

        const string message =
            "The $schema URL and the plugin manifest disagree about which GitHub repo this is, so one "
            + "of them is dead. Owner/repo from plugin.json:";

        ScaffoldedSchemaUrl().ShouldContain($"/{ownerAndRepo}/", Case.Sensitive, $"{message} {ownerAndRepo}");
    }

    [Fact]
    public void The_schema_id_matches_the_url_it_is_published_at()
    {
        // A schema whose $id disagrees with where it lives resolves relative $refs against the wrong
        // base. There are none today, which is exactly when this is cheap to get right.
        Schema()["$id"]!.GetValue<string>().ShouldBe(ScaffoldedSchemaUrl());
    }

    [Fact]
    public void The_schema_describes_exactly_the_fields_docume_json_has()
    {
        var problems = new List<string>();
        Compare(Schema(), typeof(DocumeConfig), "(root)", problems);

        const string message =
            "schema/docume.schema.json and DocumeConfig disagree. A field the record has and the "
            + "schema lacks is flagged as an error in every editor (additionalProperties is false); a "
            + "field the schema has and the record lacks is advertised and then ignored. Problems:";

        problems.ShouldBeEmpty($"{message} {string.Join(" // ", problems)}");
    }

    [Fact]
    public void The_schema_requires_exactly_the_fields_the_loader_requires()
    {
        var required = RequiredLeaves(Schema(), typeof(DocumeConfig), string.Empty).Order(StringComparer.Ordinal);

        required.ShouldBe(["confluence.baseUrl", "confluence.spaceKey", "wiki.root"]);

        // And each one is required by the code as well as by the schema: a schema that is merely
        // self-consistent would still let the two drift apart.
        ConfigLoader.Validate(Complete()).ShouldBeEmpty();

        foreach (var leaf in required)
        {
            ConfigLoader.Validate(Without(leaf))
                .ShouldContain(
                    error => error.StartsWith(leaf, StringComparison.Ordinal),
                    $"The schema requires {leaf} but ConfigLoader.Validate does not complain when it is missing.");
        }
    }

    [Fact]
    public void The_loader_ignores_a_misspelled_key_which_is_why_the_schema_forbids_extras()
    {
        const string misspelled = """
            {
              "confluence": { "baseUrl": "https://example.atlassian.net/wiki", "spaceKey": "DOCS" },
              "wiki": { "roots": "docs/handbook" }
            }
            """;

        var path = Path.Combine(_dir, "docume.json");
        File.WriteAllText(path, misspelled);

        var config = ConfigLoader.Load(path);

        // "roots" is dropped without a word and wiki.root silently keeps its default, which is why
        // every object in the schema sets additionalProperties: false.
        config.Wiki.Root.ShouldBe("docs/wiki");
        Objects(Schema(), typeof(DocumeConfig), "(root)")
            .ShouldAllBe(entry => !entry.Node["additionalProperties"]!.GetValue<bool>());
    }

    /// <summary>The <c>$schema</c> value a real <c>docume init</c> writes, read back off disk.</summary>
    private string ScaffoldedSchemaUrl()
    {
        ProjectScaffolder.Scaffold(_dir, spaceKey: "DOCS", baseUrl: "https://example.atlassian.net/wiki");

        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "docume.json")))!;
        var url = config["$schema"];

        url.ShouldNotBeNull("The scaffolded docume.json declares no $schema (PLAN.md §5.1).");

        return url.GetValue<string>();
    }

    /// <summary>Walks schema object and record together, collecting every disagreement.</summary>
    private static void Compare(JsonNode schema, Type type, string path, List<string> problems)
    {
        var expected = JsonProperties(type);
        var actual = schema["properties"]?.AsObject().Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        if (actual is null)
        {
            problems.Add($"{path}: the schema declares no properties");
            return;
        }

        foreach (var name in expected.Keys.Where(name => !actual.Contains(name)))
        {
            problems.Add($"{path}: the record has {name}, the schema does not");
        }

        foreach (var name in actual.Where(name => !expected.ContainsKey(name)))
        {
            problems.Add($"{path}: the schema has {name}, the record does not");
        }

        foreach (var (name, propertyType) in expected)
        {
            if (!actual.Contains(name))
            {
                continue;
            }

            var node = schema["properties"]![name]!;
            var nested = NestedRecord(propertyType);

            if (nested is not null)
            {
                // An array of records describes its element type under `items`.
                var target = ReferenceEquals(nested, propertyType) ? node : node["items"];

                if (target is null)
                {
                    problems.Add($"{path}.{name}: the schema declares no items for an array of objects");
                    continue;
                }

                Compare(target, nested, $"{path}.{name}", problems);
            }
        }
    }

    /// <summary>Every schema node that describes an object, paired with the record it describes.</summary>
    private static List<(JsonNode Node, Type Type, string Path)> Objects(JsonNode schema, Type type, string path)
    {
        List<(JsonNode, Type, string)> found = [(schema, type, path)];

        foreach (var (name, propertyType) in JsonProperties(type))
        {
            var nested = NestedRecord(propertyType);
            var node = schema["properties"]?[name];

            if (nested is null || node is null)
            {
                continue;
            }

            var target = ReferenceEquals(nested, propertyType) ? node : node["items"];
            if (target is not null)
            {
                found.AddRange(Objects(target, nested, $"{path}.{name}"));
            }
        }

        return found;
    }

    /// <summary>
    /// The record's JSON property names in declaration order, honoring
    /// <see cref="JsonPropertyNameAttribute"/> and otherwise the camelCase policy every DocuMe file
    /// is written with (<see cref="Core.Json.DocumeJson"/>).
    /// </summary>
    private static Dictionary<string, Type> JsonProperties(Type type)
    {
        var properties = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

            properties[name] = property.PropertyType;
        }

        return properties;
    }

    /// <summary>
    /// The config record a property describes: itself when it is one, its element type when it is a
    /// collection of them, and <c>null</c> for a scalar or a collection of scalars.
    /// </summary>
    private static Type? NestedRecord(Type propertyType)
    {
        if (IsConfigRecord(propertyType))
        {
            return propertyType;
        }

        if (!typeof(IEnumerable).IsAssignableFrom(propertyType) || !propertyType.IsGenericType)
        {
            return null;
        }

        var element = propertyType.GetGenericArguments()[0];
        return IsConfigRecord(element) ? element : null;
    }

    private static bool IsConfigRecord(Type type) =>
        type.IsClass
        && type != typeof(string)
        && string.Equals(type.Namespace, typeof(DocumeConfig).Namespace, StringComparison.Ordinal);

    /// <summary>
    /// Every required field that is a leaf — a required object whose own required children carry the
    /// real constraint is walked through, so <c>confluence</c> yields <c>confluence.baseUrl</c>.
    /// </summary>
    private static List<string> RequiredLeaves(JsonNode schema, Type type, string prefix)
    {
        var leaves = new List<string>();
        var required = schema["required"]?.AsArray().Select(node => node!.GetValue<string>()) ?? [];
        var properties = JsonProperties(type);

        foreach (var name in required)
        {
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            var nested = properties.TryGetValue(name, out var propertyType) ? NestedRecord(propertyType) : null;
            var node = schema["properties"]?[name];

            if (nested is null || node is null)
            {
                leaves.Add(path);
                continue;
            }

            leaves.AddRange(RequiredLeaves(node, nested, path));
        }

        return leaves;
    }

    private static DocumeConfig Complete() => new()
    {
        Confluence = new ConfluenceConfig { BaseUrl = "https://example.atlassian.net/wiki", SpaceKey = "DOCS" },
        Wiki = new WikiConfig { Root = "docs/wiki" },
    };

    /// <summary>The complete config with one required leaf blanked out.</summary>
    private static DocumeConfig Without(string leaf)
    {
        var complete = Complete();

        return leaf switch
        {
            "confluence.baseUrl" => complete with
            {
                Confluence = complete.Confluence with { BaseUrl = null },
            },
            "confluence.spaceKey" => complete with
            {
                Confluence = complete.Confluence with { SpaceKey = null },
            },
            "wiki.root" => complete with { Wiki = complete.Wiki with { Root = string.Empty } },
            _ => throw new InvalidOperationException(
                $"The schema requires {leaf}, which this test does not know how to remove. Add a case "
                + "here and confirm ConfigLoader.Validate enforces it."),
        };
    }

    private static JsonNode Schema() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "schema", "docume.schema.json")))!;

    private static string RepoRoot { get; } = Locate();

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>: the schema ships in the tree, is served
    /// from the tree by raw.githubusercontent, and has no build artifact.
    /// </summary>
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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so schema/docume.schema.json cannot be found.");
    }
}
