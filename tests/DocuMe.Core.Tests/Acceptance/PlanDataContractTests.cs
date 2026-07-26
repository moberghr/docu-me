using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocuMe.Core.Config;
using DocuMe.Core.Feedback;
using DocuMe.Core.Json;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;
using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// PLAN.md §5's four data contracts held against the types that carry them, and against the code that
/// reads them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure this exists to catch.</strong> §5 is where the plan writes down the files a
/// consumer edits and the machine owns, field by field. Nothing read that list back.
/// <see cref="Config.ConfigSchemaTests"/> pins <c>schema/docume.schema.json</c> against
/// <see cref="DocumeConfig"/>, but both sides of that comparison are shipped artifacts, so a field §5.1
/// declares and neither has is invisible to it; §5.2, §5.3 and §5.4 have no schema at all.
/// <see cref="PlanCommandSpecTests"/> does the same job for §6's command surface, and finding
/// <c>--notify-reviewers</c> unbuilt there is what said §5 was worth reading too.
/// </para>
/// <para>
/// <strong>A declared field nobody reads is the same defect as an unbuilt flag.</strong> The shape check
/// alone would pass on a field that binds, round-trips and changes nothing: a consumer sets it, the loader
/// keeps it, and no behaviour asks. That is how <c>links.repoBlobUrl</c> came to be declared in §5.1,
/// specified in §6.2 step 2, described to a generative skill as working
/// (<c>plugin/skills/docs-loop/SKILL.md</c>) and read by no line of <c>src/</c>. So the second half of this
/// file counts reads, and every member with none is on <see cref="DeadFields"/> with why.
/// </para>
/// <para>
/// <strong>What the read count can and cannot see.</strong> It is a scan of <c>src/</c> for
/// <c>.Member</c>, so it is blind by design in one direction: a member whose name is also a live member's
/// name somewhere else (<c>Version</c>, <c>Title</c>, <c>Status</c>) reads as live whether or not its own
/// declaring type is ever asked. It is never wrong the other way — nothing can be reported dead while
/// something reads it — which is the direction that matters for a list that must not cry wolf. Full-line
/// comments are dropped first, so an <c>&lt;see cref="Type.Member"/&gt;</c> is not a read.
/// </para>
/// <para>
/// Both lists are double-entry, the way <see cref="PlanCommandSpecTests"/> records §6's unbuilt options: a
/// new gap fails, and so does closing a gap without striking it off.
/// </para>
/// </remarks>
public sealed partial class PlanDataContractTests
{
    /// <summary>The page a consumer hand-writes <c>docume.json</c> from.</summary>
    private const string ConfigPagePath = "docs/wiki/20-reference/configuration.md";

    /// <summary>One §5 subsection and the type that carries it.</summary>
    /// <param name="Section">Subsection number, e.g. <c>5.1</c>.</param>
    /// <param name="Type">The root type the contract binds to.</param>
    /// <param name="Yaml">Whether the block is YAML (§5.2) rather than JSONC.</param>
    private sealed record Contract(string Section, Type Type, bool Yaml);

    /// <summary>A contract member no line of <c>src/</c> reads.</summary>
    private sealed record DeadField
    {
        /// <summary>The member, keyed as <see cref="Leaves"/> spells it.</summary>
        public required string Path { get; init; }

        /// <summary>Why the gap stands, and what closing it would take.</summary>
        public required string Why { get; init; }

        /// <summary>
        /// Its dotted <c>docume.json</c> name when it is an input a consumer can set and be ignored on;
        /// <c>null</c> for a member whose reader is a human or which is ignored deliberately.
        /// </summary>
        public string? Knob { get; init; }
    }

    private static readonly Contract[] Contracts =
    [
        new("5.1", typeof(DocumeConfig), Yaml: false),
        new("5.2", typeof(PageFrontmatter), Yaml: true),
        new("5.3", typeof(DocumeState), Yaml: false),
        new("5.4", typeof(FeedbackItem), Yaml: false),
    ];

    /// <summary>
    /// Members the types carry and §5's blocks do not declare. Each one is a plan edit nobody has made,
    /// not a field this suite may quietly accept.
    /// </summary>
    private static readonly (string Path, string Why)[] UnspecifiedMembers =
    [
        ("5.1 confluence.protectedSpaces",
            "The write lock behind CLAUDE.md §0.1 and rule §1.4 (PublishGuard), added as config rather "
            + "than as a space key hardcoded in the tool. It is in schema/docume.schema.json and in "
            + "docs/wiki/20-reference/configuration.md; §5.1's example block predates it."),
        ("5.3 pages.*.diagramWidths",
            "The published `ac:width` per diagram attachment (PLAN.md §7, DiagramImageWidth). Remembered "
            + "in state because a publish re-renders only the diagrams it uploads, so a text-only edit "
            + "would otherwise republish unchanged diagrams without their width. §5.3's example block "
            + "predates it."),
    ];

