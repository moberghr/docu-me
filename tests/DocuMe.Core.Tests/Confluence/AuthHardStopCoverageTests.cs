using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Confluence;

/// <summary>
/// The structural half of rule §1.2 — a 401/403 is a hard stop, never a retry — asserted over the tree
/// rather than over the two surfaces somebody remembered to test.
/// </summary>
/// <remarks>
/// <para>
/// The client half is settled: one <c>SendAsync</c>, and every call site reaches it, so a 401 throws
/// <c>ConfluenceAuthenticationException</c> and the resilience pipeline never replays it
/// (<c>ConfluenceClientOptions</c>, <c>ConfluenceClientTests</c>). What that says nothing about is what
/// the layers above <em>catch</em>. <c>ConfluenceAuthenticationException</c> is a
/// <c>ConfluenceException</c>, so a tolerant <c>catch (ConfluenceException)</c> inside a per-item loop
/// swallows an expired token as though it were one page's own problem and the run walks on to the next
/// item with a dead credential — which is exactly the ~80-request replay rule §1.2 exists to prevent.
/// </para>
/// <para>
/// This is the same class of property as <c>WriteLockCoverageTests</c> and for the same reason: it is
/// enforced by <em>where the catch sits</em>, so no run of a tolerant site can demonstrate that the next
/// one is protected. Every base-<c>ConfluenceException</c> catch under <c>src/</c> is derived from source
/// and compared against the pinned classification below, in both directions. A new one is a red suite
/// naming it until somebody writes down which mechanism carries the hard stop through it.
/// </para>
/// <para>
/// What this class does <em>not</em> claim: that a protected site behaves correctly when it fires. Seven
/// mechanisms are pinned below, six auth catches and one filter, and four of them are executed by a test
/// that answers 401 or 403 for real — the page loop, the prune delete, the reply post and the space probe.
/// The other three are pinned here and run by nothing: the open-comment read's filter, the child-order
/// pass, and the <em>resolve</em> half of the reply loop. Measured, not assumed: deleting each of those
/// three failed exactly one test in the suite, this class's second fact. The residual is recorded rather
/// than papered over.
/// </para>
/// </remarks>
public sealed class AuthHardStopCoverageTests
{
    /// <summary>How rule §1.2's hard stop survives a given catch site.</summary>
    private enum HardStop
    {
        /// <summary>
        /// The block ends the run, so an auth failure landing in it is still a stop. Nothing more is
        /// asked of these sites than that they keep ending it.
        /// </summary>
        Aborts,

        /// <summary>
        /// The run continues past this catch, so a dedicated <c>catch
        /// (ConfluenceAuthenticationException)</c> on the same <c>try</c> has to take the subclass first.
        /// Also pinned where a site both aborts <em>and</em> keeps an auth catch for its own message: the
        /// message is the half a mutation would take away silently.
        /// </summary>
        AuthFirst,

        /// <summary>
        /// Same exposure, closed the other way: the site itself carries
        /// <c>when (ex is not ConfluenceAuthenticationException)</c>, so the subclass is never caught here
        /// at all.
        /// </summary>
        Filtered,
    }

