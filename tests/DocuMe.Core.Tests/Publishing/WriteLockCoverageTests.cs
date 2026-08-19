using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// The structural half of CLAUDE.md §0.1 and rule §1.4 — the production write lock — asserted over the
/// tree rather than over a list of surfaces somebody remembered to extend.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PublishGuardTests"/> covers the guard's logic and four per-surface tests run the commands
/// that call it (<c>publish</c>, <c>dashboard</c>, <c>sync</c>, <c>drift --mark</c>). Together they prove
/// the lock holds for the write paths that exist today, and nothing more: the guard is enforced by
/// <em>where the call sits</em>, so an eleventh write method on the client, or a fifth command reaching
/// one, is covered only if whoever adds it remembers to add a test as well. That gap was traced by hand
/// and recorded as accepted residual risk; this class is what closes it.
/// </para>
/// <para>
/// The shape is the one <see cref="Confluence.RemoteBodyReadTests"/> uses for rule §9.1, and it is the
/// same argument: the property is a statement about the whole tree, so no run of the four commands that
/// do check the guard can demonstrate that a fifth one does. Five sets are derived from source and
/// compared against pinned lists — the write methods the client ships, the files that reach one, the
/// surfaces that hold the guard, the files that issue an HTTP write verb at all, and the spellings that
/// would issue one invisibly. Each comparison fails in both directions on purpose. Adding a write and
/// listing it here is one edit; adding a write and not listing it is a red suite naming the method.
/// </para>
/// <para>
/// <strong>The last two were added at iter183, after auditing this class's own population rather than
/// the tree it guards.</strong> The first paragraph promises that an eleventh write on the client fails
/// the suite. It did not, for two spellings, because the population was defined twice over by a literal
/// nothing asserted: the verbs were <c>Post</c>/<c>Put</c>/<c>Delete</c> and the file was
/// <c>ConfluenceClient.cs</c>. Measured, one mutation each, full suite per cell: an eleventh public write
/// method spelled <c>HttpMethod.Patch</c> left the suite <em>green at 1,433</em>, and so did an
/// <c>HttpMethod.Post</c> spelling planted in <c>DashboardPublisher.cs</c>. A third cell respelled an
/// existing write as <c>new HttpMethod("DELETE")</c> and was caught by exactly one test — fact 1's
/// vacuous-pass floor, whose message says the scan stopped reading the client, which is true but names
/// the instrument rather than the cause.
/// </para>
/// <para>
/// What this class does <em>not</em> claim: that a listed caller is correctly guarded. It pins which
/// surface each one sits behind so the mapping is reviewable, and the per-surface tests execute it.
/// Those were measured in the same pass and they hold: each of <c>dashboard</c>, <c>drift --mark</c> and
/// <c>sync --reply</c> has a test that runs the command against a protected space and asserts the run
/// reached Confluence with no request at all, and <c>publish</c> has
/// <see cref="PublishExecutorTests.Refuses_to_send_a_single_request_when_the_target_space_is_protected"/>.
/// </para>
/// </remarks>
public sealed class WriteLockCoverageTests
{
    /// <summary>The verbs that make a request a write. Everything else is a read.</summary>
    /// <remarks>
    /// <c>Patch</c> is listed though the client issues none, and that is the point: this is a claim about
    /// HTTP, not an inventory of the endpoints Confluence happens to expose today. It cost one word and
    /// closed a measured hole — see the second paragraph of this class's remarks.
    /// </remarks>
    private static readonly string[] Verbs = ["Post", "Put", "Delete", "Patch"];

