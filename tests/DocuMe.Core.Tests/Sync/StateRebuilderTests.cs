using System.Net;
using DocuMe.Core.Confluence;
using DocuMe.Core.State;
using DocuMe.Core.Sync;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Sync;

/// <summary>
/// The <c>--rebuild-state</c> walk (PLAN.md §6.3, docs/specs/2026-08-19-state-rebuild.md) against a
/// real local HTTP server (.claude/rules/testing.md §4.2), the same shape as
/// <see cref="Publishing.PruneExecutorTests"/>: the client is real, the space is WireMock, and the
/// filesystem is an injected predicate so no wiki tree exists on disk.
/// </summary>
public sealed class StateRebuilderTests
{
    private const string Email = "bot@example.com";
    private const string ApiToken = "very-secret-token";
    private const string SpaceId = "98304";

    private static readonly ConfluenceCredentials Credentials = new(Email, ApiToken);

    /// <summary>
    /// The happy path: a stamped page whose file exists and whose path state has never seen becomes an
    /// entry with the page's id, its title, the marked flag, and deliberately nothing else. No content
    /// hash in particular, so the next publish sees the page as changed and re-records it honestly.
    /// </summary>
    [Fact]
    public async Task Adopts_a_stamped_page_state_never_saw_and_records_no_hash()
    {
        using var server = WireMockServer.Start();
        StubSpacePages(server, ("10", "Lifecycle"));
        StubMarker(server, "10", "10-concepts/lifecycle.md");

        var report = await RebuildAsync(server, State(), Files("10-concepts/lifecycle.md"));

        var entry = report.Entries.ShouldHaveSingleItem();
        entry.Disposition.ShouldBe(RebuildDisposition.Adopted);
        entry.Path.ShouldBe("10-concepts/lifecycle.md");
        entry.PageId.ShouldBe("10");
        entry.Title.ShouldBe("Lifecycle");

        report.StateChanged.ShouldBeTrue();

        var adopted = report.State.Pages["10-concepts/lifecycle.md"];
        adopted.PageId.ShouldBe("10");
        adopted.Title.ShouldBe("Lifecycle");
        adopted.Marked.ShouldBeTrue();
        adopted.ContentHash.ShouldBeNull();
        adopted.Approval.ShouldBeNull();
        adopted.PublishedVersion.ShouldBe(0);

        // One space listing for the whole walk, then one property read per page.
        server.LogEntries
            .Count(logged => string.Equals(
                logged.RequestMessage!.Path,
                $"/wiki/api/v2/spaces/{SpaceId}/pages",
                StringComparison.Ordinal))
            .ShouldBe(1);
    }

    /// <summary>
    /// A page state already maps to its stamped path is confirmed, not rewritten: the entry says
    /// AlreadyTracked and the state object is exactly the one passed in.
    /// </summary>
    [Fact]
    public async Task A_page_state_already_tracks_changes_nothing()
    {
        using var server = WireMockServer.Start();
        StubSpacePages(server, ("10", "Lifecycle"));
        StubMarker(server, "10", "a.md");

        var state = State(("a.md", "10"));
        var report = await RebuildAsync(server, state, Files("a.md"));

        var entry = report.Entries.ShouldHaveSingleItem();
        entry.Disposition.ShouldBe(RebuildDisposition.AlreadyTracked);
        entry.Note.ShouldBeNull();

        report.StateChanged.ShouldBeFalse();
        report.State.ShouldBeSameAs(state);
    }

    /// <summary>
    /// State mapping the stamped path to a different page id is the disagreement only a human can
    /// settle: reported with both ids in reach, and nothing written on either record's say-so.
    /// </summary>
    [Fact]
    public async Task A_path_state_maps_to_a_different_id_is_conflicted_and_nothing_is_written()
    {
        using var server = WireMockServer.Start();
        StubSpacePages(server, ("20", "Lifecycle"));
        StubMarker(server, "20", "a.md");

        var state = State(("a.md", "10"));
        var report = await RebuildAsync(server, state, Files("a.md"));

        var entry = report.Entries.ShouldHaveSingleItem();
        entry.Disposition.ShouldBe(RebuildDisposition.Conflicted);
        entry.PageId.ShouldBe("20");
        entry.Note.ShouldNotBeNull();
        entry.Note.ShouldContain("10");

        report.StateChanged.ShouldBeFalse();
        report.State.Pages["a.md"].PageId.ShouldBe("10");
    }

