using DocuMe.Core.Drift;
using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Drift;

/// <summary>
/// The PR-comment block (PLAN.md §6.4's <c>--format github-comment</c>). It is posted by a bot that
/// edits one comment in place, so what matters is that it is stable across runs, that it says something
/// honest when nothing drifted, and that no string a consumer writes — a title, an owner, a path, a
/// glob — can turn into markdown formatting.
/// </summary>
/// <remarks>
/// <see cref="IDisposable"/> because the path fixtures are real files in a real wiki root:
/// <c>WikiTree.InScope</c> reads page paths off the working tree with
/// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
/// (<c>src/DocuMe.Core/Markdown/WikiTree.cs:255-258</c>), so a filename's own bytes are what reach the
/// renderer — never git's quoted spelling of them. A hand-built <see cref="DriftedPage"/> would test the
/// renderer against a path this suite invented and prove nothing about the route a repo actually takes,
/// which is exactly how the code-span vector survived three reviews.
/// </remarks>
public sealed class DriftCommentTests : IDisposable
{
    /// <summary>What an owned group's heading opens with, mirrored from <c>DriftComment.OwnerHeading</c>.</summary>
    private const string OwnerHeading = "**Owner:**";

    /// <summary>
    /// The payload every neutralization test below carries: a blank line, an ATX heading forging the one
    /// verdict a reviewer would act on, and prose that reads like the renderer's own. Spelled with LF
    /// here; <see cref="Render_NeutralizesEveryLineSeparatorAnUntrustedStringCanCarry"/> is what stops
    /// that one spelling from becoming the whole claim.
    /// </summary>
    private const string Forgery = "\n\n### No drift detected\n\nNothing to review here.";

    /// <summary>
    /// A page whose <c>owner:</c> is a double-quoted YAML scalar carrying <c>\n</c> escapes. YAML decodes
    /// them into real newlines before <see cref="PageFrontmatter"/> ever sees the value, so what lands in
    /// the renderer is a multi-line string that would open a forged heading and an unclosed
    /// <c>&lt;details&gt;</c> of its own.
    /// </summary>
    private const string QuotedEscapeOwner = """
        ---
        title: Loans
        owner: "@alice\n\n### No drift detected\n\nNothing to review here.\n\n<details><summary>Resolved</summary>"
        sources:
          - src/Loans/**
        ---

        # Loans
        """;

    /// <summary>
    /// The same payload spelled as a <c>|</c> block scalar — YAML's other way to put a newline in a
    /// scalar, and the one a reader of the file would find perfectly ordinary-looking.
    /// </summary>
    private const string BlockScalarOwner = """
        ---
        title: Loans
        owner: |
          @alice

          ### No drift detected

          Nothing to review here.

          <details><summary>Resolved</summary>
        sources:
          - src/Loans/**
        ---

        # Loans
        """;

    /// <summary>
    /// No newline at all. CommonMark passes inline raw HTML through unchanged and GitHub's sanitizer
    /// allows <c>&lt;details&gt;</c>/<c>&lt;summary&gt;</c>, so a single unclosed tag on the heading line
    /// collapses the page list and every group below it.
    /// </summary>
    private const string InlineHtmlOwner = """
        ---
        title: Loans
        owner: "@a <details><summary>Resolved</summary>"
        sources:
          - src/Loans/**
        ---

        # Loans
        """;

    /// <summary>
    /// The same forgery as <see cref="BlockScalarOwner"/>, spelled through <c>title:</c> — the sibling
    /// YAML scalar in the same frontmatter, written by the same author, rendered into the same comment.
    /// <c>title:</c> reaches the bullet through <c>DriftComment.Escape</c>, which neutralizes the markdown
    /// metacharacters and, before this slice, nothing about line endings: a <c>|</c> block scalar carried
    /// its newlines straight through and everything after the first one became a fresh CommonMark block.
    /// </summary>
    private const string BlockScalarTitle = """
        ---
        title: |
          Loans

          ### No drift detected

          Nothing to review here.
        owner: "@alice"
        sources:
          - src/Loans/**
        ---

        # Loans
        """;

    /// <summary>
    /// The same payload as <see cref="BlockScalarTitle"/> through YAML's other newline carrier, a
    /// double-quoted scalar with <c>\n</c> escapes, and with the raw-HTML half appended: unlike the owner
    /// route, <c>Escape</c> already emitted <c>\&lt;</c> here, so the <c>&lt;details&gt;</c> collapse never
    /// reproduced for a title — the assertion below pins that it still does not.
    /// </summary>
    private const string QuotedEscapeTitle = """
        ---
        title: "Loans\n\n### No drift detected\n\nNothing to review here.\n\n<details><summary>Resolved</summary>"
        owner: "@alice"
        sources:
          - src/Loans/**
        ---

        # Loans
        """;

    /// <summary>
    /// W1's two crafted <c>sources:</c> globs, both of which match a real changed file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first opens with a line break. <c>DriftPlanner.NormalizePattern</c>
    /// (<c>DriftPlanner.cs:212</c>) calls <see cref="string.Trim()"/> before building the matcher, so it
    /// matches <c>src/Loans/A.cs</c> exactly as the untrimmed spelling would — and
    /// <c>MatchesByPattern</c> (<c>DriftPlanner.cs:177</c>) then keys the dictionary on the
    /// <em>raw</em> pattern and hands that back. "A pattern that could forge a block is a pattern that
    /// matched nothing and is therefore absent" was false, and this is the counter-example.
    /// </para>
    /// <para>
    /// The second carries backticks and needs no trimming quirk at all: the author writes the glob and
    /// the directory it names in the same push, and git does not quote a backtick in a path (see
    /// <c>GitRepositoryTests.Quotes_a_line_break_in_a_path_and_leaves_a_backtick_alone</c>), so both
    /// halves of this line reach the comment as literal bytes.
    /// </para>
    /// </remarks>
    private const string CraftedSourcePatterns = """
        ---
        title: Loans
        owner: "@moberghr/lending"
        sources:
          - "\nsrc/Loans/**"
          - "src/Loans/`<details><summary>Resolved`/**"
        ---

        # Loans
        """;

    /// <summary>The ordinary frontmatter the path fixtures vary only the <em>filename</em> of.</summary>
    private const string OrdinaryPage = """
        ---
        title: Loans
        owner: "@moberghr/lending"
        sources:
          - src/Loans/**
        ---

        # Loans
        """;

    /// <summary>
    /// The payload a value carries when it is closing a code span rather than opening a block: the first
    /// backtick ends the span early, the raw HTML that follows is then live markdown, and the second
    /// re-opens a span so the line still ends looking like a path. No line ending needed, and a backtick
    /// is a legal filename byte on every platform this tool runs on.
    /// </summary>
    /// <remarks>
    /// The <c>&lt;details&gt;</c> is left unclosed and the <c>&lt;/summary&gt;</c> dropped, because a
    /// closing tag would need a <c>/</c> and this string has to be legal inside a single path segment.
    /// Unclosed is also the stronger attack: GitHub's sanitizer allowlists <c>&lt;details&gt;</c>, and an
    /// unclosed one swallows the rest of the comment into a triangle the attacker labelled.
    /// </remarks>
    private const string BacktickPayload = "`<details><summary>Resolved`";

    /// <summary>
    /// The wiki root the path fixtures are written into. Real files, because
    /// <c>WikiTree.InScope</c> is the producer under test as much as the renderer is.
    /// </summary>
    private readonly string _wikiRoot =
        Directory.CreateTempSubdirectory("docume-drift-comment-tests").FullName;