    /// <summary>
    /// Spellings that issue a write without naming a verb <see cref="WriteVerbs"/> can classify. None may
    /// appear anywhere under <c>src/</c>.
    /// </summary>
    /// <remarks>
    /// The verb scan keys on the literal <c>HttpMethod.&lt;verb&gt;</c>, so a request built from a
    /// constructed method or sent through one of <c>HttpClient</c>'s verb helpers is a write no fact here
    /// can count — including <see cref="Verbs"/>, now that it knows about <c>Patch</c>. The tree holds
    /// none of these (the client funnels every request through a single <c>SendAsync</c> with a static
    /// verb), which is what makes a flat refusal affordable rather than a second parser.
    /// </remarks>
    private static readonly string[] UnscannableSpellings =
    [
        "new HttpMethod(",
        ".PostAsync(",
        ".PutAsync(",
        ".PatchAsync(",
        ".DeleteAsync(",
        ".PostAsJsonAsync(",
        ".PutAsJsonAsync(",
    ];

    /// <summary>
    /// Every <c>ConfluenceClient</c> method that issues a write verb, and the verb it issues. Ten, as
    /// traced by hand before this class existed.
    /// </summary>
    private static readonly Dictionary<string, string> WriteMethods = new(StringComparer.Ordinal)
    {
        ["AddLabelsAsync"] = "Post",
        ["CreateFooterCommentAsync"] = "Post",
        ["CreatePageAsync"] = "Post",
        ["CreatePagePropertyAsync"] = "Post",
        ["DeletePageAsync"] = "Delete",
        ["MovePageAsync"] = "Put",
        ["RemoveLabelAsync"] = "Delete",
        ["ReplyToCommentAsync"] = "Post",
        ["ResolveInlineCommentAsync"] = "Put",
        ["UpdatePageAsync"] = "Put",
        ["UploadAttachmentAsync"] = "Put",
    };

    /// <summary>
    /// Every file outside the client that invokes one of those methods, mapped to the guarded surface it
    /// sits behind. <c>DriftCommand</c> maps to itself: it is both the surface and the caller.
    /// </summary>
    /// <remarks>
    /// <c>PruneExecutor</c> is the one entry whose guard is transitive rather than adjacent — the delete
    /// is reached only when the publish it follows succeeded, and a refused publish never gets there.
    /// It is listed rather than exempted so that the reasoning has somewhere to live.
    /// </remarks>
    private static readonly Dictionary<string, string> Callers = new(StringComparer.Ordinal)
    {
        ["DashboardPublisher.cs"] = "DashboardCommand.cs",
        ["DriftCommand.cs"] = "DriftCommand.cs",
        ["FeedbackReplyExecutor.cs"] = "SyncCommand.cs",
        ["PruneExecutor.cs"] = "PublishPipeline.cs",
        ["PublishExecutor.cs"] = "PublishPipeline.cs",
    };

    /// <summary>The files that call the guard, which is what makes them surfaces.</summary>
    private static readonly string[] GuardedSurfaces =
        ["DashboardCommand.cs", "DriftCommand.cs", "PublishPipeline.cs", "SyncCommand.cs"];

    private const string ClientFile = "ConfluenceClient.cs";
    private const string GuardCall = "PublishGuard.WriteRefusal(";

