using System.Reflection;
using DocuMe.Core.Confluence;
using DocuMe.Core.Scaffolding;
using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Confluence;

/// <summary>
/// Rule §9.1's safety half — "never build features that read Confluence page bodies back as a content
/// source" — counted over the tree rather than argued from prose.
/// </summary>
/// <remarks>
/// <para>
/// The rule was the one invariant in <c>.claude/rules/project-specific.md</c> with nothing mechanical
/// behind it. Every other §9.x line has a test that fails when it stops holding; this one held because
/// nobody had written the feature it forbids, which is a different thing from being enforced. What it
/// forbids is cheap to add by accident: "publish should preserve hand edits", "drift should hash what
/// Confluence actually stores", "adopt should seed titles from the space" all arrive as one extra
/// <c>includeBody: true</c> and would have landed on a green build.
/// </para>
/// <para>
/// <strong>The count, as of this commit.</strong> Four call sites in <c>src/</c> read a page
/// (<see cref="DocuMe.Core.Publishing.PublishExecutor"/> twice, <see cref="DocuMe.Core.Sync.LabelReader"/>
/// once, <see cref="DocuMe.Core.Dashboard.DashboardPublisher"/> once) and exactly one asks for the body. That
/// one is compare-then-discard: §6.5 refreshes the dashboard on every <c>drift --mark</c>, so it reads
/// its own last render to skip a no-op version, and the value reaches a single <c>string.Equals</c>. The
/// two body reads the client hardcodes are comment collections, which §6.3 ingests by design and rule
/// §1.3 treats as untrusted. <c>init --adopt</c> takes no client at all.
/// </para>
/// <para>
/// Three of the four checks here read source text, which the suite otherwise avoids — the house habit is
/// to execute the boundary and assert the verdict beside the wording. It does not transfer: the property
/// is <em>an absence</em>, and no run of a program that does not read bodies can demonstrate that no
/// other program reads them. What is executable is the other half, and it is executed — see
/// <see cref="Dashboard.DashboardPublisherTests.The_write_carries_the_render_it_was_given_not_the_body_it_read"/>,
/// which poisons the stored body and proves none of it reaches the write.
/// </para>
/// </remarks>
public sealed class RemoteBodyReadTests
{
    /// <summary>The one file allowed to ask for a page body, and the only spelling it may use.</summary>
    private const string DashboardFile = "DashboardPublisher.cs";

    /// <summary>The parameter every page read carries, and the token the call-site scan counts.</summary>
    private const string Parameter = "includeBody";

    /// <summary>The client's own body-requesting spelling: hardcoded, and only for comments.</summary>
    private const string Hardcoded = "BodyFormat(includeBody: true";

    /// <summary>
    /// The client file itself, excluded from the call-site scan because it <em>declares</em> the parameter.
    /// Its own uses are counted separately by
    /// <see cref="Every_body_the_client_asks_for_by_default_is_a_comment_collection"/>.
    /// </summary>
    private const string ClientFile = "ConfluenceClient.cs";

    /// <summary>The two page reads, whose default decides what a caller that says nothing gets.</summary>
    private static readonly string[] PageReads =
        [nameof(ConfluenceClient.FindPageByTitleAsync), nameof(ConfluenceClient.FindPageByIdAsync)];

    [Fact]
    public void Only_the_dashboard_asks_Confluence_for_a_page_body()
    {
        var optIns = CallSites();

        const string added = "A second call site asks Confluence for a page body. Rule §9.1 allows one: "
            + "the dashboard's compare-and-skip, whose value reaches a string.Equals and nothing else. "
            + "If the new one feeds a file, a page write or a state field, the repo has stopped being the "
            + "source of truth. If it is another comparison, add it here and say what it compares.";

        optIns.ShouldHaveSingleItem(added);
        Path.GetFileName(optIns[0]).ShouldBe(DashboardFile, added);
    }