    public void Dispose() => Directory.Delete(_wikiRoot, recursive: true);

    [Fact]
    public void Render_OpensWithTheMarkerACiJobFindsItsOwnCommentBy()
    {
        DriftComment.Render(Report(Page())).ShouldStartWith(DriftComment.Marker);
    }

    [Fact]
    public void Render_NamesTheAffectedPagesAndWhatTheyMatched()
    {
        var comment = DriftComment.Render(Report(Page()));

        comment.ShouldContain("This PR touches sources for **1 wiki page** of 3 with declared sources:");
        comment.ShouldContain("- **Loans** — `domains/loans.md`");
        comment.ShouldContain("- `src/Loans/**` → `src/Loans/A.cs`, `src/Loans/B.cs`");
        comment.ShouldContain("advisory");
    }

    [Fact]
    public void Render_CarriesTheRevisionsAndTheDiffSize()
    {
        var comment = DriftComment.Render(Report(Page()));

        comment.ShouldContain("baseline `abc1234` → head `def5678`, 9 changed files.");
    }

    /// <summary>
    /// The comment is a slot, not a log: leaving yesterday's warning up after the PR fixed the docs
    /// would teach the reviewer to ignore it.
    /// </summary>
    [Fact]
    public void Render_SaysSoWhenNothingDrifted()
    {
        var comment = DriftComment.Render(Report());

        comment.ShouldStartWith(DriftComment.Marker);
        comment.ShouldContain("No documented sources were touched.");
        comment.ShouldNotContain("touches sources for");
    }

    [Fact]
    public void Render_DistinguishesNothingDriftedFromNothingDeclared()
    {
        var comment = DriftComment.Render(Report() with { PagesWithSourcesCount = 0 });

        comment.ShouldContain("No page in this wiki declares a `sources:` glob");
        comment.ShouldNotContain("No documented sources were touched.");
    }

    /// <summary>
    /// The disclosure that keeps "nothing drifted" honest (§6.4): when <c>_meta/drift-ignore</c>
    /// narrowed the inputs, the comment says so — a reviewer reading a quiet verdict must be able to
    /// tell an exempted change from an unmatched one.
    /// </summary>
    [Fact]
    public void Render_DisclosesTheExemptionsBehindAQuietVerdict()
    {
        var report = Report() with
        {
            Exempted =
            [
                new ExemptedChange("src/Generated/Api.cs", "src/Generated/**", "codegen sweep"),
                new ExemptedChange("vendor/lib.js", "vendor/**", null),
            ],
        };

        var comment = DriftComment.Render(report);

        comment.ShouldContain("No documented sources were touched.");
        comment.ShouldContain("2 changed files were exempted by `_meta/drift-ignore`:");
        comment.ShouldContain("`src/Generated/Api.cs` (`src/Generated/**` — codegen sweep)");
        comment.ShouldContain("`vendor/lib.js` (`vendor/**`)");
        comment.ShouldContain("9 changed files, 2 exempted.");
    }

    [Fact]
    public void Render_SaysNothingAboutExemptionsWhenThereAreNone()
    {
        DriftComment.Render(Report()).ShouldNotContain("exempted");
    }

    /// <summary>
    /// The other disclosure (§6.4), mirrored from the exemption one: when
    /// <c>_meta/drift-ignore-revs</c> narrowed the attribution, the provenance line says how many
    /// commits were held out, and a report that ignored none says nothing about commits at all.
    /// </summary>
    [Fact]
    public void Render_DisclosesTheIgnoredCommitsInTheProvenance()
    {
        var comment = DriftComment.Render(Report() with { IgnoredCommitCount = 2 });

        comment.ShouldContain("9 changed files, 2 commits ignored.");
        DriftComment.Render(Report()).ShouldNotContain("commit");
    }

    /// <summary>
    /// The third disclosure (spec §3.4), mirroring the exemption one: a page the diff flagged and its own
    /// seal held out is named here, with the date of the seal, because the verdict above it was narrowed
    /// by a claim about bytes rather than by anything a human declared — and round 6's lesson is that the
    /// PR-comment format is exactly where a disclosure the other formats carry goes missing.
    /// </summary>
    [Fact]
    public void Render_DisclosesTheSealedPagesBehindAQuietVerdict()
    {
        var report = Report() with
        {
            Sealed =
            [
                new SealedPage("domains/loans.md", "Loans", "2026-08-19T09:12:44Z"),
                new SealedPage("domains/rates.md", "Rates_and_Fees", null),
            ],
        };

        var comment = DriftComment.Render(report);

        // The verdict line says why it is quiet rather than claiming nothing was touched: the section
        // directly below it names two pages whose sources this PR did touch, and a disclosure that
        // contradicts the sentence above it is worse than no disclosure at all.
        comment.ShouldContain(
            "This PR touches documented sources, but all 2 pages they belong to are byte-identical");

        comment.ShouldNotContain("No documented sources were touched.");
        comment.ShouldContain("2 flagged pages were held out by their seal");

        var one = DriftComment.Render(Report() with
        {
            Sealed = [new SealedPage("domains/loans.md", "Loans", "2026-08-19T09:12:44Z")],
        });

        one.ShouldContain(
            "This PR touches documented sources, but the one page they belong to is byte-identical");

        one.ShouldContain("1 flagged page was held out by their seal");
        comment.ShouldContain(@"- **Loans** — `domains/loans.md` (sealed 2026-08-19T09:12:44Z)");

        // A title is consumer prose, escaped like every other one this file renders; a seal with no
        // recorded date says so rather than trailing an empty parenthesis.
        comment.ShouldContain(@"- **Rates\_and\_Fees** — `domains/rates.md`" + Environment.NewLine);

        comment.ShouldContain("9 changed files, 2 sealed.");
    }

    [Fact]
    public void Render_SaysNothingAboutSealsWhenNoPageIsSealed()
    {
        DriftComment.Render(Report(Page())).ShouldNotContain("sealed");
    }

    [Fact]
    public void Render_StatesTheOverflowRatherThanTrimmingQuietly()
    {
        var files = Enumerable.Range(1, 9).Select(index => $"src/Loans/F{index}.cs").ToList();
        var page = new DriftedPage(
            "domains/loans.md",
            "Loans",
            Owner: null,
            [new SourceMatch("src/Loans/**", files)]);

        var comment = DriftComment.Render(Report(page));

        comment.ShouldContain("`src/Loans/F5.cs` and 4 more");
        comment.ShouldNotContain("F6.cs");
    }

    [Fact]
    public void Render_EscapesAPageTitleThatLooksLikeMarkdown()
    {
        var page = new DriftedPage(
            "domains/rates.md",
            "Rates_and_Fees <draft>",
            Owner: null,
            [new SourceMatch("src/Rates/**", ["src/Rates/A.cs"])]);

        var comment = DriftComment.Render(Report(page));

        comment.ShouldContain(@"**Rates\_and\_Fees \<draft\>**");
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        var report = Report(Page());

        DriftComment.Render(report).ShouldBe(DriftComment.Render(report));
    }