    /// <summary>
    /// Every catch of the base <c>ConfluenceException</c> under <c>src/</c>, keyed
    /// <c>&lt;file&gt; &lt;member&gt; #&lt;nth in that member&gt;</c>, with the mechanism that carries
    /// §1.2 through it. Twelve, as derived from source before this class existed.
    /// </summary>
    private static readonly Dictionary<string, HardStop> Sites = new(StringComparer.Ordinal)
    {
        ["DashboardCommand.cs RunAsync #1"] = HardStop.Aborts,
        ["DriftCommand.cs LabelAsync #1"] = HardStop.Aborts,
        ["DriftCommand.cs RefreshDashboardAsync #1"] = HardStop.Aborts,
        ["FeedbackReplyExecutor.cs ExecuteAsync #1"] = HardStop.AuthFirst,
        ["FeedbackReplyExecutor.cs ExecuteAsync #2"] = HardStop.AuthFirst,
        ["PruneExecutor.cs PruneAsync #1"] = HardStop.AuthFirst,
        ["PublishExecutor.cs ExecuteAsync #1"] = HardStop.Aborts,
        ["PublishExecutor.cs ExecuteAsync #2"] = HardStop.AuthFirst,
        ["PublishExecutor.cs GuardOpenCommentsAsync #1"] = HardStop.Filtered,
        ["PublishExecutor.cs ReconcileChildOrderAsync #1"] = HardStop.AuthFirst,
        ["StatusProbes.cs SpaceAsync #1"] = HardStop.AuthFirst,
        ["SyncCommand.cs RunAsync #1"] = HardStop.Aborts,
    };

    private const string Site = "catch (ConfluenceException";
    private const string AuthCatch = "catch (ConfluenceAuthenticationException";
    private const string Filter = "when (ex is not ConfluenceAuthenticationException)";

    /// <summary>
    /// Every base catch the tree ships is one this class has classified — the assertion a thirteenth one
    /// fails, in either direction.
    /// </summary>
    [Fact]
    public void Every_catch_of_a_confluence_failure_is_one_this_class_has_classified()
    {
        var found = CatchSites();

        // Vacuous-pass guard. A moved src/, a renamed exception or a scan that stopped matching would
        // otherwise compare two empty sets and report rule §1.2 fully classified.
        var lost = $"The scan found {found.Count} base-ConfluenceException catch site(s), fewer than the "
            + $"{Sites.Count} pinned in this class. It has stopped reading src/ rather than found the tree "
            + "shedding catch blocks.";

        found.Count.ShouldBeGreaterThanOrEqualTo(Sites.Count, lost);

        var unnamed = found
            .Where(site => site.Member is null)
            .Select(site => $"{site.FileName}:{site.Line}")
            .ToList();

        const string anonymous = "A base-ConfluenceException catch sits outside any member this scan can name, so "
            + "no key can be written for it. Check the declaration's shape against MemberName. Sites:";

        unnamed.ShouldBeEmpty(anonymous);

        const string drifted = "The set of places that catch a Confluence failure has changed. Every one of them "
            + "decides what an expired token does next, and rule §1.2 says the answer is always 'stop' "
            + "— never 'report this item and try the next with the same dead credential'. Read the new "
            + "site, decide whether it ends the run (Aborts) or needs the subclass taken first "
            + "(AuthFirst/Filtered), and pin it. Derived:";

        found
            .Select(Key)
            .Order(StringComparer.Ordinal)
            .ShouldBe(Sites.Keys.Order(StringComparer.Ordinal), drifted);
    }

    /// <summary>
    /// Every site whose run walks on still has the expired token taken off it first — the assertion a
    /// deleted auth catch fails.
    /// </summary>
    /// <remarks>
    /// This is the fact with something to catch. C# refuses a base catch placed before its subclass
    /// (CS0160), so the order cannot regress silently; what can, and compiles cleanly, is somebody
    /// deleting the auth clause or the <c>is not</c> filter while every test name stays exactly as it was.
    /// </remarks>
    [Fact]
    public void Every_tolerant_site_takes_the_expired_token_off_the_run_first()
    {
        var tolerated = CatchSites()
            .Where(site => Sites.TryGetValue(Key(site), out var stop) && stop != HardStop.Aborts)
            .ToList();

        var declared = Sites.Count(entry => entry.Value != HardStop.Aborts);

        var thin = $"Only {tolerated.Count} of the {declared} tolerant site(s) were matched in source, so "
            + "this fact is asserting less than it names.";

        tolerated.Count.ShouldBe(declared, thin);

        var unguarded = tolerated
            .Where(site => !Protects(site))
            .Select(Key)
            .ToList();

        const string swallowed = "A catch that lets the run continue no longer takes an expired token off it "
            + "first, so ConfluenceAuthenticationException now lands in the tolerant branch and the "
            + "next item is attempted with the same dead credential (rule §1.2: a token retried across "
            + "a bulk run is how an account gets locked out). Restore the "
            + "`catch (ConfluenceAuthenticationException)` above it, or the "
            + "`when (ex is not ConfluenceAuthenticationException)` filter on it. Offenders:";

        unguarded.ShouldBeEmpty(swallowed);
    }