    /// <summary>
    /// The client ships exactly the writes this class knows about — the assertion an eleventh one fails.
    /// </summary>
    [Fact]
    public void Every_write_the_client_ships_is_one_this_class_knows_about()
    {
        var verbs = WriteVerbs();

        // Vacuous-pass guard. A renamed client, a moved src/ or a scan that stopped matching would
        // otherwise compare two empty sets and report the lock fully covered.
        var lost = $"The scan found {verbs.Count} write verb(s) in {ClientFile}, fewer than the "
            + $"{WriteMethods.Count} methods pinned below. It has stopped reading the client rather than "
            + "found the client shrinking.";

        verbs.Count.ShouldBeGreaterThanOrEqualTo(WriteMethods.Count, lost);

        var orphans = verbs
            .Where(verb => verb.Method is null)
            .Select(verb => $"{verb.Verb} at {ClientFile}:{verb.Line}")
            .ToList();

        // A verb inside a private helper reaches Confluence just as well as one in a public method, and
        // the caller scan below keys on public names — so it would be a write no set here can see.
        const string hidden = "A write verb sits outside any public method of the client. The caller scan keys on "
            + "public method names, so a write issued from a private helper is invisible to it. Give the "
            + "verb a public entry point, or teach both scans about the helper. Orphans:";

        orphans.ShouldBeEmpty(hidden);

        var declared = verbs
            .Where(verb => verb.Method is not null)
            .GroupBy(verb => verb.Method!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(verb => verb.Verb).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        const string added = "The set of write methods on ConfluenceClient has changed. Every one of them is a "
            + "request that can reach the production AUR space, and CLAUDE.md §0.1 / rule §1.4 say none "
            + "may run before `confluence.productionAllowed`. Add it to WriteMethods, then add its caller "
            + "to Callers and confirm that caller sits behind a surface holding PublishGuard.";

        declared.Keys.Order(StringComparer.Ordinal)
            .ShouldBe(WriteMethods.Keys.Order(StringComparer.Ordinal), added);

        foreach (var (method, issued) in declared)
        {
            var changed = $"{method} issues [{string.Join(", ", issued)}], but this class has it pinned as "
                + $"{WriteMethods[method]}. A method that gained a second verb gained a second way to "
                + "write — re-read it against the lock before repinning.";

            issued.ShouldBe([WriteMethods[method]], changed);
        }
    }

    /// <summary>
    /// Every file that reaches a write is one whose guarded surface is written down — the assertion a
    /// fifth command fails.
    /// </summary>
    [Fact]
    public void Every_file_that_reaches_a_write_sits_behind_a_named_surface()
    {
        var reached = WriteCallers();

        var vacuous = "No file in src/ calls any of the client's write methods, which cannot be true "
            + $"while {ClientFile} ships {WriteMethods.Count} of them. The scan has stopped reading the "
            + "tree.";

        reached.ShouldNotBeEmpty(vacuous);

        const string added = "A file reaches a Confluence write without this class knowing which guarded surface "
            + "it sits behind. That is how the production write lock (CLAUDE.md §0.1, rule §1.4) is "
            + "enforced — by the guard being upstream of the call, not by the call itself — so a new "
            + "write path is locked only once something checks PublishGuard before it. Walk it up to its "
            + "command, confirm the refusal happens before the first write, add a per-surface test that "
            + "runs it, then add the file here. Reached:";

        reached.Keys.Order(StringComparer.Ordinal)
            .ShouldBe(Callers.Keys.Order(StringComparer.Ordinal), added);

        var unknown = Callers.Values
            .Distinct(StringComparer.Ordinal)
            .Except(GuardedSurfaces, StringComparer.Ordinal)
            .ToList();

        const string dangling = "A caller is mapped to a surface that is not in GuardedSurfaces, so the mapping "
            + "names a guard nothing asserts the existence of. Offenders:";

        unknown.ShouldBeEmpty(dangling);
    }

    /// <summary>
    /// Every surface the mapping leans on still holds the guard, and nothing else does.
    /// </summary>
    /// <remarks>
    /// The reverse direction matters as much as the forward one. A file that drops its
    /// <c>PublishGuard</c> call keeps every per-surface test's <em>name</em> while the refusal is gone,
    /// and a new file that adds one is a write path that arrived without
    /// <see cref="Every_file_that_reaches_a_write_sits_behind_a_named_surface"/> noticing — the guard
    /// being present is evidence somebody thought it was a write path.
    /// </remarks>
    [Fact]
    public void Every_guarded_surface_still_calls_the_guard()
    {
        var holding = Sources()
            .Where(file => File.ReadAllText(file).Contains(GuardCall, StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file)!)
            .Order(StringComparer.Ordinal)
            .ToList();

        const string drifted = "The set of files calling PublishGuard.WriteRefusal has changed. A surface that "
            + "dropped the call still passes its own per-surface test by name while writing into a "
            + "protected space; a file that added one is a write path this class was never told about. "
            + "Either way, reconcile Callers and GuardedSurfaces before repinning. Holding the guard:";

        holding.ShouldBe(GuardedSurfaces.Order(StringComparer.Ordinal).ToList(), drifted);
    }

    /// <summary>
    /// The client is the only file in the tree that issues an HTTP write verb — the assertion a second
    /// client fails.
    /// </summary>
    /// <remarks>
    /// <see cref="Every_write_the_client_ships_is_one_this_class_knows_about"/> reads one file <em>by
    /// name</em>, so a write issued from anywhere else is outside its population rather than caught by
    /// it: measured, an <c>HttpMethod.Post</c> spelling planted in <c>DashboardPublisher.cs</c> left the
    /// whole suite green. The lock is a property of requests, not of a filename. No floor is needed here
    /// — an empty scan compares empty against one name and fails, which is the vacuity guard.
    /// </remarks>
    [Fact]
    public void The_client_is_the_only_file_that_issues_a_write_verb()
    {
        var issuing = Sources()
            .Where(file => IssuesAWriteVerb(File.ReadAllText(file)))
            .Select(file => Path.GetFileName(file)!)
            .Order(StringComparer.Ordinal)
            .ToList();

        const string moved = "The set of files issuing an HTTP write verb has changed. Every write is a request "
            + "that can reach the production AUR space (CLAUDE.md §0.1, rule §1.4), and the lock is "
            + "enforced upstream of the client's callers — a file issuing its own requests has no such "
            + "upstream, and none of the three facts above can see it, because they scan the client by "
            + "name. Route it through ConfluenceClient, or widen this class to cover it. Issuing:";

        List<string> expected = [ClientFile];

        issuing.ShouldBe(expected, moved);
    }

    /// <summary>
    /// No write is spelled in a way the verb scan cannot see — the assertion an <c>HttpClient</c> verb
    /// helper fails.
    /// </summary>
    /// <remarks>
    /// The cheapest way past every fact in this class is not a new file or a new caller: it is spelling
    /// the same request so the scan reads no verb. Respelling one existing write as
    /// <c>new HttpMethod("DELETE")</c> was caught only by fact 1's floor, and only because it took the
    /// count below the ten pinned methods — an <em>eleventh</em> write spelled that way would take it
    /// nowhere and pass.
    /// </remarks>
    [Fact]
    public void No_write_is_spelled_in_a_way_the_verb_scan_cannot_see()
    {
        var found = Sources()
            .SelectMany(file => Hidden(File.ReadAllText(file))
                .Select(spelling => $"`{spelling}` in {Path.GetFileName(file)}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        const string invisible = "A request is spelled in a way this class cannot classify as a verb, which "
            + "makes any write behind it invisible to every fact here and to the production write lock "
            + "they exist to make structural (CLAUDE.md §0.1, rule §1.4). Send it through "
            + "ConfluenceClient's SendAsync with a static HttpMethod, or teach Verbs and WriteVerbs the "
            + "spelling in the same change. Found:";

        found.ShouldBeEmpty(invisible);

        // Second, not first, and the order is the whole design: this floor exists only to give the empty
        // list above a meaning. Asserted ahead of it, a respelled write trips the floor and the run
        // reports a broken instrument where it could have named the spelling.
        var scanned = WriteVerbs().Count;

        var blind = $"The verb scan found {scanned} write(s) in {ClientFile}, fewer than the "
            + $"{WriteMethods.Count} pinned. It has stopped reading the client, so the clean result above "
            + "is a statement about nothing.";

        scanned.ShouldBeGreaterThanOrEqualTo(WriteMethods.Count, blind);
    }

    /// <summary>Whether <paramref name="text"/> names any of <see cref="Verbs"/> as an HTTP method.</summary>
    private static bool IssuesAWriteVerb(string text) => Verbs
        .Any(verb => text.Contains($"HttpMethod.{verb}", StringComparison.Ordinal));

    /// <summary>The unscannable spellings present in <paramref name="text"/>.</summary>
    private static IEnumerable<string> Hidden(string text) => UnscannableSpellings
        .Where(spelling => text.Contains(spelling, StringComparison.Ordinal));

    /// <summary>
    /// Every write verb in the client, with the public method enclosing it. <c>Method</c> is
    /// <c>null</c> where the nearest enclosing member declaration is not public.
    /// </summary>
    private static List<(string? Method, string Verb, int Line)> WriteVerbs()
    {
        var lines = File.ReadAllLines(ClientPath());
        var found = new List<(string? Method, string Verb, int Line)>();

        for (var index = 0; index < lines.Length; index++)
        {
            var verb = Verbs.FirstOrDefault(
                candidate => lines[index].Contains($"HttpMethod.{candidate}", StringComparison.Ordinal));

            if (verb is null)
            {
                continue;
            }

            found.Add((EnclosingMethod(lines, index), verb, index + 1));
        }

        return found;
    }

    /// <summary>
    /// The name of the public member whose body contains <paramref name="index"/>, or <c>null</c> when
    /// the nearest enclosing declaration is not public.
    /// </summary>
    /// <remarks>
    /// Walks back to the nearest member-level declaration rather than to the nearest <c>public</c> one.
    /// The difference is the whole point: stopping at the first <c>public</c> would step straight over a
    /// private helper and file its verbs under whichever public method happened to sit above it, which
    /// is exactly the misattribution the orphan check exists to report.
    /// </remarks>
    private static string? EnclosingMethod(string[] lines, int index)
    {
        for (var line = index; line >= 0; line--)
        {
            var text = lines[line];

            if (!IsMemberDeclaration(text))
            {
                continue;
            }

            if (!text.StartsWith("    public ", StringComparison.Ordinal))
            {
                return null;
            }

            var open = text.IndexOf('(', StringComparison.Ordinal);
            var start = text.LastIndexOf(' ', open - 1);

            return start < 0 ? null : text[(start + 1)..open];
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="text"/> declares a member: four spaces of indent, an access modifier, and
    /// a parameter list. Read on the raw line so a nested local function, indented further, is not one.
    /// </summary>
    private static bool IsMemberDeclaration(string text)
    {
        var modifiers = text.StartsWith("    public ", StringComparison.Ordinal)
            || text.StartsWith("    private ", StringComparison.Ordinal)
            || text.StartsWith("    internal ", StringComparison.Ordinal)
            || text.StartsWith("    protected ", StringComparison.Ordinal);

        if (!modifiers)
        {
            return false;
        }

        var open = text.IndexOf('(', StringComparison.Ordinal);

        return open > 0;
    }

    /// <summary>
    /// Every file under <c>src/</c> outside the client that invokes a write method, with the methods it
    /// invokes. Matched on the name followed immediately by <c>(</c>, the spelling
    /// <see cref="Confluence.RemoteBodyReadTests"/> uses for the same reason: a documentation reference
    /// carries no parameter list.
    /// </summary>
    private static Dictionary<string, List<string>> WriteCallers()
    {
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in Sources())
        {
            var name = Path.GetFileName(file);

            if (string.Equals(name, ClientFile, StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var called = WriteMethods.Keys
                .Where(method => text.Contains($"{method}(", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList();

            if (called.Count > 0)
            {
                hits[name] = called;
            }
        }

        return hits;
    }

    private static string ClientPath() => Sources()
        .Single(file => string.Equals(Path.GetFileName(file), ClientFile, StringComparison.Ordinal));

    /// <summary>Every committed C# source under <c>src/</c>, build output excluded.</summary>
    private static IEnumerable<string> Sources() => Directory
        .EnumerateFiles(Path.Combine(DocumeCli.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