    /// <summary>
    /// SC4: the affected pages group under their owner, ordinal by the owner string, with the unowned
    /// bucket last. Ordering is asserted as positions in the rendered text rather than as a returned
    /// list, because the comment <em>is</em> the contract — a bot rewrites one comment in place on every
    /// push (<see cref="DriftComment.Marker"/>), so an order that came out of a hash seed or a dictionary
    /// would produce a diff on every run with no change in the answer.
    /// </summary>
    [Fact]
    public void Render_GroupsTheAffectedPagesByOwnerWithUnownedLast()
    {
        var comment = DriftComment.Render(Report(
            Owned("domains/zebra.md", "Zebra", "@moberghr/zulu"),
            Page(),
            Owned("domains/alpha.md", "Alpha", "@moberghr/alpha")));

        var alpha = comment.IndexOf("**Owner:** @moberghr/alpha", StringComparison.Ordinal);
        var zulu = comment.IndexOf("**Owner:** @moberghr/zulu", StringComparison.Ordinal);
        var unowned = comment.IndexOf(DriftComment.UnownedHeading, StringComparison.Ordinal);

        alpha.ShouldBeGreaterThan(0);
        zulu.ShouldBeGreaterThan(alpha, "The owner groups are not in ordinal order.");
        unowned.ShouldBeGreaterThan(zulu, "The unowned bucket is not last.");

        // And the pages sit under the heading that names them, not merely somewhere in the comment.
        comment.IndexOf("`domains/alpha.md`", StringComparison.Ordinal).ShouldBeInRange(alpha, zulu);
        comment.IndexOf("`domains/zebra.md`", StringComparison.Ordinal).ShouldBeInRange(zulu, unowned);
        comment.IndexOf("`domains/loans.md`", StringComparison.Ordinal).ShouldBeGreaterThan(unowned);
    }

    /// <summary>
    /// The ordering is a function of the owner strings alone. The same pages arriving in a different
    /// order render byte-identical text, which is the property that keeps a sticky comment from showing
    /// an edit on a push that changed nothing about the answer.
    /// </summary>
    [Fact]
    public void Render_OrdersTheGroupsFromTheOwnersRatherThanFromTheArrivalOrder()
    {
        var alpha = Owned("domains/alpha.md", "Alpha", "@moberghr/alpha");
        var zebra = Owned("domains/zebra.md", "Zebra", "@moberghr/zulu");

        DriftComment.Render(Report(zebra, Page(), alpha))
            .ShouldBe(DriftComment.Render(Report(alpha, zebra, Page())));
    }

    /// <summary>
    /// SC5, and the fact this whole slice would be worthless without: grouping is a <strong>partition</strong>,
    /// not a filter. Every affected page is listed exactly once and the group sizes sum to
    /// <see cref="DriftReport.AffectedCount"/> — asserted directly rather than inferred from the
    /// implementation, because a grouping bug that dropped a page would hide exactly the drift this
    /// feature exists to route, and would look like a quiet comment rather than like a failure.
    /// </summary>
    [Fact]
    public void Render_ListsEveryAffectedPageExactlyOnceAcrossTheGroups()
    {
        // Two pages share an owner, two are unowned, one owner holds a single page: enough shape for a
        // grouping that silently dropped a duplicate key or a null one to show up as a wrong sum.
        var report = Report(
            Owned("domains/alpha.md", "Alpha", "@moberghr/alpha"),
            Owned("domains/beta.md", "Beta", "@moberghr/alpha"),
            Page(),
            Owned("domains/zebra.md", "Zebra", "@moberghr/zulu"),
            new DriftedPage("domains/fees.md", "Fees", null, [new SourceMatch("src/Fees/**", ["src/Fees/A.cs"])]));

        var comment = DriftComment.Render(report);
        var groups = Groups(comment);

        // The sum, which is the claim: nothing was dropped and nothing was listed twice.
        groups.Sum(group => group.Paths.Count).ShouldBe(report.AffectedCount);

        // Three buckets: two owners plus the unowned one.
        groups.Count.ShouldBe(3);

        // And the same pages, not merely the same number of them.
        groups.SelectMany(group => group.Paths).Order(StringComparer.Ordinal).ShouldBe(
            report.Pages.Select(page => page.Path).Order(StringComparer.Ordinal));

        foreach (var page in report.Pages)
        {
            Occurrences(comment, $"`{page.Path}`").ShouldBe(1, $"{page.Path} is not listed exactly once.");
        }
    }

    /// <summary>
    /// SC6: the owner reaches the comment exactly as the frontmatter spells it. Four shapes a real repo
    /// uses, because the failure the refusal avoids is silent — <c>alice</c> turned into <c>@alice</c>
    /// would notify whichever account holds that handle on the forge, a stranger in the email and
    /// display-name cases (spec §3.1).
    /// </summary>
    [Theory]
    [InlineData("alice")]
    [InlineData("@moberghr/lending")]
    [InlineData("@my_org/team")]
    [InlineData("_platform_")]
    [InlineData("*docs*")]
    [InlineData("mirko.budimir@moberg.hr")]
    [InlineData("Alice Smith")]
    public void Render_EmitsTheOwnerVerbatim(string owner)
    {
        var comment = DriftComment.Render(Report(Owned("domains/loans.md", "Loans", owner)));

        comment.ShouldContain($"**Owner:** {owner}");

        // No `@` prepended — including onto a handle that already carries one.
        comment.ShouldNotContain($"@{owner}");

        // No case folded, and no trimming that would leave the handle a forge cannot resolve.
        comment.ShouldNotContain(owner.ToUpperInvariant(), Case.Sensitive);
        comment.ShouldContain($"**Owner:** {owner}{Environment.NewLine}");
    }

    /// <summary>
    /// The unowned bucket is disclosed rather than hidden, and it says how many pages are in it: a drift
    /// report where nobody owns anything should say so out loud (spec §2).
    /// </summary>
    [Fact]
    public void Render_SaysHowManyAffectedPagesHaveNoOwner()
    {
        var two = DriftComment.Render(Report(
            Page(),
            new DriftedPage("domains/fees.md", "Fees", null, [new SourceMatch("src/Fees/**", ["src/Fees/A.cs"])])));

        two.ShouldContain($"{DriftComment.UnownedHeading} — 2 pages declare no `owner:`");

        DriftComment.Render(Report(Page()))
            .ShouldContain($"{DriftComment.UnownedHeading} — 1 page declares no `owner:`");
    }

    [Fact]
    public void Render_OmitsTheUnownedBucketWhenEveryAffectedPageHasAnOwner()
    {
        var comment = DriftComment.Render(Report(Owned("domains/loans.md", "Loans", "@moberghr/lending")));

        comment.ShouldNotContain(DriftComment.UnownedHeading);
        comment.ShouldContain("**Owner:** @moberghr/lending");
    }

