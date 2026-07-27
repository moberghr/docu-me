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
/// <strong>What bounds the population these checks sweep.</strong> Two literals used to, and neither was
/// asserted: <see cref="PageReads"/>, which <c>nameof</c> protects against a rename but not against an
/// omission, and one spelling of the client's body request. Both were measured open — a page read named
/// outside the <c>FindPageBy</c> family, and a body request spelled positionally, each passed the whole
/// suite. <see cref="Every_public_method_that_answers_with_a_page_is_named_by_this_class"/> and the
/// by-value scan behind
/// <see cref="Every_body_the_client_asks_for_by_default_is_a_comment_collection"/> close them, so a new
/// page read now has to be declared here before anything in this class can be green about it.
/// </para>
/// <para>
/// Three of the checks here read source text, which the suite otherwise avoids — the house habit is
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

    /// <summary>
    /// The helper every body request in the client goes through. Counted by the VALUE of its first
    /// argument rather than by one spelling of it — see
    /// <see cref="Every_body_the_client_asks_for_by_default_is_a_comment_collection"/>.
    /// </summary>
    private const string Helper = "BodyFormat(";

    /// <summary>
    /// The client file itself, excluded from the call-site scan because it <em>declares</em> the parameter.
    /// Its own uses are counted separately by
    /// <see cref="Every_body_the_client_asks_for_by_default_is_a_comment_collection"/>.
    /// </summary>
    private const string ClientFile = "ConfluenceClient.cs";

    /// <summary>The two page reads, whose default decides what a caller that says nothing gets.</summary>
    private static readonly string[] PageReads =
        [nameof(ConfluenceClient.FindPageByTitleAsync), nameof(ConfluenceClient.FindPageByIdAsync)];

    /// <summary>
    /// The two methods that answer with a page without reading one: the upsert's create and update
    /// halves, which echo back what they just wrote. Declared so that
    /// <see cref="Every_public_method_that_answers_with_a_page_is_named_by_this_class"/> can tell a
    /// new read from a new write instead of guessing from a name.
    /// </summary>
    private static readonly string[] PageWrites =
        [nameof(ConfluenceClient.CreatePageAsync), nameof(ConfluenceClient.UpdatePageAsync)];

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

    /// <summary>
    /// The same rule as <see cref="Only_the_dashboard_asks_Confluence_for_a_page_body"/>, counted by the
    /// argument's <em>value</em> instead of the parameter's <em>name</em>.
    /// </summary>
    /// <remarks>
    /// Naming an optional argument is a style choice, not a requirement, and
    /// <c>dotnet_diagnostic.MA0003.severity = none</c> in <c>.editorconfig</c> leaves it one — so
    /// <c>FindPageByIdAsync(pageId!, true, cancellationToken)</c> compiles, reads a body on the publish
    /// path, and never writes the token the scan above counts. Measured: that edit passed the whole suite,
    /// all five checks of this class included. A guard against <em>adding</em> an opt-in says nothing about
    /// the spelling, so this one reads every invocation's arguments and accepts neither spelling outside
    /// the dashboard.
    /// </remarks>
    [Fact]
    public void A_body_opt_in_passed_by_position_is_counted_the_same_as_a_named_one()
    {
        var calls = PageReadCalls();

        const string vacuous = "The invocation scan found no page read in src/ at all, so it is proving "
            + "nothing. Every read goes through one of ConfluenceClient's two methods; if a name changed, "
            + "follow it here rather than deleting the check.";

        const string nested = "A page read now passes a call as an argument. This scan reads an argument "
            + "list up to its first ')', so a nested one truncates it and the check silently weakens. "
            + "Teach it to balance parentheses before trusting it again.";

        const string opted = "A page read outside the dashboard asks Confluence for the page body. Rule "
            + "§9.1 allows exactly one — the dashboard's compare-and-skip, whose value reaches a "
            + "string.Equals and nothing else. Note that this fires on a bare `true` as well as on "
            + "`includeBody: true`: the positional form leaves "
            + "Only_the_dashboard_asks_Confluence_for_a_page_body green, because that check counts the "
            + "token rather than the value.";

        calls.ShouldNotBeEmpty(vacuous);
        calls.ShouldAllBe(call => !call.Arguments.Contains('(', StringComparison.Ordinal), nested);

        var optIns = calls
            .Where(call => Arguments(call.Arguments).Any(IsBodyOptIn))
            .ToList();

        optIns.ShouldAllBe(call => string.Equals(call.File, DashboardFile, StringComparison.Ordinal), opted);
    }

    /// <summary>
    /// The two bodies the client fetches without asking anyone are both comment collections, and no
    /// third one exists under any spelling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Counted by value, not by spelling, and that is the whole point of this check.</strong>
    /// It used to match the literal <c>BodyFormat(includeBody: true</c>. Measured: a client method that
    /// fetches a page body and <em>returns it to its caller</em> — rule §9.1's forbidden feature, stated
    /// plainly — passed all 1,435 tests when its request was spelled <c>BodyFormat(true</c>, and was
    /// caught the moment the same method spelled it <c>BodyFormat(includeBody: true</c>. One optional
    /// argument name apart, and <c>dotnet_diagnostic.MA0003.severity = none</c> leaves the choice free,
    /// exactly as it does for the call-site scan above.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_body_the_client_asks_for_by_default_is_a_comment_collection()
    {
        var lines = File.ReadAllLines(ClientPath());
        var hardcoded = HardcodedBodyReads(lines);

        const string nested = "A BodyFormat call now passes a call as an argument. This scan reads an "
            + "argument list up to its first ')', so a nested one truncates it and the check silently "
            + "weakens. Teach it to balance parentheses before trusting it again.";

        const string page = "The client asks for a body on an endpoint that is not a comment collection. "
            + "Comment text is ingested by design (§6.3) and quoted as untrusted (rule §1.3); a page body "
            + "fetched unconditionally is rule §9.1's forbidden feature with no call site to review.";

        const string vacuous = "The client's two hardcoded body reads are no longer two. Both are comment "
            + "collections and both are meant to stay; a scan that finds neither is measuring nothing, "
            + "and one that finds a third has been told about it by the check above.";

        hardcoded.ShouldAllBe(read => !read.Arguments.Contains('(', StringComparison.Ordinal), nested);
        hardcoded.ShouldAllBe(read => Collection(lines, read.Index), page);

        // Asserted second on purpose: a real third body read trips the endpoint check above, whose
        // message names the cause. Reaching this line means the scan itself stopped seeing the code.
        hardcoded.Count.ShouldBe(2, vacuous);
    }

    /// <summary>
    /// Rule §9.1 is about what comes <em>back</em> from Confluence, so the population this class scans
    /// has to be every method that can answer with a page — not the two it happens to name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PageReads"/> is bound by <c>nameof</c>, so a rename cannot slip past it, and
    /// <see cref="The_scans_these_checks_rest_on_found_the_code_they_read"/> pins its length. Neither
    /// says the array is <em>complete</em>. Measured: a third page read named outside the
    /// <c>FindPageBy</c> family, opted into a body positionally at the publish call site, left every
    /// check in this class green — the call-site scans match two names and nothing else, and the
    /// default check reflects over the same two. What caught it was
    /// <see cref="Publishing.PublishExecutorTests.The_version_read_does_not_ask_confluence_for_the_page_body"/>,
    /// a per-surface test on the one call site that happens to have one. This fact removes the luck:
    /// a new page-returning method has to be declared before the scans can be green about it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_public_method_that_answers_with_a_page_is_named_by_this_class()
    {
        var answering = typeof(ConfluenceClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => AnswersWithAPage(method.ReturnType))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        const string added = "ConfluenceClient answers with a page from a method that neither PageReads "
            + "nor PageWrites names, so nothing in this class can see it: both call-site scans match the "
            + "two names in PageReads, and the default check reflects over the same two. Decide which it "
            + "is and declare it. A read goes in PageReads, which subjects it to the body-default check "
            + "and both scans; a write goes in PageWrites. Rule §9.1 is about what comes back from "
            + "Confluence, not about which verb the request used.";

        var declared = PageReads.Concat(PageWrites).Order(StringComparer.Ordinal);

        // Joined rather than compared as sequences: Shouldly resolves a two-list ShouldBe over strings
        // to its Case-sensitivity overload, and the joined form reads better in the failure anyway.
        string.Join(", ", answering).ShouldBe(string.Join(", ", declared), added);
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

        File.ReadAllText(ClientPath()).ShouldContain(Helper);
        PageReads.Length.ShouldBe(2);
    }

    /// <summary>
    /// Every page-read invocation in <c>src/</c> with its argument list, the client's own declarations
    /// excluded. Matched on the method name followed immediately by <c>(</c>, so the
    /// <c>&lt;see cref="..."/&gt;</c> references in <c>ConfluenceModels.cs</c> are not mistaken for calls.
    /// </summary>
    private static List<(string File, string Arguments)> PageReadCalls()
    {
        var calls = new List<(string File, string Arguments)>();

        foreach (var file in Sources())
        {
            if (string.Equals(Path.GetFileName(file), ClientFile, StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            foreach (var read in PageReads)
            {
                var token = $"{read}(";
                var index = text.IndexOf(token, StringComparison.Ordinal);

                while (index >= 0)
                {
                    var open = index + token.Length;
                    var close = text.IndexOf(')', open);

                    if (close > open)
                    {
                        calls.Add((Path.GetFileName(file), text[open..close]));
                    }

                    index = text.IndexOf(token, open, StringComparison.Ordinal);
                }
            }
        }

        return calls;
    }

    /// <summary>
    /// Every <c>BodyFormat</c> call in the client whose body decision is hardcoded rather than
    /// forwarded, by either spelling of it, with the argument list it was read from.
    /// </summary>
    private static List<(int Index, string Arguments)> HardcodedBodyReads(string[] lines)
    {
        var hardcoded = new List<(int Index, string Arguments)>();

        for (var index = 0; index < lines.Length; index++)
        {
            var at = lines[index].IndexOf(Helper, StringComparison.Ordinal);

            if (at < 0)
            {
                continue;
            }

            var open = at + Helper.Length;
            var close = lines[index].IndexOf(')', open);

            if (close <= open)
            {
                continue;
            }

            // The first argument is the body decision; `bool includeBody` on the declaration and a
            // forwarded `includeBody` at a call site are both something other than an opt-in.
            var arguments = lines[index][open..close];

            if (Arguments(arguments).Take(1).Any(IsBodyOptIn))
            {
                hardcoded.Add((index, arguments));
            }
        }

        return hardcoded;
    }

    /// <summary>
    /// Whether the endpoint a body request completes is a comment collection. The suffix is usually
    /// built one line below the endpoint it is concatenated onto, and sometimes on the same line.
    /// </summary>
    private static bool Collection(string[] lines, int index) =>
        lines[index].Contains("CommentsSegment}", StringComparison.Ordinal)
        || (index > 0 && lines[index - 1].Contains("CommentsSegment}", StringComparison.Ordinal));

    /// <summary>Whether a method hands its caller a page, whatever it had to do to get one.</summary>
    private static bool AnswersWithAPage(Type returnType) =>
        returnType.IsGenericType
        && returnType.GetGenericTypeDefinition() == typeof(Task<>)
        && returnType.GetGenericArguments()[0] == typeof(ConfluencePage);

    /// <summary>An argument list split into its arguments, whitespace removed so spelling cannot hide.</summary>
    private static IEnumerable<string> Arguments(string arguments) => arguments
        .Split(',')
        .Select(argument => argument.Replace(" ", string.Empty, StringComparison.Ordinal));

    /// <summary>Both ways an argument can say "and bring the body": by name, and by position.</summary>
    private static bool IsBodyOptIn(string argument) =>
        string.Equals(argument, "true", StringComparison.Ordinal)
        || string.Equals(argument, $"{Parameter}:true", StringComparison.Ordinal);

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