    /// <summary>
    /// Members no line of <c>src/</c> reads, with why the gap stands. Two of these are dead knobs — a
    /// consumer can set them and nothing asks — and two are fields whose reader is a human.
    /// </summary>
    /// <remarks>
    /// A <see cref="DeadField.Knob"/> entry is an input a consumer can write, so it is also owed a line on
    /// the page consumers read: <see cref="The_config_reference_names_every_field_that_is_inert"/> holds
    /// this list and <c>docs/wiki/20-reference/configuration.md</c> to the same two names.
    /// </remarks>
    private static readonly DeadField[] DeadFields =
    [
        new()
        {
            Path = "5.1 links.repoBlobUrl",
            Knob = "links.repoBlobUrl",
            Why = "A DEAD KNOB, and the find that opened this file. §5.1 declares it, §6.2 step 2 "
                + "specifies the behaviour (\"linkify source refs to `repoBlobUrl@baselineSha` if "
                + "configured\"), and plugin/skills/docs-loop/SKILL.md tells the skill that publish does "
                + "it. No line of src/ reads the field, so a consumer who sets it gets nothing and is told "
                + "otherwise. Build the linkification, or strike the promise from §6.2 and the skill — a "
                + "spec decision, not this suite's to take.",
        },
        new()
        {
            Path = "5.1 drift.defaultBranch",
            Knob = "drift.defaultBranch",
            Why = "A DEAD KNOB. §5.1 declares it and §10's PR job writes `docume drift --baseline "
                + "origin/<defaultBranch>`, but no command reads it: templates/workflows/docs-drift-pr.yml "
                + "computes a merge base from the pull request instead (deliberately, and its comment says "
                + "the setting is \"for local runs\"), and a local `drift` with no --baseline falls back to "
                + "state.json's baselineSha. Either the fallback should consult it, or §5.1 and §10 should "
                + "stop implying a branch default exists.",
        },
        new()
        {
            Path = "5.1 $schema",
            Why = "NOT A GAP, and listed so that reading nothing is a recorded choice: DocumeConfig.Schema "
                + "exists to keep the loader from dropping the key it binds, and the URL `docume init` "
                + "writes is asserted by ConfigSchemaTests against both the shipped schema file and "
                + "plugin.json's repository. Runtime behaviour deliberately ignores it.",
        },
        new()
        {
            Path = "5.3 pages.*.approval.history[].by",
            Why = "WRITE-ONLY BY DESIGN. §8 keeps approval history for audit; StateUpdates appends an "
                + "entry per approval and nothing reads one back. Its reader is a human on the state.json "
                + "diff, so the field earns its place by being serialized. Unlike a dead knob it is an "
                + "output, not an input: no consumer can set it and be ignored.",
        },
        new()
        {
            Path = "5.3 pages.*.approval.history[].at",
            Why = "WRITE-ONLY BY DESIGN, exactly as `history[].by` above and for the same reason.",
        },
    ];

    /// <summary>
    /// Anti-vacuity guard: every assertion below reads the parsed blocks, so a renumbered §5 or a fence
    /// that stopped being <c>jsonc</c> would turn them all green by comparing nothing at all.
    /// </summary>
    [Fact]
    public void Section_5_parses_into_one_field_list_per_contract()
    {
        foreach (var contract in Contracts)
        {
            var block = SpecBlock(contract);

            var reformatted = $"PLAN.md §{contract.Section}'s block parsed to {block.Count} top-level "
                + "field(s). It has either been reformatted or renumbered; point this test at it rather "
                + "than letting the comparison run on nothing.";

            block.Count.ShouldBeGreaterThanOrEqualTo(3, reformatted);
        }

        // The four blocks together, so shrinking one cannot hide behind the others being intact.
        Contracts.Sum(contract => Leaves(contract).Count).ShouldBeGreaterThanOrEqualTo(30);
    }