    /// <summary>
    /// The owner is the one value this comment writes unescaped, and on a drift comment the PR author
    /// <em>is</em> the adversary: they change the code and commit the crafted <c>owner:</c> in the same
    /// push, and the scaffolded workflow (<c>templates/workflows/docs-drift-pr.yml</c>) then posts the
    /// result under the bot's identity as a sticky comment a reviewer trusts. Three spellings, each one
    /// a way out of the heading line: YAML's two newline carriers (a <c>\n</c> escape in a double-quoted
    /// scalar, a <c>|</c> block scalar) forge an ATX heading, and raw inline HTML needs no newline at all
    /// because CommonMark passes it through and GitHub's sanitizer allows <c>&lt;details&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Fed through the real <see cref="FrontmatterParser"/> rather than a hand-built
    /// <see cref="PageFrontmatter"/>, because half the claim is about what the parser lets through: its
    /// only filter is <c>IsNullOrWhiteSpace</c>, so a value with a newline in it is a value it hands on.
    /// A hand-built frontmatter would test the renderer against a string this suite invented and prove
    /// nothing about the path a repo actually takes.
    /// </remarks>
    [Theory]
    [InlineData(QuotedEscapeOwner)]
    [InlineData(BlockScalarOwner)]
    [InlineData(InlineHtmlOwner)]
    public void Render_NeutralizesAnOwnerThatWouldBreakOutOfItsHeadingLine(string page)
    {
        var owner = FrontmatterParser.Parse(page).Frontmatter.Owner;
        owner.ShouldNotBeNull();

        // The crafted owner sorts first (`@a…` before `@m…`), which is the adversary's own preference:
        // an unclosed `<details>` opened here would swallow the page list under it and every group below.
        var report = Report(
            new DriftedPage(
                "domains/loans.md",
                "Loans",
                owner,
                [new SourceMatch("src/Loans/**", ["src/Loans/A.cs"])]),
            Owned("domains/zebra.md", "Zebra", "@moberghr/zulu"),
            new DriftedPage(
                "domains/fees.md",
                "Fees",
                null,
                [new SourceMatch("src/Fees/**", ["src/Fees/A.cs"])]));

        var comment = DriftComment.Render(report);
        var lines = comment.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        // No forged verdict: an ATX heading is a `#` at the start of a line, and this comment opens
        // exactly one heading of its own.
        lines.Where(line => line.StartsWith('#')).ShouldBe(["### 📄 Documentation drift"]);

        // No raw HTML: `<details>`/`<summary>` are on GitHub's sanitizer allowlist, so an unclosed one
        // would collapse everything below it into a disclosure triangle labelled by the attacker.
        comment.ShouldNotContain("<details");
        comment.ShouldNotContain("<summary");
        comment.ShouldContain("&lt;details", customMessage: "The angle bracket was dropped, not escaped.");

        // Both owner headings are still one line each, and neither carries a bracket that opens a tag.
        var headings = lines.Where(line => line.StartsWith(OwnerHeading, StringComparison.Ordinal)).ToList();

        headings.Count.ShouldBe(2, "An owner escaped its heading line.");
        headings.ShouldAllBe(line => !line.Contains('<'));

        // And the report the forgery was trying to hide is intact: the same three groups, the same
        // partition, every page still listed under the heading that names it.
        var groups = Groups(comment);

        groups.Count.ShouldBe(3);
        groups.Sum(group => group.Paths.Count).ShouldBe(report.AffectedCount);
        groups.SelectMany(group => group.Paths).Order(StringComparer.Ordinal).ShouldBe(
            report.Pages.Select(affected => affected.Path).Order(StringComparer.Ordinal));

        comment.ShouldContain($"{OwnerHeading} @moberghr/zulu");
        comment.ShouldContain(DriftComment.UnownedHeading);
        comment.ShouldContain("- **Loans** — `domains/loans.md`");
        comment.ShouldContain("- **Zebra** — `domains/zebra.md`");
        comment.ShouldContain("- **Fees** — `domains/fees.md`");
    }

    /// <summary>
    /// The third construct, and the one the "exactly two characters" sentence would have talked a reader
    /// out of: an owner does not have to leave its line or open a tag to lie, it can stay on the line and
    /// become a <strong>link</strong>. <c>[Resolved — see the fix](https://evil.example/login)</c> renders
    /// as an arbitrarily-labelled clickable link inside a comment the bot signs, pointing anywhere the PR
    /// author likes; <c>![…](…)</c> is the same construct with an <c>!</c> in front and loads the target
    /// without a click.
    /// </summary>
    /// <remarks>
    /// Pinned on the entities rather than on "no link appeared", because the two failures look identical
    /// from the outside: an owner whose brackets were <em>dropped</em> also contains no <c>](</c>, and
    /// would silently delete a reader's data. The bytes must arrive and must be inert.
    /// </remarks>
    [Theory]
    [InlineData("[Resolved — see the fix](https://evil.example/login)")]
    [InlineData("![Resolved — see the fix](https://evil.example/pixel.png)")]
    public void Render_NeutralizesAnOwnerThatWouldTurnItsHeadingIntoALink(string owner)
    {
        var comment = DriftComment.Render(Report(Owned("domains/loans.md", "Loans", owner)));
        var heading = Lines(comment)
            .Single(line => line.StartsWith(OwnerHeading, StringComparison.Ordinal));

        // The reproduction, named: `](` is the seam of a link, and no bracket survives to open one.
        heading.ShouldNotContain("](", customMessage: "The owner rendered as a clickable link.");
        heading.ShouldNotContain("[", customMessage: "A bracket survived that can open a link label.");
        heading.ShouldNotContain("]", customMessage: "A bracket survived that can close a link label.");

        // Neutralized, not dropped: the label and the target are still in front of the reviewer, as the
        // text they always were. The entities are what `<` already uses on this line.
        heading.ShouldContain("&#91;Resolved — see the fix&#93;(https://evil.example/");
    }

    /// <summary>
    /// The forgery above, spelled through <c>title:</c> instead. Same frontmatter, same author, same
    /// comment: a fix that only taught the owner heading about line endings would leave the sibling scalar
    /// opening blocks of its own, so the claim under test is the class — <strong>no untrusted string this
    /// comment renders can reach a second line</strong> — and not the one field it was first reported on.
    /// </summary>
    /// <remarks>
    /// Fed through the real <see cref="FrontmatterParser"/> for the reason the owner theory is: the parser
    /// collapses a blank <c>title:</c> and nothing else, so a multi-line title is a value it hands on, and
    /// a hand-built <see cref="PageFrontmatter"/> would prove nothing about the path a repo actually takes.
    /// </remarks>
    [Theory]
    [InlineData(BlockScalarTitle)]
    [InlineData(QuotedEscapeTitle)]
    public void Render_NeutralizesATitleThatWouldBreakOutOfItsBulletLine(string page)
    {
        var title = FrontmatterParser.Parse(page).Title;
        title.ShouldNotBeNull();

        var report = Report(
            new DriftedPage(
                "domains/loans.md",
                title,
                "@moberghr/lending",
                [new SourceMatch("src/Loans/**", ["src/Loans/A.cs"])]),
            Owned("domains/zebra.md", "Zebra", "@moberghr/zulu"));

        var comment = DriftComment.Render(report);

        // The forgery, and the whole reason this is a Critical: an ATX heading is a `#` at the start of a
        // line, and this comment opens exactly one heading of its own.
        Lines(comment).Where(line => line.StartsWith('#')).ShouldBe(["### 📄 Documentation drift"]);

        // The raw-HTML half never reproduced here — `Escape` has always emitted `\<`, and a backslash
        // escape renders a literal `<` rather than opening a tag. Pinned on the bullet itself rather than
        // on the whole comment, so the `<!-- -->` marker and the `<sub>` the renderer writes for its own
        // provenance line are not mistaken for the title's bytes; the claim is that every angle bracket
        // reaching this line carries its backslash.
        var bullet = Lines(comment).Single(line => line.StartsWith("- **Loans", StringComparison.Ordinal));

        bullet
            .Replace(@"\<", string.Empty, StringComparison.Ordinal)
            .Replace(@"\>", string.Empty, StringComparison.Ordinal)
            .ShouldNotContain("<", customMessage: "A title opened a raw HTML tag.");

        // The report the forgery was trying to hide is intact: both pages still listed, still under the
        // heading that names them.
        var groups = Groups(comment);

        groups.Count.ShouldBe(2);
        groups.Sum(group => group.Paths.Count).ShouldBe(report.AffectedCount);
        comment.ShouldContain($"{OwnerHeading} @moberghr/lending");
        comment.ShouldContain($"{OwnerHeading} @moberghr/zulu");
        comment.ShouldContain("`domains/loans.md`");
    }