    /// <summary>
    /// A stamped path naming no file in this repo is listed and left out of state: safe by omission,
    /// because a prune cannot touch what state does not hold. It is also the check that keeps another
    /// repo's pages out when two repos share a space.
    /// </summary>
    [Fact]
    public async Task A_stamped_path_with_no_file_is_listed_and_not_adopted()
    {
        using var server = WireMockServer.Start();
        StubSpacePages(server, ("10", "Someone Else's Page"));
        StubMarker(server, "10", "their/page.md");

        var report = await RebuildAsync(server, State(), Files());

        var entry = report.Entries.ShouldHaveSingleItem();
        entry.Disposition.ShouldBe(RebuildDisposition.PathMissing);
        entry.Note.ShouldNotBeNull();

        report.StateChanged.ShouldBeFalse();
        report.State.Pages.ShouldBeEmpty();
    }

    /// <summary>
    /// Two stamped pages claiming the same path both land as conflicts, each naming the other, and
    /// neither adopts. Picking one would be guessing which page a copied or trash-restored duplicate
    /// is, and the wrong guess hands a future prune the wrong page.
    /// </summary>
    [Fact]
    public async Task Two_pages_claiming_one_path_are_both_conflicted_and_neither_adopts()
    {
        using var server = WireMockServer.Start();
        StubSpacePages(server, ("10", "Original"), ("20", "The Copy"));
        StubMarker(server, "10", "a.md");
        StubMarker(server, "20", "a.md");

        var report = await RebuildAsync(server, State(), Files("a.md"));

        report.Entries.Count.ShouldBe(2);
        report.Entries.ShouldAllBe(entry => entry.Disposition == RebuildDisposition.Conflicted);
        report.Entries.Select(entry => entry.PageId).ShouldBe(["10", "20"]);

        // Each note names the other claimant, so the manifest alone says what disagrees.
        var original = report.Entries[0];
        var copy = report.Entries[1];

        original.Note.ShouldNotBeNull();
        original.Note.ShouldContain("20");
        copy.Note.ShouldNotBeNull();
        copy.Note.ShouldContain("10");

        report.StateChanged.ShouldBeFalse();
        report.State.Pages.ShouldBeEmpty();
    }

    /// <summary>
    /// A page that vanishes between the listing and its property read answers 404 there, and the walk
    /// skips it with a count rather than failing: a page that no longer exists has nothing to adopt,
    /// and one deletion mid-walk says nothing about the rest of the space.
    /// </summary>
    [Fact]
    public async Task A_page_that_vanishes_mid_walk_is_skipped_and_counted()
    {
        using var server = WireMockServer.Start();
        StubSpacePages(server, ("10", "Lifecycle"), ("20", "Racing"));
        StubMarker(server, "10", "a.md");
        server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages/20/properties").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound).WithBody("{}"));

        var report = await RebuildAsync(server, State(), Files("a.md"));

        report.SkippedVanishedCount.ShouldBe(1);
        report.Entries.ShouldHaveSingleItem().Path.ShouldBe("a.md");
    }

    /// <summary>
    /// Foreign pages are counted, never listed: no marker at all, somebody else's property under the
    /// same key, and a marker without a path all read as "none of this repo's business". A shared
    /// space can hold thousands of them, and a manifest that listed each one would bury the verdicts.
    /// </summary>
    [Fact]
    public async Task Unstamped_and_foreign_pages_are_counted_and_never_listed()
    {
        using var server = WireMockServer.Start();
        StubSpacePages(server, ("10", "No Property"), ("20", "Hand Made"), ("30", "Flag Only"));
        StubProperty(server, "10", """{ "results": [], "_links": {} }""");
        StubProperty(
            server,
            "20",
            """{ "results": [{ "id": "p2", "key": "docume", "value": "hand made" }], "_links": {} }""");
        StubProperty(
            server,
            "30",
            """{ "results": [{ "id": "p3", "key": "docume", "value": { "managed": true } }], "_links": {} }""");

        var state = State();
        var report = await RebuildAsync(server, state, Files());

        report.UnstampedCount.ShouldBe(3);
        report.Entries.ShouldBeEmpty();

        // The vacuity half of the same fact: a run that adopted nothing must say so, or the CLI
        // rewrites an identical state file and opens an empty PR every cron run.
        report.StateChanged.ShouldBeFalse();
        report.State.ShouldBeSameAs(state);
    }

