using DocuMe.Core.Scaffolding;
using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.Scaffolding;

/// <summary>
/// <c>docume init --adopt</c> (PLAN.md §6.1): the state skeleton built from a wiki the repo already
/// has. Driven through <see cref="ProjectScaffolder.Scaffold(string, string?, string?, bool, string?, DocuMe.Core.Config.AgentRail?)"/>
/// rather than <see cref="WikiAdopter.Adopt"/> directly, because half of what can go wrong is the
/// wiring — which file is read, which is written, and which row the outcome is reported on.
/// </summary>
public sealed class WikiAdopterTests : IDisposable
{
    private const string WikiRoot = "docs/wiki";
    private const string StatePath = "docs/wiki/_meta/state.json";

    private readonly string _dir = Directory.CreateTempSubdirectory("docume-adopt-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Adopt_writes_one_entry_per_page_carrying_its_title()
    {
        Page("README.md", "# Handbook");
        Page("domains/loans.md", "---\ntitle: Loans Domain\n---\n\n# Loans\n");
        Page("domains/nested/deep.md", "# Deep Page");

        var state = Adopt();

        state.Action.ShouldBe(ScaffoldAction.Created);
        state.Note.ShouldNotBeNull().ShouldContain("adopted 3 pages");

        var adopted = ReadState();
        adopted.Pages.Keys.ShouldBe(["README.md", "domains/loans.md", "domains/nested/deep.md"]);
        adopted.Pages["domains/loans.md"].Title.ShouldBe("Loans Domain");
        adopted.Pages["domains/nested/deep.md"].Title.ShouldBe("Deep Page");
    }

    /// <summary>
    /// An entry with no <c>pageId</c> plans a create exactly as a missing entry does, so the skeleton
    /// alone changes no publish decision — but a consumer whose pages ARE already in Confluence has to
    /// be told, because a create against an existing title collides instead of updating.
    /// </summary>
    [Fact]
    public void Adopt_says_out_loud_that_unseeded_pages_will_be_created()
    {
        Page("README.md", "# Handbook");

        var note = Adopt().Note.ShouldNotBeNull();

        note.ShouldContain("no pageIds were seeded");
        note.ShouldContain("collide");
        ReadState().Pages["README.md"].PageId.ShouldBeNull();
    }

    /// <summary>
    /// The one skeleton file that would become a published page. An adopted repo has its own root
    /// page, and adding DocuMe's boilerplate to someone's documentation tree is not adoption.
    /// </summary>
    [Fact]
    public void Adopt_does_not_add_a_root_page_to_the_wiki_it_adopts()
    {
        Page("index.md", "# Index");

        var results = Scaffold(adopt: true);
        var readme = results.Single(r => r.RelativePath.EndsWith("/README.md", StringComparison.Ordinal));

        readme.Action.ShouldBe(ScaffoldAction.Skipped);
        readme.Note.ShouldNotBeNull().ShouldContain("--adopt");
        File.Exists(Full($"{WikiRoot}/README.md")).ShouldBeFalse();
        ReadState().Pages.Keys.ShouldBe(["index.md"]);
    }

    /// <summary>
    /// The normal route in: a plain <c>init</c> writes an empty state file, so an adopt that refused
    /// every existing file would refuse the one it just created itself. Its <c>baselineSha</c> is
    /// carried through — that field says which commit the wiki was generated against, which adoption
    /// knows nothing about and must not erase.
    /// </summary>
    [Fact]
    public void Adopt_fills_the_empty_state_a_plain_init_wrote_and_keeps_its_shas()
    {
        Page("README.md", "# Handbook");
        Scaffold(adopt: false);
        StateStore.Save(Full(StatePath), ReadState() with { BaselineSha = "abc123", LastPublishedSha = "def456" });

        var state = Adopt();

        state.Action.ShouldBe(ScaffoldAction.Updated);

        var adopted = ReadState();
        adopted.Pages.Keys.ShouldBe(["README.md"]);
        adopted.BaselineSha.ShouldBe("abc123");
        adopted.LastPublishedSha.ShouldBe("def456");
    }

    /// <summary>
    /// Rule §9.4 where it bites hardest. Those entries are the only record of what is published: an
    /// "adoption" that replaced them would re-create every page and revoke every approval.
    /// </summary>
    [Fact]
    public void Adopt_leaves_a_state_file_that_already_lists_pages_untouched()
    {
        Page("README.md", "# Handbook");
        StateStore.Save(
            Full(StatePath),
            new DocumeState
            {
                Pages = new Dictionary<string, PageState>(StringComparer.Ordinal)
                {
                    ["README.md"] = new() { PageId = "999", Title = "Handbook", ContentHash = "sha256:x" },
                },
            });
        var before = File.ReadAllText(Full(StatePath));

        var state = Adopt();

        state.Action.ShouldBe(ScaffoldAction.Skipped);
        state.Note.ShouldNotBeNull().ShouldContain("already lists 1 page");
        File.ReadAllText(Full(StatePath)).ShouldBe(before);
    }

    /// <summary>
    /// A state file that cannot be parsed is a typo to fix, not a reason to start over: overwriting it
    /// would throw away however many page ids it holds.
    /// </summary>
    [Fact]
    public void Adopt_refuses_a_state_file_it_cannot_read()
    {
        Page("README.md", "# Handbook");
        const string broken = """{ "version": 1, "pages": { """;
        WriteFile(StatePath, broken);

        var state = Adopt();

        state.Action.ShouldBe(ScaffoldAction.Skipped);
        state.Note.ShouldNotBeNull().ShouldContain("could not be read");
        state.Note.ShouldNotContain("\n"); // printed as one line under a table
        File.ReadAllText(Full(StatePath)).ShouldBe(broken);
    }

    /// <summary>
    /// A hand-edited <c>"version": "1"</c>. <see cref="StateStore.Load"/> reads that member off a
    /// JsonNode, which throws <see cref="InvalidOperationException"/> rather than a
    /// <see cref="System.Text.Json.JsonException"/> — a crash out of the command a consumer runs to fix
    /// such things, if it were not caught.
    /// </summary>
    [Fact]
    public void Adopt_refuses_a_state_file_whose_version_is_not_a_number()
    {
        Page("README.md", "# Handbook");
        WriteFile(StatePath, """{ "version": "1", "pages": {} }""");

        Adopt().Action.ShouldBe(ScaffoldAction.Skipped);
    }

    [Fact]
    public void Adopt_refuses_when_there_is_no_wiki_to_adopt()
    {
        var state = Adopt();

        state.Action.ShouldBe(ScaffoldAction.Skipped);
        state.Note.ShouldNotBeNull().ShouldContain("there is no wiki");

        // And nothing was written in its place: a state file created here would be the empty one a
        // plain init writes, which is exactly what the consumer did not ask for.
        File.Exists(Full(StatePath)).ShouldBeFalse();
    }

    /// <summary>
    /// The wiki root exists but holds no pages — <c>_meta/**</c> is excluded by default, so a repo whose
    /// only file there is a STYLE.md lands here. Adopting nothing is reported, not passed off as done.
    /// </summary>
    [Fact]
    public void Adopt_refuses_a_wiki_with_no_pages_in_scope()
    {
        WriteFile($"{WikiRoot}/_meta/STYLE.md", "# Style");

        var state = Adopt();

        state.Action.ShouldBe(ScaffoldAction.Skipped);
        state.Note.ShouldNotBeNull().ShouldContain("no pages");
    }

    /// <summary>
    /// A tree that cannot be published cannot be adopted either: the skeleton's whole content is titles
    /// and paths, and a page with no title has neither. Publish would refuse it too (§6.2 step 1), so
    /// this is the same refusal moved earlier, with a pointer at the command that lists every problem.
    /// </summary>
    [Fact]
    public void Adopt_refuses_a_tree_that_cannot_be_published()
    {
        Page("README.md", "# Handbook");
        Page("titleless.md", "Just a paragraph, no heading.\n");

        var note = Adopt().Note.ShouldNotBeNull();

        note.ShouldContain("cannot be published");
        note.ShouldContain("titleless.md");
        note.ShouldContain("docume convert");
        File.Exists(Full(StatePath)).ShouldBeFalse();
    }

    /// <summary>
    /// The load-bearing half: these ids are what make the first publish 3 updates instead of 3 title
    /// collisions. Every key spelling here is one a legacy map plausibly uses — the bare wiki-relative
    /// path, a repo-root-relative one carrying the wiki root, and a Windows-written backslash path —
    /// and the values cover both an id quoted as a string and one left as a JSON number.
    /// </summary>
    [Fact]
    public void Adopt_seeds_page_ids_from_a_legacy_map()
    {
        Page("README.md", "# Handbook");
        Page("domains/loans.md", "# Loans");
        Page("domains/cards.md", "# Cards");
        const string map = """
            {
              "README.md": "111",
              "docs/wiki/domains/loans.md": 222,
              "docs\\wiki\\domains\\cards.md": "333"
            }
            """;
        WriteFile("legacy-map.json", map);

        var state = Adopt("legacy-map.json");

        state.Action.ShouldBe(ScaffoldAction.Created);
        state.Note.ShouldNotBeNull().ShouldContain("seeded 3 pageIds");

        var pages = ReadState().Pages;
        pages["README.md"].PageId.ShouldBe("111");
        pages["domains/loans.md"].PageId.ShouldBe("222");
        pages["domains/cards.md"].PageId.ShouldBe("333");
    }

    /// <summary>
    /// A previous DocuMe <c>state.json</c> is a legacy map — the entries live under <c>pages</c> and
    /// carry their id in a member. That is the one such file anybody other than AurServices is likely to
    /// have, so accepting it is worth the second shape.
    /// </summary>
    [Fact]
    public void Adopt_accepts_a_previous_state_file_as_a_legacy_map()
    {
        Page("README.md", "# Handbook");
        const string previous = """
            {
              "version": 1,
              "pages": { "README.md": { "pageId": "4242", "title": "Handbook" } }
            }
            """;
        WriteFile("old-state.json", previous);

        Adopt("old-state.json").Action.ShouldBe(ScaffoldAction.Created);

        ReadState().Pages["README.md"].PageId.ShouldBe("4242");
    }

    /// <summary>
    /// The failure a silent adoption would hide: a key spelled a way this reader does not normalize
    /// leaves its page with no id, which collides on its title at the first publish. The page is still
    /// adopted — the rest of the map is fine — so the note is the only thing that can say so.
    /// </summary>
    [Fact]
    public void Adopt_reports_map_entries_that_matched_no_page()
    {
        Page("README.md", "# Handbook");
        WriteFile("legacy-map.json", """{ "README.md": "111", "Loans Domain": "222" }""");

        var note = Adopt("legacy-map.json").Note.ShouldNotBeNull();

        note.ShouldContain("1 map entry matched no page");
        note.ShouldContain("'Loans Domain'");
        ReadState().Pages["README.md"].PageId.ShouldBe("111");
    }

    /// <summary>
    /// Frontmatter wins because it is a per-page annotation somebody wrote deliberately (§5.2 calls it
    /// pre-seeding for adopted pages) while the map is a bulk artifact. The disagreement is still named:
    /// one of the two is stale and only a human can say which.
    /// </summary>
    [Fact]
    public void Adopt_prefers_a_frontmatter_page_id_over_the_map_and_reports_the_disagreement()
    {
        Page("README.md", "---\npageId: \"555\"\n---\n\n# Handbook\n");
        WriteFile("legacy-map.json", """{ "README.md": "111" }""");

        var note = Adopt("legacy-map.json").Note.ShouldNotBeNull();

        ReadState().Pages["README.md"].PageId.ShouldBe("555");
        note.ShouldContain("from page frontmatter");
        note.ShouldContain("disagree on 1 page");
    }

    [Fact]
    public void Adopt_seeds_from_frontmatter_with_no_map_at_all()
    {
        Page("README.md", "---\npageId: \"777\"\n---\n\n# Handbook\n");
        Page("other.md", "# Other");

        var note = Adopt().Note.ShouldNotBeNull();

        ReadState().Pages["README.md"].PageId.ShouldBe("777");
        note.ShouldContain("seeded 1 pageId (from page frontmatter)");
        note.ShouldContain("the other 1 will be created");
    }

    /// <summary>
    /// A named map that cannot be read refuses the whole adoption rather than falling back to an
    /// unseeded skeleton: the consumer asked for the ids precisely because their pages exist, and a
    /// skeleton quietly missing them is worse than no skeleton at all.
    /// </summary>
    [Fact]
    public void Adopt_refuses_a_legacy_map_that_does_not_exist()
    {
        Page("README.md", "# Handbook");

        var state = Adopt("nope.json");

        state.Action.ShouldBe(ScaffoldAction.Skipped);
        state.Note.ShouldNotBeNull().ShouldContain("does not exist");
        File.Exists(Full(StatePath)).ShouldBeFalse();
    }

    [Fact]
    public void Adopt_refuses_a_legacy_map_that_is_not_valid_json()
    {
        Page("README.md", "# Handbook");
        WriteFile("legacy-map.json", """{ "README.md": """);

        var state = Adopt("legacy-map.json");

        state.Action.ShouldBe(ScaffoldAction.Skipped);
        state.Note.ShouldNotBeNull().ShouldContain("not valid JSON");
        state.Note.ShouldNotContain("\n");
    }

    /// <summary>
    /// The whole-file shape mismatch, as opposed to one odd entry: a map whose values are all something
    /// this reader cannot read would otherwise seed nothing and look like a successful adoption.
    /// </summary>
    [Fact]
    public void Adopt_refuses_a_legacy_map_with_no_usable_ids()
    {
        Page("README.md", "# Handbook");
        WriteFile("legacy-map.json", """{ "README.md": null, "other.md": { "confluence": {} } }""");

        var state = Adopt("legacy-map.json");

        state.Action.ShouldBe(ScaffoldAction.Skipped);
        state.Note.ShouldNotBeNull().ShouldContain("no usable");
    }

    /// <summary>
    /// One unusable entry among usable ones is not a refusal — but it is not silence either, since its
    /// page ends up unseeded.
    /// </summary>
    [Fact]
    public void Adopt_reports_a_single_unusable_map_entry_and_keeps_going()
    {
        Page("README.md", "# Handbook");
        Page("other.md", "# Other");
        WriteFile("legacy-map.json", """{ "README.md": "111", "other.md": {} }""");

        var note = Adopt("legacy-map.json").Note.ShouldNotBeNull();

        note.ShouldContain("1 map entry carried no readable page id");
        note.ShouldContain("'other.md'");
        ReadState().Pages["other.md"].PageId.ShouldBeNull();
    }

    /// <summary>
    /// <c>--adopt</c> reads the tree <c>wiki.root</c> names, not the default: a repo whose wiki lives
    /// elsewhere is the whole reason the flag exists.
    /// </summary>
    [Fact]
    public void Adopt_reads_the_wiki_where_the_config_points()
    {
        WriteFile(
            "docume.json",
            """{"confluence":{"baseUrl":"https://x.atlassian.net/wiki","spaceKey":"SBX"},"wiki":{"root":"documentation"}}""");
        WriteFile("documentation/handbook.md", "# Handbook");

        var state = Adopt();

        state.RelativePath.ShouldBe("documentation/_meta/state.json");
        state.Action.ShouldBe(ScaffoldAction.Created);
        StateStore.Load(Full("documentation/_meta/state.json")).Pages.Keys.ShouldBe(["handbook.md"]);
        File.Exists(Full(StatePath)).ShouldBeFalse();
    }

    /// <summary>
    /// A plain <c>init</c> tolerates an unreadable config and falls back to the defaults, but adoption
    /// cannot: the config is how it knows where the wiki is and which of its files are pages, so
    /// guessing would adopt the wrong tree.
    /// </summary>
    [Fact]
    public void Adopt_refuses_when_the_config_cannot_be_read()
    {
        WriteFile("docume.json", """{ "confluence": { "baseUrl": "https://x.atlassian.net/wiki" """);
        Page("README.md", "# Handbook");

        var state = Adopt();

        state.Action.ShouldBe(ScaffoldAction.Skipped);
        state.Note.ShouldNotBeNull().ShouldContain("docume.json could not be read");
        state.Note.ShouldNotContain("\n");
    }

    /// <summary>
    /// <c>wiki.exclude</c> decides what a publish publishes, so it decides what adoption lists: a
    /// skeleton holding entries no publish would ever write is a dashboard full of pages that never
    /// appear.
    /// </summary>
    [Fact]
    public void Adopt_honours_wiki_exclude()
    {
        WriteFile(
            "docume.json",
            """{"confluence":{"baseUrl":"https://x.atlassian.net/wiki","spaceKey":"SBX"},"wiki":{"exclude":["_meta/**","drafts/**"]}}""");
        Page("README.md", "# Handbook");
        Page("drafts/wip.md", "# Work in progress");

        Adopt();

        ReadState().Pages.Keys.ShouldBe(["README.md"]);
    }

    private ScaffoldResult Adopt(string? legacyMapPath = null)
        => Scaffold(adopt: true, legacyMapPath)
            .Single(r => r.RelativePath.EndsWith(ProjectScaffolder.StateFile, StringComparison.Ordinal));

    private IReadOnlyList<ScaffoldResult> Scaffold(bool adopt, string? legacyMapPath = null)
        => ProjectScaffolder.Scaffold(_dir, adopt: adopt, legacyMapPath: legacyMapPath);

    private DocumeState ReadState() => StateStore.Load(Full(StatePath));

    private void Page(string wikiRelativePath, string content)
        => WriteFile($"{WikiRoot}/{wikiRelativePath}", content);

    private void WriteFile(string relativePath, string content)
    {
        var full = Full(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Full(string relativePath)
        => System.IO.Path.Combine([_dir, .. relativePath.Split('/')]);
}