    /// <summary>
    /// The class claim, and the reason the two theories above are not it: <strong>a fix that only knew
    /// about <c>\n</c> would keep this whole suite green and hand the forgery straight back through a lone
    /// CR.</strong> Six separators, every one of them reachable from a YAML scalar and every one of them a
    /// line ending to somebody, fed through both untrusted routes at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set is the one <see cref="string.ReplaceLineEndings()"/> was <em>verified by execution</em> to
    /// collapse on .NET 10.0.11 — LF, CR, CRLF, FF, NEL, LS, PS — rather than the one its documentation
    /// lists, because the whole point of a class fix is that the boundary is where the runtime puts it and
    /// not where a doc comment claims. CRLF is left out only because CR and LF each appear here alone,
    /// which is the harder case.
    /// </para>
    /// <para>
    /// <strong>Through all three sinks, which is what makes this the class claim rather than two of
    /// three.</strong> Every separator is carried by a title and an owner (<c>Escape</c> and
    /// <c>Heading</c>) <em>and</em> by a page path, a <c>sources</c> glob, a matched file and the baseline
    /// revision (<c>Code</c>). Until the code-span route was added here, narrowing <c>Code</c>'s
    /// <c>ReplaceLineEndings</c> to a <c>\n</c> replace left all forty-six of these tests green while a
    /// page committed as <c>domains/loans\r\r### No drift detected\r\r.md</c> forged an ATX heading — the
    /// same hole this theory was written to close for the other two sinks.
    /// </para>
    /// <para>
    /// CommonMark itself breaks a line on LF, CR and CRLF only, so FF, NEL, LS and PS are neutralized
    /// wider than a forge strictly needs. That is the safe direction and it is free: no title, handle,
    /// path or revision contains one.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0x000A, "LF")]
    [InlineData(0x000D, "CR")]
    [InlineData(0x000C, "FF")]
    [InlineData(0x0085, "NEL")]
    [InlineData(0x2028, "LS")]
    [InlineData(0x2029, "PS")]
    public void Render_NeutralizesEveryLineSeparatorAnUntrustedStringCanCarry(int codePoint, string name)
    {
        var separator = char.ConvertFromUtf32(codePoint);
        var forgery = Forgery.Replace("\n", separator, StringComparison.Ordinal);

        // What each of the four Code sinks must read as once the separator is collapsed: one space per
        // separator, and every other byte of the value untouched.
        var flattened = Forgery.Replace("\n", " ", StringComparison.Ordinal);
        var path = $"domains/loans{forgery}.md";
        var pattern = $"src/Loans/**{forgery}";
        var file = $"src/Loans/A.cs{forgery}";

        var report = Report(
            new DriftedPage(
                path,
                $"Loans{forgery}",
                $"@alice{forgery}",
                [new SourceMatch(pattern, [file])]),
            Owned("domains/zebra.md", "Zebra", "@moberghr/zulu")) with
        {
            Baseline = $"abc1234{forgery}",
        };

        var comment = DriftComment.Render(report);
        var lines = Lines(comment);

        lines.Where(line => line.StartsWith('#')).ShouldBe(
            ["### 📄 Documentation drift"],
            customMessage: $"{name} forged a heading.");

        // Which route let go, named. The tail of the payload has to still be on the line its value opened,
        // so a separator that split the heading or the bullet is a failure here even when the fragment it
        // left behind happens not to start with a `#`.
        var headings = lines.Where(line => line.StartsWith(OwnerHeading, StringComparison.Ordinal)).ToList();

        headings.Count.ShouldBe(2, $"{name} in an owner changed the group count.");
        headings.ShouldContain(
            line => line.EndsWith("Nothing to review here.", StringComparison.Ordinal),
            customMessage: $"{name} carried an owner off its heading line.");

        var bullets = lines.Where(line => line.StartsWith("- **", StringComparison.Ordinal)).ToList();

        bullets.Count.ShouldBe(2, $"{name} in a title changed the bullet count.");
        bullets.ShouldContain(
            line => line.Contains("Nothing to review here.**", StringComparison.Ordinal),
            customMessage: $"{name} carried a title off its bullet line.");

        // The Code sinks, which no separator but LF reached until now. A value that broke its code span is
        // not on this list at all — an opening backtick run whose closer ended up in another block opens no
        // span — so this is the assertion a `\n`-only replace inside `Code` fails on a lone CR, and it
        // fails by naming the route rather than by a heading count three sections away.
        var spans = Spans(comment);

        spans.ShouldContain(
            $"domains/loans{flattened}.md",
            $"{name} carried a page path out of its code span.");
        spans.ShouldContain(
            $"src/Loans/**{flattened}",
            $"{name} carried a sources glob out of its code span.");
        spans.ShouldContain(
            $"src/Loans/A.cs{flattened}",
            $"{name} carried a matched file out of its code span.");
        spans.ShouldContain(
            $"abc1234{flattened}",
            $"{name} carried the baseline revision out of its code span.");

        // The separator is gone from the values themselves, not merely harmless — and this is what makes
        // the FF, NEL, LS and PS cases mean anything at all. CommonMark breaks a line on LF, CR and CRLF
        // only, so the assertions above pin those four cases not one bit: they would pass whether or not
        // the character was collapsed, because a span that still holds a NEL is still one span. Every one
        // of the six is a line ending to somebody reading this text — a forge's renderer, a terminal, a log
        // shipper — so what is worth pinning is that none of them reaches a rendered value. Scoped to the
        // heading, bullet and code-span contents rather than the whole comment, because the comment's own
        // line endings are LF.
        headings.Concat(bullets).Concat(spans).ShouldAllBe(
            value => !value.Contains(separator, StringComparison.Ordinal),
            customMessage: $"{name} survived inside a rendered value.");
    }

    /// <summary>
    /// The other half of the same claim, and the honest one: <see cref="string.ReplaceLineEndings()"/>
    /// leaves VT (U+000B) and NUL standing — <strong>verified by execution</strong> on .NET 10.0.11, not
    /// read off the documentation — and that is a boundary rather than a hole. CommonMark breaks a line on
    /// LF, CR or CRLF and nothing else, so neither character can open the block an ATX heading needs, and
    /// NUL must additionally be replaced with U+FFFD before parsing. They reach the comment intact, and
    /// the comment still opens exactly one heading of its own.
    /// </summary>
    [Theory]
    [InlineData(0x000B, "VT")]
    [InlineData(0x0000, "NUL")]
    public void Render_LeavesTheTwoSeparatorsThatOpenNoBlockAlone(int codePoint, string name)
    {
        var separator = char.ConvertFromUtf32(codePoint);
        var forgery = Forgery.Replace("\n", separator, StringComparison.Ordinal);

        var report = Report(new DriftedPage(
            "domains/loans.md",
            $"Loans{forgery}",
            $"@alice{forgery}",
            [new SourceMatch("src/Loans/**", ["src/Loans/A.cs"])]));

        var comment = DriftComment.Render(report);

        Lines(comment).Where(line => line.StartsWith('#')).ShouldBe(
            ["### 📄 Documentation drift"],
            customMessage: $"{name} opened a block CommonMark says it cannot open.");

        // Carried through rather than stripped: this documents where the neutralization stops, so a later
        // reader who widens or narrows it is doing so against a pinned answer instead of a guess.
        comment.ShouldContain(separator, Case.Sensitive, $"{name} was dropped rather than left alone.");
    }

