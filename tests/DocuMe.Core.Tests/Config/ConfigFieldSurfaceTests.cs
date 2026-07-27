using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocuMe.Core.Config;
using DocuMe.Core.Tests.Fixtures;
using Shouldly;

namespace DocuMe.Core.Tests.Config;

/// <summary>
/// Every <c>docume.json</c> field path named anywhere in the shipped tree, held against the fields that
/// exist.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConfigSchemaTests"/> pins the schema against the record; <see cref="ConfigReferencePageTests"/>
/// pins <c>docs/wiki/20-reference/configuration.md</c> against the schema. Both hold one artifact each, and
/// a config field is promised in more than one: eight shipped files name a field path between them —
/// <c>PLAN.md</c>, the CLI reference, <c>_meta/GAPS.md</c>, <c>_meta/STYLE.md</c> and four
/// <c>SKILL.md</c> files, none of which any of those checks reads. Rename <c>dashboard.title</c> and the
/// config page fails loudly while <c>docs/wiki/20-reference/cli.md</c> goes on offering a key the loader
/// now drops in silence.
/// </para>
/// <para>
/// This is the live-knob half of the inventory
/// <c>PlanDataContractTests.Every_shipped_artifact_that_names_a_dead_knob_is_recorded</c> keeps for the
/// dead ones, and it takes the same lesson: a promise spread across the tree cannot be guarded one file at
/// a time. It is a behaviour check rather than an inventory, though — a field path either resolves or it
/// does not, so there is no list to keep and no judgement to defer.
/// </para>
/// <para>
/// The expected side comes from the records by reflection, not from a list here and not from the schema:
/// the record is what a rename actually edits, and a hand-listed expectation is the same artifact as the
/// prose and rots with it.
/// </para>
/// </remarks>
public sealed partial class ConfigFieldSurfaceTests
{
    [Fact]
    public void Every_config_field_a_shipped_artifact_names_is_a_field_that_exists()
    {
        var declared = FieldPaths(typeof(DocumeConfig), string.Empty);
        var sections = declared
            .Where(path => !path.Contains('.', StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        const string vacuous = "No config section was found on the record at all, so this sweep would pass "
            + "on anything. The reflection walk is broken, not the tree.";

        sections.ShouldNotBeEmpty(vacuous);

        var named = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var file in ShippedTree.Files())
        {
            var matches = BacktickedPath().Matches(File.ReadAllText(file.Absolute));

            foreach (var path in matches.Select(match => match.Groups["path"].Value))
            {
                // Only paths headed by a real section: `docume.json` and `state.json` are backticked
                // dotted names too, and neither is a field path.
                if (!sections.Contains(path.Split('.')[0]))
                {
                    continue;
                }

                if (!named.TryGetValue(path, out var files))
                {
                    files = new SortedSet<string>(StringComparer.Ordinal);
                    named[path] = files;
                }

                files.Add(file.Relative);
            }
        }

        const string unswept = "No shipped artifact names a single config field path, which means the sweep "
            + "found nothing rather than found nothing wrong. The roots it walks have moved.";

        named.ShouldNotBeEmpty(unswept);

        foreach (var (path, files) in named)
        {
            var phantom = $"[{string.Join(", ", files)}] name `{path}`, and no such field exists. A consumer "
                + "who copies it into docume.json is told nothing: the loader drops unknown keys in silence "
                + "and only the schema's additionalProperties reports the misspelling. Either the field was "
                + "renamed and this mention was left behind, or the mention is a typo.";

            declared.ShouldContain(path, customMessage: phantom);
        }
    }

    /// <summary>
    /// The config's field paths in JSON spelling — <c>wiki</c>, <c>wiki.root</c>,
    /// <c>wiki.extraPages.path</c> — walked off the records that bind them.
    /// </summary>
    /// <remarks>
    /// List elements collapse onto their parent path rather than carrying an index, because the tree spells
    /// a member of <c>extraPages</c> as <c>extraPages.path</c> when it spells it at all.
    /// </remarks>
    private static HashSet<string> FieldPaths(Type type, string prefix)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = JsonName(property);
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            found.Add(path);

            var nested = Nested(property.PropertyType);

            if (nested is not null)
            {
                found.UnionWith(FieldPaths(nested, path));
            }
        }

        return found;
    }

    /// <summary>The record a property descends into, or <see langword="null"/> where the property is a leaf.</summary>
    private static Type? Nested(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = type.IsGenericType ? type.GetGenericArguments()[0] : null;

            return element is not null && Nested(element) is not null ? element : null;
        }

        return type.Assembly == typeof(DocumeConfig).Assembly ? type : null;
    }

    /// <summary>The name the property serializes under, matching the loader's camelCase policy.</summary>
    private static string JsonName(PropertyInfo property)
    {
        var declared = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;

        if (declared is not null)
        {
            return declared;
        }

        var name = property.Name;

        return string.Concat(char.ToLowerInvariant(name[0]), name[1..]);
    }

    /// <summary>
    /// A dotted path in backticks whose head is one of the config's own sections — <c>`wiki.root`</c>,
    /// <c>`confluence.spaceKey`</c>. Anchored on the head so ordinary prose carrying a full stop cannot
    /// match, and read out of backticks only, because that is how the tree spells a key it means literally.
    /// </summary>
    [GeneratedRegex(
        @"`(?<path>[a-z][A-Za-z0-9]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)`",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex BacktickedPath();
}