    /// <summary>
    /// The manifest is ordinal by path whatever order Confluence answered in, so two runs over the
    /// same space print the same report and a human can diff them.
    /// </summary>
    [Fact]
    public async Task Entries_are_ordered_by_path_not_by_answer_order()
    {
        using var server = WireMockServer.Start();
        StubSpacePages(server, ("10", "Zeta"), ("20", "Alpha"), ("30", "Middle"));
        StubMarker(server, "10", "z.md");
        StubMarker(server, "20", "a.md");
        StubMarker(server, "30", "m/README.md");

        var report = await RebuildAsync(server, State(), Files("z.md", "a.md", "m/README.md"));

        report.Entries.Select(entry => entry.Path).ShouldBe(["a.md", "m/README.md", "z.md"]);
        report.Entries.ShouldAllBe(entry => entry.Disposition == RebuildDisposition.Adopted);
        report.State.Pages.Count.ShouldBe(3);
    }

    /// <summary>
    /// An entry that exists but names no page id is a gap, not a claim (§6.3: adoption fills gaps and
    /// never overwrites one), so the stamped page adopts into it and whatever the entry already held
    /// survives alongside the id.
    /// </summary>
    [Fact]
    public async Task An_entry_with_no_page_id_is_a_gap_the_page_adopts_into()
    {
        using var server = WireMockServer.Start();
        StubSpacePages(server, ("10", "Lifecycle"));
        StubMarker(server, "10", "a.md");

        var report = await RebuildAsync(server, State(("a.md", null)), Files("a.md"));

        report.Entries.ShouldHaveSingleItem().Disposition.ShouldBe(RebuildDisposition.Adopted);
        report.StateChanged.ShouldBeTrue();
        report.State.Pages["a.md"].PageId.ShouldBe("10");
        report.State.Pages["a.md"].Marked.ShouldBeTrue();
    }