    /// <summary>
    /// A wiki page <strong>filename</strong> carrying a line break, through the real
    /// <c>WikiTree.Load</c> → <c>DriftPlanner.Plan</c> → <c>DriftComment.Render</c> chain. This is the
    /// vector three reviews walked past, because the argument that retired it was about the wrong
    /// producer: git C-quotes a newline-bearing path in <c>diff --name-only</c>, but a wiki page path is
    /// never git output — <c>WikiTree.InScope</c> reads it off the working tree
    /// (<c>WikiTree.cs:255-258</c>), so the byte arrives literal, and a code span cannot hold it.
    /// </summary>
    /// <remarks>
    /// The forged heading lands at column 0 <em>before</em> inline parsing ever considers the code span:
    /// the newline closes the list, the <c>### No drift detected</c> that follows is a fresh ATX heading
    /// in the bot's own comment, and the reviewer reads a verdict the tool never reached. It is
    /// committable and it survives a clone, so no CI-only guard helps.
    /// </remarks>
    [Fact]
    public void Render_NeutralizesAPageFilenameThatForgesAHeading()
    {
        const string windows = "A line break is not a legal filename byte on Windows; the vector needs a "
            + "filesystem that allows one, which is every platform this suite's CI runs on.";

        Assert.SkipWhen(OperatingSystem.IsWindows(), windows);

        const string fileName = "loans\n\n### No drift detected\n\nNothing to review here.\n\n.md";

        var comment = Rendered(fileName, OrdinaryPage);

        // The whole claim, and the reason this is a Critical: an ATX heading is a `#` at the start of a
        // line, and this comment opens exactly one heading of its own.
        Lines(comment).Where(line => line.StartsWith('#')).ShouldBe(["### 📄 Documentation drift"]);

        // Not merely harmless: the path is still there, whole, still the reviewer's data.
        Spans(comment).ShouldContain(
            $"domains/{fileName}".ReplaceLineEndings(" "),
            "The path did not arrive as the content of exactly one code span.");
    }

    /// <summary>
    /// The same filename vector spelled in <strong>bare carriage returns</strong>, which is the one
    /// spelling a <c>Code</c> that only knew about <c>\n</c> would hand straight back. CommonMark breaks a
    /// line on a lone CR as readily as on an LF, and a CR is a legal filename byte wherever an LF is, so
    /// <c>domains/loans\r\r### No drift detected\r\r.md</c> is a committable page that forges an ATX
    /// heading at column 0.
    /// </summary>
    /// <remarks>
    /// A sibling of the LF case rather than a theory case with it, for the reason the backtick case is
    /// kept separate: what this pins is not "a filename is neutralized" but "the neutralization is the
    /// runtime's whole line-ending set and not the one separator the fixture happened to use". The
    /// renderer-level proof for the other five separators is
    /// <see cref="Render_NeutralizesEveryLineSeparatorAnUntrustedStringCanCarry"/>; this one is here
    /// because the CR arrives through the real <c>WikiTree</c> → <c>DriftPlanner</c> → <c>Render</c> chain
    /// off a real file on disk.
    /// </remarks>
    [Fact]
    public void Render_NeutralizesAPageFilenameWhoseLineBreaksAreBareCarriageReturns()
    {
        const string windows = "A carriage return is not a legal filename byte on Windows; the vector "
            + "needs a filesystem that allows one, which is every platform this suite's CI runs on.";

        Assert.SkipWhen(OperatingSystem.IsWindows(), windows);

        const string fileName = "loans\r\r### No drift detected\r\r.md";

        var comment = Rendered(fileName, OrdinaryPage);

        Lines(comment).Where(line => line.StartsWith('#')).ShouldBe(["### 📄 Documentation drift"]);
        Spans(comment).ShouldContain(
            $"domains/{fileName}".ReplaceLineEndings(" "),
            "The path did not arrive as the content of exactly one code span.");
    }

    /// <summary>
    /// The same filename vector without a line break at all: one <strong>backtick</strong> closes the
    /// code span early and everything after it on the line is live markdown —
    /// <c>`domains/loans`&lt;details&gt;&lt;summary&gt;Resolved`.md`</c>. GitHub's sanitizer allowlists
    /// <c>&lt;details&gt;</c>, so an unclosed one collapses the rest of the comment behind a triangle the
    /// attacker labelled, and the reviewer sees a report that looks resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept separate from the line-break case rather than folded into a theory, because the two fail for
    /// different reasons and a fix can close one and leave the other: collapsing line endings does
    /// nothing about a backtick, and a longer fence does nothing about a newline. Both are needed, and a
    /// theory would let one assertion cover for the other's absence.
    /// </para>
    /// <para>
    /// It carries no <c>Assert.SkipWhen(OperatingSystem.IsWindows())</c>, and that is deliberate rather
    /// than an oversight: the two line-break cases beside it need one because Windows rejects a control
    /// byte in a filename, but a backtick is an ordinary printable character and legal there. This vector
    /// is reachable on every platform, so it is checked on every platform.
    /// </para>
    /// </remarks>
    [Fact]
    public void Render_NeutralizesAPageFilenameThatClosesItsCodeSpanEarly()
    {
        const string fileName = $"loans{BacktickPayload}.md";

        var comment = Rendered(fileName, OrdinaryPage);

        // The whole path is one code span's content, so the `<details>` inside it is literal text rather
        // than a tag. Asserted this way rather than as `ShouldNotContain("<details")`, which would be
        // false for a *correct* render too: a code span keeps the bytes, it just stops them meaning
        // anything.
        Spans(comment).ShouldContain(
            $"domains/{fileName}",
            "The backtick closed the code span and the raw HTML went live.");
    }

    /// <summary>
    /// W1, and the backtick half of the same class: the containment argument the renderer leaned on for
    /// <c>sources</c> globs does not hold. "A pattern that could forge a block is a pattern that matched
    /// nothing and is therefore absent" was false in two independent ways — trimming lets a
    /// line-break-bearing glob match, and a glob naming a directory whose name carries a backtick matches
    /// whatever the author called that directory. Planned through the real matcher rather than asserted
    /// about it, so a change to <c>NormalizePattern</c> that closed either half would show up here.
    /// </summary>
    [Fact]
    public void Render_NeutralizesASourcePatternTheMatcherLetThrough()
    {
        var backtickFile = $"src/Loans/{BacktickPayload}/B.cs";
        var report = Planned("loans.md", CraftedSourcePatterns, "src/Loans/A.cs", backtickFile);
        var patterns = report.Pages[0].Matches.Select(match => match.Pattern).ToList();

        // The half that makes these vectors rather than hypotheses: both crafted globs really did match,
        // and the planner really does hand back the raw spelling of each.
        patterns.ShouldBe(["\nsrc/Loans/**", $"src/Loans/{BacktickPayload}/**"]);

        var comment = DriftComment.Render(report);
        var spans = Spans(comment);

        Lines(comment).Where(line => line.StartsWith('#')).ShouldBe(["### 📄 Documentation drift"]);
        spans.ShouldContain(" src/Loans/**", "The line break carried a glob onto a second line.");
        spans.ShouldContain(
            $"src/Loans/{BacktickPayload}/**",
            "The glob's backtick closed its code span and the raw HTML went live.");
        spans.ShouldContain(
            backtickFile,
            "The matched file's backtick closed its code span and the raw HTML went live.");
    }