    /// <summary>
    /// Every site classified <c>Aborts</c> still ends the run, since that is the whole of its claim.
    /// </summary>
    /// <remarks>
    /// The regression this exists for is a one-word edit: turning a <c>return</c> into a <c>continue</c>
    /// converts a site that needs no auth clause into one that does, and nothing else in the tree would
    /// notice. Read on the block's own statements, so a <c>return</c> belonging to a neighbouring catch
    /// cannot answer for this one.
    /// </remarks>
    [Fact]
    public void Every_aborting_site_still_ends_the_run()
    {
        var aborting = CatchSites()
            .Where(site => Sites.TryGetValue(Key(site), out var stop) && stop == HardStop.Aborts)
            .ToList();

        var declared = Sites.Count(entry => entry.Value == HardStop.Aborts);

        var thin = $"Only {aborting.Count} of the {declared} aborting site(s) were matched in source, so "
            + "this fact is asserting less than it names.";

        aborting.Count.ShouldBe(declared, thin);

        var walked = aborting
            .Where(site => !Ends(site))
            .Select(Key)
            .ToList();

        const string continued = "A site pinned as ending the run does not end it any more. That is how a "
            + "hard stop turns into a replay: the block reports the failure, the loop takes the next "
            + "item, and an expired token is spent once per item (rule §1.2). Either restore the "
            + "`return`, or reclassify the site as AuthFirst and add the auth catch it now needs. "
            + "Offenders:";

        walked.ShouldBeEmpty(continued);
    }

    private static string Key(CatchSite site) => $"{site.FileName} {site.Member} #{site.Ordinal}";