    [Fact]
    public void A_caller_that_asks_for_no_body_is_answered_without_one()
    {
        // The scan above counts who opts in, which is only a guard while opting out stays the default.
        // A default flipped to true would give every existing call site a body it never asked for and
        // leave the scan reporting one opt-in, exactly as it does now.
        foreach (var read in PageReads)
        {
            var method = typeof(ConfluenceClient).GetMethod(read, BindingFlags.Public | BindingFlags.Instance);
            method.ShouldNotBeNull($"ConfluenceClient no longer declares {read}.");

            var parameter = method!.GetParameters()
                .SingleOrDefault(candidate => string.Equals(candidate.Name, Parameter, StringComparison.Ordinal));
            parameter.ShouldNotBeNull($"{read} no longer takes an {Parameter} parameter.");

            parameter!.HasDefaultValue.ShouldBeTrue($"{read}'s {Parameter} has no default, so a caller must choose.");

            var flipped = $"{read} defaults to fetching the body, so every caller reads one whether it "
                + "wants it or not — including the publish and label paths, which need version and labels only.";

            parameter.DefaultValue.ShouldBe(false, flipped);
        }
    }

    [Fact]
    public void Every_body_the_client_asks_for_by_default_is_a_comment_collection()
    {
        var lines = File.ReadAllLines(ClientPath());
        var hardcoded = new List<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(Hardcoded, StringComparison.Ordinal))
            {
                continue;
            }

            // The endpoint is built one line above the format suffix that completes it, so that line
            // names the collection being read: a comments segment, or a page id.
            hardcoded.Add(index > 0 ? lines[index - 1] : lines[index]);
        }

        const string page = "The client asks for a body on an endpoint that is not a comment collection. "
            + "Comment text is ingested by design (§6.3) and quoted as untrusted (rule §1.3); a page body "
            + "fetched unconditionally is rule §9.1's forbidden feature with no call site to review.";

        hardcoded.Count.ShouldBe(2, page);
        hardcoded.ShouldAllBe(endpoint => endpoint.Contains("CommentsSegment}", StringComparison.Ordinal), page);
    }

    [Fact]
    public void Adoption_seeds_ids_from_the_repo_and_is_handed_no_client()
    {
        // The third route a body could enter by, and the only one closed by construction rather than by a
        // choice at a call site: --adopt is offline. Seeding ids from the space would read titles, not
        // bodies, so §9.1 would not forbid it outright — but it would put the adopter one field away from
        // "and while we are here, take the body too". This is the tripwire that forces that read.
        var surface = typeof(WikiAdopter)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(typeof(AdoptionRequest).GetProperties().Select(property => property.PropertyType))
            .ToList();

        const string wired = "WikiAdopter now takes a Confluence client, so `init --adopt` can reach a "
            + "remote page. Re-check rule §9.1 against what it reads: ids and titles are fine, a body is "
            + "not — then widen this test to whatever the new surface is.";

        surface.ShouldNotBeEmpty();
        surface.ShouldAllBe(type => type != typeof(ConfluenceClient), wired);
    }

    [Fact]
    public void The_scans_these_checks_rest_on_found_the_code_they_read()
    {
        // Both scans pass vacuously on a tree they cannot see: a moved src/ makes the call-site scan
        // report zero opt-ins, and a renamed helper makes the client scan report zero hardcoded reads.
        Sources().Count().ShouldBeGreaterThan(50, "The src/ scan is reading a far smaller tree than the product.");

        var readers = Sources()
            .Where(file => File.ReadAllText(file).Contains("FindPageBy", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        readers.ShouldContain(DashboardFile);
        readers.ShouldContain(ClientFile);

        var moved = "A page read was added or removed. Confirm the new one asks for no body, then update "
            + $"this count: {string.Join(", ", readers.Order(StringComparer.Ordinal))}.";

        readers.Count.ShouldBe(5, moved);

        File.ReadAllText(ClientPath()).ShouldContain(Hardcoded);
        PageReads.Length.ShouldBe(2);
    }

    /// <summary>
    /// Every <c>src/</c> file that opts a page read into a body, the client's own declaration excluded.
    /// </summary>
    private static List<string> CallSites() => Sources()
        .Where(file => !string.Equals(Path.GetFileName(file), ClientFile, StringComparison.Ordinal))
        .Where(file => File.ReadAllText(file).Contains(Parameter, StringComparison.Ordinal))
        .ToList();

    private static string ClientPath() => Sources()
        .Single(file => string.Equals(Path.GetFileName(file), ClientFile, StringComparison.Ordinal));

    /// <summary>Every committed C# source under <c>src/</c>, build output excluded.</summary>
    private static IEnumerable<string> Sources() => Directory
        .EnumerateFiles(Path.Combine(DocumeCli.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