    /// <summary>
    /// The exemption disclosure renders two more consumer strings in code spans — the exempted path and
    /// the <c>_meta/drift-ignore</c> glob that claimed it — and both come through the real
    /// <see cref="DriftExemptions.Parse"/>. The glob has to name the backtick literally to match, which
    /// is the whole trick: the PR author writes the file, the line that exempts it, and the code change,
    /// in one push.
    /// </summary>
    [Fact]
    public void Render_NeutralizesAnExemptedPathAndItsPatternThatCloseTheirCodeSpans()
    {
        var file = $"vendor/lib{BacktickPayload}.js";
        var pattern = $"vendor/lib{BacktickPayload}.*";
        var report = DriftPlanner.Plan(
            "abc1234",
            "def5678",
            [file],
            [],
            DriftExemptions.Parse($"{pattern} # vendored"));

        report.Exempted.ShouldBe([new ExemptedChange(file, pattern, "vendored")]);

        var spans = Spans(DriftComment.Render(report));

        spans.ShouldContain(file, "The exempted path escaped its code span.");
        spans.ShouldContain(pattern, "The exempting glob escaped its code span.");
    }

    /// <summary>
    /// The last inconsistency the enumeration turned up: the two revisions are rendered <em>inside</em>
    /// code spans by the provenance line, and were being treated as prose. A backslash escape does
    /// nothing inside a code span — CommonMark gives code spans no escape mechanism at all — so a
    /// backtick in <c>--baseline</c>, or in the <c>baselineSha</c> of the committed and hand-editable
    /// <c>_meta/state.json</c>, closed the span and left the rest of the revision standing in the comment
    /// as <c>abc\</c> and a stray backtick.
    /// </summary>
    /// <remarks>
    /// Never a raw-HTML route, because <c>Escape</c> backslashed the <c>&lt;</c> before it got out — this
    /// was the one value in the enumeration whose two treatments were both defensible and neither was
    /// right. It renders through <c>Code</c> now, like every other value in a code span, which also stops
    /// an ordinary revision printing with backslashes in it.
    /// </remarks>
    [Fact]
    public void Render_FencesTheRevisionsItPrintsAsCode()
    {
        var report = Report(Page()) with { Baseline = $"abc{BacktickPayload}", Head = "def5678" };
        var spans = Spans(DriftComment.Render(report));

        spans.ShouldContain($"abc{BacktickPayload}", "The baseline escaped the code span the line puts it in.");
        spans.ShouldContain("def5678");
    }

    /// <summary>
    /// The edge case a longer fence alone does not survive: a value whose own first and last bytes are
    /// backticks. A backtick string is <em>maximal</em>, so a fence written flush against one fuses with
    /// it — <c>``</c> + <c>`weird`</c> opens a run of three that the two-backtick closer can never match,
    /// and the whole line renders as literal backticks with no code span at all. One space at each end
    /// keeps the fence its own string, and CommonMark's strip rule then removes exactly that space, so
    /// the reader sees the value and not the padding.
    /// </summary>
    [Fact]
    public void Render_FencesAValueWhoseOwnEdgesAreBackticks()
    {
        const string file = "`weird`";
        var report = DriftPlanner.Plan("abc1234", "def5678", [file], [], DriftExemptions.Parse("**"));

        report.Exempted.Select(change => change.Path).ShouldBe([file]);

        Spans(DriftComment.Render(report)).ShouldContain(
            file,
            "A value whose edges are backticks fused with its own fence and opened no span at all.");
    }

    /// <summary>
    /// Every string this comment renders that a PR author can write, forged at once — all thirteen. In
    /// render order: the affected page's title, owner, path, matched glob and matched file; the sealed
    /// page's title, path and seal date; the exempted change's path, glob and reason; and the two
    /// revisions the provenance line quotes. Frontmatter (<c>title</c>, <c>owner</c>, <c>sources</c>),
    /// filenames on disk, <c>_meta/state.json</c>, <c>_meta/drift-ignore</c> and the <c>--baseline</c> /
    /// <c>--head</c> arguments are all committed or crafted in the same push as the code change, so every
    /// one of them is the adversary's own bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The occurrence count is the second half of the assertion and the more useful one: neutralizing by
    /// deleting the payload would satisfy "one heading" and quietly drop a reader's data. Every one of the
    /// thirteen values must still arrive, on one line.
    /// </para>
    /// <para>
    /// <strong>The number is the coverage claim, so it must be the whole table.</strong> This test read
    /// seven for three review rounds while <c>DriftComment</c> rendered thirteen, and the six it did not
    /// count are exactly the ones that went into a code span and were argued safe in prose. A value added
    /// to the renderer without a row here is a value nobody is checking.
    /// </para>
    /// </remarks>
    [Fact]
    public void Render_KeepsEveryUntrustedStringItRendersOnOneLine()
    {
        var report = new DriftReport
        {
            Baseline = $"abc1234{Forgery}",
            Head = $"def5678{Forgery}",
            ChangedFileCount = 9,
            PageCount = 4,
            PagesWithSourcesCount = 3,
            Pages =
            [
                new DriftedPage(
                    $"domains/loans{Forgery}.md",
                    $"Loans{Forgery}",
                    $"@alice{Forgery}",
                    [new SourceMatch($"src/Loans/**{Forgery}", [$"src/Loans/A.cs{Forgery}"])]),
            ],
            Sealed =
            [
                new SealedPage(
                    $"domains/rates{Forgery}.md",
                    $"Rates{Forgery}",
                    $"2026-08-19T09:12:44Z{Forgery}"),
            ],
            Exempted =
            [
                new ExemptedChange(
                    $"src/Generated/Api.cs{Forgery}",
                    $"src/Generated/**{Forgery}",
                    $"sweep{Forgery}"),
            ],
        };

        var comment = DriftComment.Render(report);

        Lines(comment).Where(line => line.StartsWith('#')).ShouldBe(["### 📄 Documentation drift"]);

        const string dropped = "A forged payload was dropped rather than neutralized, or one of the "
            + "thirteen values is no longer line-safe.";

        Occurrences(comment, "### No drift detected").ShouldBe(13, dropped);
    }

    /// <summary>
    /// The grouping lands inside the body section and disturbs neither disclosure around it (spec §6.3):
    /// a sealed page is not an affected page, so it is named by <c>WriteSealed</c> and by nothing else —
    /// SC10, at the layer that renders it.
    /// </summary>
    [Fact]
    public void Render_NeverRoutesASealedPageToAnOwner()
    {
        var report = Report(Owned("domains/rates.md", "Rates", "@moberghr/rates")) with
        {
            Sealed = [new SealedPage("domains/loans.md", "Loans", "2026-08-19T09:12:44Z")],
        };

        var comment = DriftComment.Render(report);

        comment.ShouldContain("**Owner:** @moberghr/rates");
        comment.ShouldContain("1 flagged page was held out by their seal");

        // The sealed page is in the disclosure and in no owner group: routing consumes Pages, and it
        // left that list before the renderer saw it.
        Groups(comment).SelectMany(group => group.Paths).ShouldBe(["domains/rates.md"]);
    }

    [Fact]
    public void Render_ReadsSingularForOneFileAndOnePage()
    {
        var page = new DriftedPage("a.md", "A", Owner: null, [new SourceMatch("src/**", ["src/A.cs"])]);
        var comment = DriftComment.Render(Report(page) with { ChangedFileCount = 1 });

        comment.ShouldContain("**1 wiki page**");
        comment.ShouldContain("1 changed file.");
    }