    /// <summary>
    /// Whether an auth handler covers <paramref name="site"/>: the filter on the catch itself, or a
    /// <c>catch (ConfluenceAuthenticationException)</c> at the same indentation between it and its
    /// <c>try</c>.
    /// </summary>
    /// <remarks>
    /// Indentation is what makes the second half precise. Catch clauses are indented with their own
    /// <c>try</c>, so walking back only over lines at the site's own depth cannot credit a nested
    /// <c>try</c>'s auth catch to the outer one, and stopping at the <c>try</c> cannot credit the
    /// previous statement's.
    /// </remarks>
    private static bool Protects(CatchSite site)
    {
        var lines = Lines(site.FileName);

        if (lines[site.Line - 1].Contains(Filter, StringComparison.Ordinal))
        {
            return true;
        }

        var depth = Indent(lines[site.Line - 1]);

        for (var index = site.Line - 2; index >= 0; index--)
        {
            var text = lines[index];

            if (text.Trim().Length == 0 || Indent(text) != depth)
            {
                continue;
            }

            if (text.TrimStart().StartsWith(AuthCatch, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(text.Trim(), "try", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether the catch block at <paramref name="site"/> returns and never continues.</summary>
    private static bool Ends(CatchSite site)
    {
        var statements = Block(Lines(site.FileName), site.Line - 1)
            .Select(line => line.Trim())
            .ToList();

        var returns = statements.Any(
            text => text.StartsWith("return ", StringComparison.Ordinal)
                || string.Equals(text, "return;", StringComparison.Ordinal));

        var walks = statements.Any(text => string.Equals(text, "continue;", StringComparison.Ordinal));

        return returns && !walks;
    }

    /// <summary>
    /// Every base-<c>ConfluenceException</c> catch under <c>src/</c>, with the member holding it and its
    /// position within that member. Matched on the trimmed line so a doc comment naming the clause — the
    /// prose and the code share one file — cannot register as one.
    /// </summary>
    private static List<CatchSite> CatchSites()
    {
        var sites = new List<CatchSite>();

        foreach (var file in Sources())
        {
            var lines = File.ReadAllLines(file);
            var name = Path.GetFileName(file);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].TrimStart().StartsWith(Site, StringComparison.Ordinal))
                {
                    continue;
                }

                var member = MemberName(lines, index);
                var ordinal = 0;

                if (member is not null)
                {
                    counts.TryGetValue(member, out ordinal);
                    ordinal++;
                    counts[member] = ordinal;
                }

                sites.Add(new CatchSite(name, member, ordinal, index + 1));
            }
        }

        return sites;
    }

    /// <summary>
    /// The member whose body contains <paramref name="index"/>, or <c>null</c> where none can be named.
    /// </summary>
    /// <remarks>
    /// Walks back to the nearest member-level declaration. A return type wide enough to wrap puts the
    /// name on the continuation line instead — <c>Task&lt;(DocumeState Marked, …)&gt;</c> followed by
    /// <c>LabelAsync(</c> — and the parenthesis it does carry belongs to a tuple, so reading the token
    /// before the first <c>(</c> would file that method's catches under <c>Task&lt;</c>.
    /// </remarks>
    private static string? MemberName(string[] lines, int index)
    {
        for (var line = index; line >= 0; line--)
        {
            var text = lines[line];

            if (!IsMemberDeclaration(text))
            {
                continue;
            }

            var open = text.IndexOf('(', StringComparison.Ordinal);
            var start = text.LastIndexOf(' ', open - 1);
            var name = start < 0 ? string.Empty : text[(start + 1)..open];

            return IsIdentifier(name) ? name : Wrapped(lines, line);
        }

        return null;
    }

    /// <summary>The name on the line below a declaration whose return type filled the first one.</summary>
    private static string? Wrapped(string[] lines, int declaration)
    {
        for (var line = declaration + 1; line < Math.Min(declaration + 3, lines.Length); line++)
        {
            var text = lines[line].Trim();

            if (!text.EndsWith('('))
            {
                continue;
            }

            var name = text[..^1];

            if (IsIdentifier(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="text"/> declares a member: four spaces of indent, an access modifier, and
    /// a parameter list. Read on the raw line, so a local function indented further is not one.
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

        var parameters = text.IndexOf('(', StringComparison.Ordinal);

        return parameters > 0;
    }

    private static bool IsIdentifier(string text) =>
        text.Length > 0 && text.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static int Indent(string text) => text.Length - text.TrimStart().Length;

    /// <summary>The statements of the block opening at or just below <paramref name="index"/>.</summary>
    private static List<string> Block(string[] lines, int index)
    {
        var start = lines[index].Contains('{', StringComparison.Ordinal) ? index : index + 1;
        var depth = 0;
        var body = new List<string>();

        for (var cursor = start; cursor < lines.Length; cursor++)
        {
            depth += lines[cursor].Count(character => character == '{');
            depth -= lines[cursor].Count(character => character == '}');

            if (cursor > start)
            {
                body.Add(lines[cursor]);
            }

            if (depth == 0 && cursor > start)
            {
                break;
            }
        }

        return body;
    }

    private static string[] Lines(string file) => File.ReadAllLines(
        Sources().Single(path => string.Equals(Path.GetFileName(path), file, StringComparison.Ordinal)));

    /// <summary>Every committed C# source under <c>src/</c>, build output excluded.</summary>
    private static IEnumerable<string> Sources() => Directory
        .EnumerateFiles(Path.Combine(DocumeCli.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>One catch of a Confluence failure: where it is, and what holds it.</summary>
    private sealed record CatchSite(string FileName, string? Member, int Ordinal, int Line);
}