    /// <summary>
    /// The shape check, in both directions: a field §5 declares and no type carries fails, and so does a
    /// member the types carry that §5 never wrote down and <see cref="UnspecifiedMembers"/> does not name.
    /// </summary>
    [Fact]
    public void Every_field_the_plan_declares_is_carried_and_every_member_carried_is_declared()
    {
        var missing = new List<string>();
        var unspecified = new List<string>();

        foreach (var contract in Contracts)
        {
            Compare(SpecBlock(contract), contract.Type, $"{contract.Section} ", missing, unspecified);
        }

        var recorded = UnspecifiedMembers.Select(member => member.Path).ToHashSet(StringComparer.Ordinal);

        var undeclared = unspecified.Where(path => !recorded.Contains(path)).ToList();
        var closed = recorded.Where(path => !unspecified.Contains(path, StringComparer.Ordinal)).ToList();

        missing.ShouldBeEmpty(
            customMessage: "PLAN.md §5 declares a field no type carries, so a consumer who writes it is "
                + "silently ignored (the loader drops what it does not recognize). Build the member, or "
                + $"correct §5: {string.Join(" // ", missing)}");

        undeclared.ShouldBeEmpty(
            customMessage: "A data contract carries a member PLAN.md §5 does not declare. Add it to §5's "
                + $"block, or record it in {nameof(UnspecifiedMembers)} with why the plan has not caught "
                + $"up: {string.Join(" // ", undeclared)}");

        closed.ShouldBeEmpty(
            customMessage: $"An entry in {nameof(UnspecifiedMembers)} is declared in §5 now, or is no "
                + "longer a member at all. Strike it off: a record of gaps that keeps naming closed ones "
                + $"stops being read. {string.Join(" // ", closed)}");
    }

    /// <summary>
    /// The read check, in both directions: a contract member no line of <c>src/</c> reads must be on
    /// <see cref="DeadFields"/>, and an entry there that has since acquired a reader must come off.
    /// </summary>
    [Fact]
    public void Every_field_the_plan_declares_is_read_by_something_or_listed_as_dead()
    {
        var sources = ProductSources();
        var recorded = DeadFields.Select(dead => dead.Path).ToHashSet(StringComparer.Ordinal);

        var unread = new List<string>();
        var live = new List<string>();

        foreach (var contract in Contracts)
        {
            foreach (var (path, member) in Leaves(contract))
            {
                var reads = sources.Count(source => Reference(member).IsMatch(source));

                if (reads == 0 && !recorded.Contains(path))
                {
                    unread.Add($"{path} ({member})");
                    continue;
                }

                if (reads > 0 && recorded.Contains(path))
                {
                    live.Add($"{path} is read by {reads} file(s)");
                }
            }
        }

        unread.ShouldBeEmpty(
            customMessage: "A field PLAN.md §5 declares is read by no line of src/. If it is an input, a "
                + "consumer can set it and nothing will happen — the defect that let links.repoBlobUrl be "
                + $"promised to a skill and implemented nowhere. Wire it up, or record it in "
                + $"{nameof(DeadFields)} with why: {string.Join(" // ", unread)}");

        live.ShouldBeEmpty(
            customMessage: $"An entry in {nameof(DeadFields)} has a reader now. Strike it off — and if it "
                + "was one of the two dead knobs, say so in PLAN.md and in the skill that describes it: "
                + $"{string.Join(" // ", live)}");
    }

    /// <summary>
    /// A dead knob is an input, so the page a consumer hand-writes <c>docume.json</c> from has to say it
    /// does nothing — in both directions, so the note cannot outlive the inertness it describes.
    /// </summary>
    /// <remarks>
    /// Without this, the finding lived only in a test list: a consumer reading
    /// <c>docs/wiki/20-reference/configuration.md</c> would set <c>links.repoBlobUrl</c>, get silence, and
    /// have no way to tell a dead field from a misconfigured one. Named fields rather than prose because
    /// prose is what rots.
    /// </remarks>
    [Fact]
    public void The_config_reference_names_every_field_that_is_inert()
    {
        var page = File.ReadAllText(
            Path.Combine(DocumeCli.RepoRoot, "docs", "wiki", "20-reference", "configuration.md"));

        var section = InertSection().Match(page);

        section.Success.ShouldBeTrue(
            $"{ConfigPagePath} has no \"### Two fields are inert\" section. Two fields load and are read "
            + "by nothing; the page a consumer hand-writes the file from is where that belongs.");

        var body = section.Groups["body"].Value;
        var knobs = DeadFields.Select(dead => dead.Knob).OfType<string>().ToList();

        // The section's own bullets, not any mention of the name: its opening sentence lists both fields,
        // so plain containment stays green when a bullet is deleted (measured — the mutation harness at
        // .mtk/paths-123/mutate-data-contracts.py missed exactly that case first time round).
        var bullets = ConfigField()
            .Matches(body)
            .Select(match => match.Groups["name"].Value)
            .ToList();

        foreach (var knob in knobs)
        {
            var unsaid = $"{knob} is read by nothing and {ConfigPagePath}'s inert section has no bullet "
                + "for it, so a consumer who sets it is told nothing at all.";

            bullets.ShouldContain(knob, customMessage: unsaid);
        }

        // The other direction: the section may not name a field that has since become live, and it may not
        // name a field this list never called inert.
        foreach (var named in bullets)
        {
            var unlisted = $"{ConfigPagePath} calls {named} inert and {nameof(DeadFields)} does not. "
                + "Either it acquired a reader — delete the bullet — or the list is missing it.";

            knobs.ShouldContain(named, customMessage: unlisted);
        }
    }

    /// <summary>
    /// Walks a parsed §5 block against the type that binds it, collecting both directions of disagreement.
    /// </summary>
    private static void Compare(
        JsonObject spec,
        Type type,
        string path,
        List<string> missing,
        List<string> unspecified)
    {
        var elementType = DictionaryValue(type);

        if (elementType is not null)
        {
            // The keys are data — page paths, attachment file names — so only one sample value is worth
            // descending into, and the key names claim nothing about the type.
            foreach (var property in spec)
            {
                if (property.Value is JsonObject nested)
                {
                    Compare(nested, elementType, $"{path}*.", missing, unspecified);
                }
            }

            return;
        }

        var members = Members(type);

        foreach (var property in spec)
        {
            if (!members.TryGetValue(property.Key, out var member))
            {
                missing.Add($"{path}{property.Key}");
                continue;
            }

            Descend(property.Value, member.PropertyType, $"{path}{property.Key}", missing, unspecified);
        }

        unspecified.AddRange(members.Keys
            .Where(name => !spec.ContainsKey(name))
            .Select(name => $"{path}{name}"));
    }

    /// <summary>Recurses into a field's own shape where the example gives one.</summary>
    private static void Descend(
        JsonNode? value,
        Type type,
        string path,
        List<string> missing,
        List<string> unspecified)
    {
        if (value is JsonObject nested)
        {
            Compare(nested, type, $"{path}.", missing, unspecified);

            return;
        }

        if (value is JsonArray array && array.FirstOrDefault() is JsonObject element)
        {
            var item = ListElement(type);

            item.ShouldNotBeNull($"PLAN.md {path} is a list of objects and {type.Name} is not a list.");
            Compare(element, item, $"{path}[].", missing, unspecified);
        }
    }

    /// <summary>Every leaf and branch member of a contract, keyed the way the deviation lists spell it.</summary>
    private static List<(string Path, string Member)> Leaves(Contract contract)
    {
        var found = new List<(string, string)>();
        Collect(contract.Type, $"{contract.Section} ", found);

        return found;
    }

    private static void Collect(Type type, string path, List<(string Path, string Member)> found)
    {
        var value = DictionaryValue(type);

        if (value is not null)
        {
            Collect(value, $"{path}*.", found);

            return;
        }

        foreach (var (name, member) in Members(type))
        {
            found.Add(($"{path}{name}", member.Name));

            var element = ListElement(member.PropertyType);

            if (element is not null && !IsLeaf(element))
            {
                Collect(element, $"{path}{name}[].", found);
                continue;
            }

            if (!IsLeaf(member.PropertyType))
            {
                Collect(member.PropertyType, $"{path}{name}.", found);
            }
        }
    }

    /// <summary>JSON name → property, named the way <see cref="DocumeJson"/> serializes it.</summary>
    private static Dictionary<string, PropertyInfo> Members(Type type)
    {
        var policy = DocumeJson.Options.PropertyNamingPolicy;

        policy.ShouldNotBeNull("DocumeJson stopped setting a naming policy, so these names are guesses.");

        return type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(
                property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                    ?? policy.ConvertName(property.Name),
                property => property,
                StringComparer.Ordinal);
    }

    /// <summary>The value type of a dictionary member, or <c>null</c> for anything else.</summary>
    private static Type? DictionaryValue(Type type) => Argument(type, typeof(IReadOnlyDictionary<,>), 1);

    /// <summary>The element type of a list member, or <c>null</c> for anything else.</summary>
    private static Type? ListElement(Type type) => Argument(type, typeof(IReadOnlyList<>), 0);

    private static Type? Argument(Type type, Type definition, int index)
    {
        var match = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == definition);

        return match?.GetGenericArguments()[index];
    }

    /// <summary>Whether a member's type is a value in its own right rather than a nested contract.</summary>
    private static bool IsLeaf(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(string)
        || Nullable.GetUnderlyingType(type) is not null
        || DictionaryValue(type) == typeof(string)
        || ListElement(type) == typeof(string);

    /// <summary>§5.x's first fenced block, parsed into an object whose keys are its field names.</summary>
    private static JsonObject SpecBlock(Contract contract)
    {
        var body = Subsection(contract.Section);
        var fence = Fence().Match(body);

        fence.Success.ShouldBeTrue($"PLAN.md §{contract.Section} has no fenced example block.");

        var block = fence.Groups["block"].Value;

        return contract.Yaml ? YamlKeys(block) : Jsonc(block);
    }

    /// <summary>
    /// The top-level keys of a YAML block (§5.2 is flat). Values are dropped: the point is the field list,
    /// and <see cref="Descend"/> only recurses where the example shows an object.
    /// </summary>
    private static JsonObject YamlKeys(string block)
    {
        var keys = new JsonObject();

        foreach (var line in block.Split('\n'))
        {
            var match = YamlKey().Match(line.TrimEnd('\r'));

            if (match.Success)
            {
                keys[match.Groups["name"].Value] = null;
            }
        }

        return keys;
    }

    private static JsonObject Jsonc(string block) =>
        JsonNode.Parse(StripComments(block))!.AsObject();

    /// <summary>
    /// Drops <c>//</c> comments without touching a <c>//</c> inside a string — §5.1's own <c>$schema</c>
    /// value is an <c>https://</c> URL, so a naive strip would eat the field this block declares.
    /// </summary>
    private static string StripComments(string block)
    {
        var stripped = new StringBuilder(block.Length);
        var inString = false;
        var escaped = false;

        var index = 0;

        while (index < block.Length)
        {
            var character = block[index];
            index++;

            if (escaped)
            {
                escaped = false;
                stripped.Append(character);
                continue;
            }

            if (inString && character == '\\')
            {
                escaped = true;
                stripped.Append(character);
                continue;
            }

            if (character == '"')
            {
                inString = !inString;
                stripped.Append(character);
                continue;
            }

            if (!inString && character == '/' && index < block.Length && block[index] == '/')
            {
                // Stops on the newline rather than consuming it, so the loop keeps the line structure.
                while (index < block.Length && block[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            stripped.Append(character);
        }

        return stripped.ToString();
    }

    /// <summary>§5.x's body, up to the next heading of any level.</summary>
    private static string Subsection(string section)
    {
        var match = Regex.Match(
            PlanText(),
            $@"\n### {Regex.Escape(section)} (?<body>[\s\S]*?)(?=\n#{{2,3}} )",
            RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));

        match.Success.ShouldBeTrue(
            $"PLAN.md has no \"### {section}\" subsection, so this whole file is reading nothing. Point it "
            + "at §5's new numbering rather than deleting it.");

        return match.Groups["body"].Value;
    }

    /// <summary>
    /// Every C# file under <c>src/</c>, with full-line comments dropped so an
    /// <c>&lt;see cref="Type.Member"/&gt;</c> does not read as a use of the member.
    /// </summary>
    private static List<string> ProductSources() =>
        Directory
            .EnumerateFiles(Path.Combine(DocumeCli.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => Code(File.ReadAllText(path)))
            .ToList();

    private static string Code(string text) =>
        string.Join(
            '\n',
            text.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>A read or write through an instance: the declaration itself carries no leading dot.</summary>
    private static Regex Reference(string member) =>
        new($@"\.{Regex.Escape(member)}\b", RegexOptions.None, TimeSpan.FromSeconds(2));

    private static string PlanText() =>
        File.ReadAllText(Path.Combine(DocumeCli.RepoRoot, "PLAN.md"));

    [GeneratedRegex(@"```(?:jsonc|json|yaml)\n(?<block>[\s\S]*?)\n```", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Fence();

    [GeneratedRegex(@"\n### Two fields are inert[^\n]*\n(?<body>[\s\S]*?)(?=\n#{2,3} )", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex InertSection();

    /// <summary>A dotted config field name in a bullet's bold lead, e.g. <c>- **`links.repoBlobUrl`**</c>.</summary>
    [GeneratedRegex(@"^- \*\*`(?<name>[a-z][A-Za-z0-9]*\.[a-zA-Z0-9.]+)`\*\*", RegexOptions.ExplicitCapture | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConfigField();

    [GeneratedRegex(@"^(?<name>[a-z][A-Za-z0-9]*):", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex YamlKey();
}