    /// <summary>
    /// <see cref="StateRebuilder.WikiFilePaths"/> spells every markdown file the way state.json keys
    /// it: wiki-root-relative, forward slashes at every depth, and markdown only. The spelling is the
    /// adoption contract, because a marker path adopts only when it is byte for byte a member of this
    /// set, so a diagram or a stray text file must not be in it either: a marker naming one would
    /// otherwise adopt a page for a file no publish will ever push.
    /// </summary>
    [Fact]
    public void WikiFilePaths_spells_nested_markdown_wiki_relative_with_forward_slashes()
    {
        var root = Directory.CreateTempSubdirectory("docume-wiki-file-paths");
        try
        {
            WriteFile(root.FullName, "a.md");
            WriteFile(root.FullName, "sub", "x.md");
            WriteFile(root.FullName, "sub", "deeper", "y.md");
            WriteFile(root.FullName, "diagram.svg");
            WriteFile(root.FullName, "sub", "notes.txt");

            var paths = StateRebuilder.WikiFilePaths(root.FullName);

            paths.ShouldBe(["a.md", "sub/x.md", "sub/deeper/y.md"], ignoreOrder: true);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The traversal defense works by construction, not by inspection: a stamped path counts only when
    /// it is byte for byte a path the enumeration produced, so every alias spelling misses the set
    /// without any of them touching the disk. The fixture makes the misses mean something.
    /// <c>../escape.md</c> names a real file one directory above the wiki root, and <c>A.md</c> is a
    /// real file inside it spelled in the wrong case: on a case-insensitive filesystem both would pass
    /// a <c>File.Exists</c> check, and the ordinal set refuses them anyway, which is why the guarantee
    /// holds on every host. The embedded NUL is the other half of "never touches the disk": a set
    /// lookup answers false for a string no filesystem accepts, rather than throwing.
    /// </summary>
    [Fact]
    public void WikiFilePaths_misses_every_spelling_the_enumeration_did_not_produce()
    {
        var outer = Directory.CreateTempSubdirectory("docume-wiki-escape");
        try
        {
            var root = Path.Combine(outer.FullName, "wiki");
            WriteFile(root, "A.md");
            WriteFile(root, "sub", "x.md");
            WriteFile(outer.FullName, "escape.md");

            var paths = StateRebuilder.WikiFilePaths(root);

            paths.Contains("A.md").ShouldBeTrue();
            paths.Contains("sub/x.md").ShouldBeTrue();

            // Aliases of files the set does hold, spelled any way but the tree walk's way.
            paths.Contains("./A.md").ShouldBeFalse();
            paths.Contains("sub/../sub/x.md").ShouldBeFalse();
            paths.Contains(Path.Combine(root, "A.md")).ShouldBeFalse();

            // The wrong case of a real file, refused everywhere because the set is ordinal.
            paths.Contains("a.md").ShouldBeFalse();

            // A path that escapes the root names a real file here, and still never adopts.
            paths.Contains("../escape.md").ShouldBeFalse();

            // "No" as an answer, never an exception.
            paths.Contains("A\0.md").ShouldBeFalse();
        }
        finally
        {
            outer.Delete(recursive: true);
        }
    }

    /// <summary>Creates a stub file at the joined path, parent directories included.</summary>
    private static void WriteFile(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# stub\n");
    }

    /// <summary>One page of space results, shaped like every other v2 listing this suite stubs.</summary>
    private static void StubSpacePages(WireMockServer server, params (string Id, string Title)[] pages)
    {
        var results = string.Join(",", pages.Select(page =>
            $$"""
            { "id": "{{page.Id}}", "status": "current", "title": "{{page.Title}}",
              "spaceId": "{{SpaceId}}", "version": { "number": 1 } }
            """));

        server
            .Given(Request.Create().WithPath($"/wiki/api/v2/spaces/{SpaceId}/pages").UsingGet())
            .RespondWith(Json($$"""{ "results": [{{results}}], "_links": {} }"""));
    }

    /// <summary>Answers one page's property read with the marker DocuMe stamps, owning <paramref name="path"/>.</summary>
    private static void StubMarker(WireMockServer server, string pageId, string path)
    {
        var body = $$"""
            { "results": [{ "id": "prop-{{pageId}}", "key": "docume",
                "value": { "managed": true, "path": "{{path}}" }, "version": { "number": 1 } }],
              "_links": {} }
            """;

        StubProperty(server, pageId, body);
    }

    /// <summary>Answers one page's managed-marker read with <paramref name="body"/>.</summary>
    private static void StubProperty(WireMockServer server, string pageId, string body) =>
        server
            .Given(Request.Create().WithPath($"/wiki/api/v2/pages/{pageId}/properties").UsingGet())
            .RespondWith(Json(body));

    private static IResponseBuilder Json(string body) =>
        Response.Create()
            .WithStatusCode(HttpStatusCode.OK)
            .WithHeader("Content-Type", "application/json")
            .WithBody(body);

    /// <summary>The injected filesystem: exactly these wiki-relative paths exist.</summary>
    private static Func<string, bool> Files(params string[] paths)
    {
        var existing = new HashSet<string>(paths, StringComparer.Ordinal);

        return existing.Contains;
    }

    private static DocumeState State(params (string Path, string? PageId)[] pages) =>
        new()
        {
            Pages = pages.ToDictionary(
                page => page.Path,
                page => new PageState { PageId = page.PageId },
                StringComparer.Ordinal),
        };

    private static async Task<RebuildReport> RebuildAsync(
        WireMockServer server,
        DocumeState state,
        Func<string, bool> fileExists)
    {
        var options = new ConfluenceClientOptions
        {
            BaseUrl = new Uri($"{server.Url}/wiki"),
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.FromMilliseconds(1),
            Timeout = TimeSpan.FromSeconds(30),
        };

        using var client = ConfluenceClient.Create(options, Credentials);

        return await new StateRebuilder(client).RebuildAsync(
            SpaceId,
            state,
            fileExists,
            TestContext.Current.CancellationToken);
    }
}