    /// <summary>
    /// The report the real chain produces for a one-page wiki whose page is written to
    /// <c>domains/<paramref name="fileName"/></c> with <paramref name="markdown"/> in it, against
    /// <paramref name="changedFiles"/> (defaulting to the one file <see cref="OrdinaryPage"/>'s glob
    /// claims).
    /// </summary>
    /// <remarks>
    /// <see cref="WikiTree.Load"/> rather than a hand-built <see cref="WikiPage"/>, and that is the point
    /// of the helper: the page path in the report is the one <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
    /// read back off the filesystem (<c>WikiTree.cs:255-258</c>), so a crafted filename reaches the
    /// renderer as its own literal bytes. Every containment argument that retired this class reasoned
    /// about git's output instead, which a page path never is.
    /// </remarks>
    private DriftReport Planned(string fileName, string markdown, params string[] changedFiles)
    {
        var directory = Path.Combine(_wikiRoot, "domains");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), markdown);

        IReadOnlyCollection<string> changed = changedFiles.Length > 0 ? changedFiles : ["src/Loans/A.cs"];

        return DriftPlanner.Plan("abc1234", "def5678", changed, WikiTree.Load(_wikiRoot).Pages);
    }

    /// <summary><see cref="Planned"/>, rendered.</summary>
    private string Rendered(string fileName, string markdown) =>
        DriftComment.Render(Planned(fileName, markdown));

    private static DriftReport Report(params DriftedPage[] pages) => new()
    {
        Baseline = "abc1234",
        Head = "def5678",
        ChangedFileCount = 9,
        PageCount = 4,
        PagesWithSourcesCount = 3,
        Pages = pages,
    };

    private static DriftedPage Page() => new(
        "domains/loans.md",
        "Loans",
        Owner: null,
        [new SourceMatch("src/Loans/**", ["src/Loans/A.cs", "src/Loans/B.cs"])]);

    private static DriftedPage Owned(string path, string title, string owner) => new(
        path,
        title,
        owner,
        [new SourceMatch($"src/{title}/**", [$"src/{title}/A.cs"])]);

    /// <summary>
    /// <paramref name="comment"/> split the way CommonMark itself defines a line boundary: LF, CR or
    /// CRLF, and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than the set <see cref="string.ReplaceLineEndings()"/> collapses. The claim
    /// these lines carry is "the payload did not open a new block", and only these three sequences start
    /// one, so a split on the wider set would report a clean comment for the two characters
    /// <c>ReplaceLineEndings</c> leaves standing (VT U+000B, NUL) whether or not a renderer breaks on
    /// them — which is exactly the question
    /// <see cref="Render_LeavesTheTwoSeparatorsThatOpenNoBlockAlone"/> exists to answer honestly.
    /// </remarks>
    private static List<string> Lines(string comment) =>
        comment.Split('\n').SelectMany(line => line.Split('\r')).ToList();

    /// <summary>The contents of every code span in <paramref name="comment"/>, line by line.</summary>
    /// <remarks>
    /// Line by line because a code span the renderer opened and a line break then carried past the end of
    /// the block is not a code span at all — scanning the whole text at once would pair an opening
    /// backtick with a closing one three blocks away and report a containment that no reader sees.
    /// </remarks>
    private static List<string> Spans(string comment) => [.. Lines(comment).SelectMany(CodeSpans)];

    /// <summary>
    /// The contents of every code span on <paramref name="line"/>, as CommonMark itself resolves them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A parser rather than a string search, because the claim needs one.</strong> "The comment
    /// contains the path" is answered yes by a render where a backtick in that path closed the span three
    /// characters early and the rest of the line went live — which is precisely the vector
    /// <see cref="Render_NeutralizesAPageFilenameThatClosesItsCodeSpanEarly"/> exists to catch.
    /// <c>CodeSpans(line).ShouldContain(path)</c> says something a substring check cannot: the whole
    /// value arrived as the content of exactly one span, so every metacharacter in it is literal text.
    /// </para>
    /// <para>
    /// The three CommonMark rules that matter here (§6.1 code spans): a backtick string is a maximal run
    /// of backticks; a span runs from an opening run to the next run of <em>equal</em> length; and if the
    /// content both begins and ends with a space and is not all spaces, one space is stripped from each
    /// end. An opening run with no equal-length closer is literal text, not a span — so a value that
    /// broke out leaves its bytes off this list rather than on it, which is the direction that fails the
    /// test.
    /// </para>
    /// </remarks>
    private static List<string> CodeSpans(string line)
    {
        var spans = new List<string>();
        var index = 0;

        while (index < line.Length)
        {
            if (line[index] != '`')
            {
                index++;
                continue;
            }

            var fence = BacktickRun(line, index);
            var content = index + fence;
            var closer = NextRunOfLength(line, content, fence);

            if (closer < 0)
            {
                // No closer: this run is ordinary text and a later, shorter run may still open a span.
                index = content;
                continue;
            }

            spans.Add(StripOnePaddingSpace(line[content..closer]));
            index = closer + fence;
        }

        return spans;
    }

    /// <summary>The length of the backtick run starting at <paramref name="at"/>.</summary>
    private static int BacktickRun(string line, int at)
    {
        var end = at;
        while (end < line.Length && line[end] == '`')
        {
            end++;
        }

        return end - at;
    }

    /// <summary>
    /// Where the first backtick run of exactly <paramref name="length"/> starts at or after
    /// <paramref name="from"/>, or -1. Runs of a different length are skipped whole, because a run of
    /// three cannot close a span opened by one.
    /// </summary>
    private static int NextRunOfLength(string line, int from, int length)
    {
        var index = from;

        while (index < line.Length)
        {
            if (line[index] != '`')
            {
                index++;
                continue;
            }

            var run = BacktickRun(line, index);
            if (run == length)
            {
                return index;
            }

            index += run;
        }

        return -1;
    }

    /// <summary>CommonMark's one-space strip, which is what makes the renderer's padding invisible.</summary>
    private static string StripOnePaddingSpace(string content) =>
        content.Length > 1 && content[0] == ' ' && content[^1] == ' ' && content.Trim(' ').Length > 0
            ? content[1..^1]
            : content;

    /// <summary>How many times <paramref name="needle"/> appears in <paramref name="haystack"/>.</summary>
    /// <remarks>
    /// A count rather than a <c>ShouldContain</c>, for the partition claim: "every affected page appears
    /// exactly once" is answered yes by containment for any number above zero, including the two that
    /// would mean a page was routed to two owners.
    /// </remarks>
    private static int Occurrences(string haystack, string needle)
    {
        var found = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            found++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return found;
    }

    /// <summary>
    /// The owner groups as the rendered comment actually spells them: each heading with the page paths
    /// listed under it, read back out of the text.
    /// </summary>
    /// <remarks>
    /// Scoped to the body section — everything between the "touches sources for" lead and the advisory
    /// trailer — so the <c>WriteSealed</c> and <c>WriteExempted</c> disclosures below it, which list
    /// pages in the same bullet shape, cannot be mistaken for a routed page. That scoping is what lets
    /// the partition assertion mean "the grouping listed these", rather than "these strings occur".
    /// </remarks>
    private static List<(string Heading, List<string> Paths)> Groups(string comment)
    {
        const string trailer = "Check whether these pages still describe the code.";

        var groups = new List<(string Heading, List<string> Paths)>();
        var lead = comment.IndexOf("This PR touches sources for", StringComparison.Ordinal);

        if (lead < 0)
        {
            return groups;
        }

        var end = comment.IndexOf(trailer, lead, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(lead, "The body section has no advisory trailer to end at.");

        var lines = comment[lead..end].Split(Environment.NewLine);

        foreach (var line in lines)
        {
            if (line.StartsWith("**", StringComparison.Ordinal))
            {
                groups.Add((line, []));

                continue;
            }

            if (!line.StartsWith("- **", StringComparison.Ordinal))
            {
                continue;
            }

            groups.Count.ShouldBeGreaterThan(0, $"'{line}' is listed under no owner heading at all.");
            groups[^1].Paths.Add(line.Split('`')[1]);
        }

        return groups;
    }
}
