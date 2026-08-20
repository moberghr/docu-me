using System.Text;

namespace DocuMe.Core.Drift;

/// <summary>
/// Renders a <see cref="DriftReport"/> as the markdown block <c>--format github-comment</c> posts on a
/// pull request (PLAN.md §6.4: "This PR touches sources for: …").
/// </summary>
/// <remarks>
/// <para>
/// Pure and clock-free, like <see cref="Dashboard.DashboardPage"/> and for the same reason: a CI job
/// updates one comment in place, so two runs over an unchanged answer must produce byte-identical text.
/// A timestamp in here would make every re-run an edit, and a reviewer would learn to ignore the
/// notification.
/// </para>
/// <para>
/// <strong>It always renders something</strong>, including "nothing drifted". The comment is a slot a
/// bot overwrites, and leaving yesterday's drift warning standing after the PR fixed the docs would be
/// worse than a line saying so.
/// </para>
/// </remarks>
public static class DriftComment
{
    /// <summary>
    /// The hidden marker a CI job finds its own previous comment by, so it edits rather than piles up.
    /// First line of every render.
    /// </summary>
    public const string Marker = "<!-- docume:drift -->";

    /// <summary>
    /// The fixed heading the pages with no <c>owner:</c> group under, always last
    /// (<c>docs/specs/2026-08-20-page-owners.md</c> §3.3).
    /// </summary>
    /// <remarks>
    /// A named bucket rather than an unlabelled remainder, and public so a consumer and this suite pin
    /// the same string: unowned pages are the ones a drift report can name and cannot route, which is
    /// the fact §2 asks be said out loud rather than left to be inferred from a group heading that
    /// happens to be missing.
    /// </remarks>
    public const string UnownedHeading = "**No owner**";

    /// <summary>
    /// How many matched files one pattern lists before the rest become a count. A PR comment is read at
    /// a glance; the full list is what <c>--format json</c> is for, and the overflow is stated rather
    /// than silently dropped.
    /// </summary>
    private const int MaxFilesPerPattern = 5;

    /// <summary>What an owned group's heading opens with, before the owner's own bytes.</summary>
    private const string OwnerHeading = "**Owner:**";

    /// <summary>The comment body for <paramref name="report"/>, ending in a newline.</summary>
    public static string Render(DriftReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();
        text.AppendLine(Marker);
        text.AppendLine("### 📄 Documentation drift");
        text.AppendLine();
        WriteBody(text, report);
        WriteSealed(text, report);
        WriteExempted(text, report);
        text.AppendLine();
        text.AppendLine(Provenance(report));

        return text.ToString();
    }

    /// <summary>
    /// The seal disclosure (spec §3.4), shaped like <see cref="WriteExempted"/> below it and there for
    /// the same reason: these pages' sources <em>were</em> touched in the range, and the verdict above
    /// says nothing drifted because their bytes turned out to be the ones each page was published
    /// against. A comment that held them out silently would be a machine narrowing a reviewer's answer
    /// without telling the reviewer, which is the one thing the two declared exemptions are careful never
    /// to do.
    /// </summary>
    /// <remarks>
    /// The date travels with each page because the seal is only as current as the publish that wrote it:
    /// "sealed this morning" and "sealed in March" are the same verdict and not the same reassurance.
    /// Capped at <see cref="MaxFilesPerPattern"/> with the overflow stated, the way the exemptions below
    /// are capped: a PR comment is read at a glance, the count above the list is the whole disclosure,
    /// and the full list is what <c>--format json</c> and the table are for.
    /// </remarks>
    private static void WriteSealed(StringBuilder text, DriftReport report)
    {
        if (report.Sealed.Count == 0)
        {
            return;
        }

        var pages = report.Sealed.Count == 1 ? "page was" : "pages were";
        text.AppendLine();
        text.AppendLine(
            $"{report.Sealed.Count} flagged {pages} held out by their seal — the sources are "
            + "byte-identical to the bytes the published body was generated from:");
        text.AppendLine();

        foreach (var page in report.Sealed.Take(MaxFilesPerPattern))
        {
            // Escaped like the title beside it, though it reads like a machine's timestamp. `_meta/state.json`
            // is a committed, hand-editable file (PLAN.md §5.3), so the PR author who crafts the frontmatter
            // can craft `verdict.sealedAt` in the same push. An ISO-8601 date passes through Escape byte for
            // byte, so the disclosure a reader actually gets is unchanged.
            var when = page.SealedAt is { Length: > 0 } at ? $" (sealed {Escape(at)})" : string.Empty;
            text.AppendLine($"- **{Escape(page.Title)}** — {Code(page.Path)}{when}");
        }

        var hidden = report.Sealed.Count - MaxFilesPerPattern;
        if (hidden > 0)
        {
            text.AppendLine($"- and {hidden} more");
        }
    }

    /// <summary>
    /// The exemption disclosure (§6.4): a verdict whose inputs `_meta/drift-ignore` narrowed must say
    /// so in the same comment, or "nothing drifted" reads as a clean diff when the truth is "nothing
    /// drifted that the list let through".
    /// </summary>
    private static void WriteExempted(StringBuilder text, DriftReport report)
    {
        if (report.Exempted.Count == 0)
        {
            return;
        }

        var files = report.Exempted.Count == 1 ? "changed file was" : "changed files were";
        text.AppendLine();
        text.AppendLine($"{report.Exempted.Count} {files} exempted by `_meta/drift-ignore`:");
        text.AppendLine();

        foreach (var exempted in report.Exempted.Take(MaxFilesPerPattern))
        {
            var reason = exempted.Reason is { Length: > 0 } why ? $" — {Escape(why)}" : string.Empty;
            text.AppendLine($"- {Code(exempted.Path)} ({Code(exempted.Pattern)}{reason})");
        }

        var hidden = report.Exempted.Count - MaxFilesPerPattern;
        if (hidden > 0)
        {
            text.AppendLine($"- and {hidden} more");
        }
    }

    private static void WriteBody(StringBuilder text, DriftReport report)
    {
        if (report.SourcesUndeclared)
        {
            text.AppendLine(
                "No page in this wiki declares a `sources:` glob, so drift cannot be detected. Add "
                + "`sources:` to a page's frontmatter to link it to the code it documents.");

            return;
        }

        if (!report.HasDrift)
        {
            // The seal is the other way this verdict comes out quiet, and it is not the same sentence:
            // the section WriteSealed adds immediately below names pages whose sources this PR DID
            // touch, so a comment opening with "no documented sources were touched" would be
            // contradicted by its own disclosure — the one thing a disclosure must never be.
            if (report.Sealed.Count > 0)
            {
                var held = report.Sealed.Count == 1
                    ? "the one page they belong to is byte-identical to the bytes its published body was "
                        + "generated from"
                    : $"all {report.Sealed.Count} pages they belong to are byte-identical to the bytes "
                        + "their published bodies were generated from";

                text.AppendLine($"This PR touches documented sources, but {held}. Nothing to review here.");

                return;
            }

            text.AppendLine("No documented sources were touched. Nothing to review here.");

            return;
        }

        var pages = report.AffectedCount == 1 ? "page" : "pages";
        text.AppendLine(
            $"This PR touches sources for **{report.AffectedCount} wiki {pages}** of "
            + $"{report.PagesWithSourcesCount} with declared sources:");

        WriteGroups(text, report);

        text.AppendLine();
        text.AppendLine(
            "Check whether these pages still describe the code. This is advisory: `docume drift` never "
            + "edits a page.");
    }

    /// <summary>
    /// The affected pages under the owner each one declares (spec §3.3) — what turns a notice pinned to
    /// a wall into a mention that notifies somebody. Inside <see cref="WriteBody"/> on purpose: the
    /// sealed and exempt disclosures around it keep their place and their order, which is the one thing
    /// §6.3 of the spec asks of a change to an output shape a scaffolded workflow already consumes.
    /// </summary>
    private static void WriteGroups(StringBuilder text, DriftReport report)
    {
        foreach (var group in Grouped(report))
        {
            text.AppendLine();
            text.AppendLine(Heading(group));
            text.AppendLine();

            foreach (var page in group.Pages)
            {
                text.AppendLine($"- **{Escape(page.Title)}** — {Code(page.Path)}");

                foreach (var match in page.Matches)
                {
                    text.AppendLine($"  - {Code(match.Pattern)} → {Files(match)}");
                }
            }
        }
    }

    /// <summary>
    /// <see cref="DriftReport.Pages"/> partitioned by owner, ordinal by the owner string, unowned last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A partition, not a filter</strong> (spec §3.3). Every affected page lands in exactly one
    /// group and the group sizes sum to <see cref="DriftReport.AffectedCount"/>: a grouping bug that
    /// dropped a page would hide exactly the drift this feature exists to route, and would look like a
    /// quiet comment rather than like a failure. <c>DriftCommentTests</c> asserts the sum rather than
    /// trusting this sentence.
    /// </para>
    /// <para>
    /// <strong>The order is a function of the owners alone.</strong> A bot rewrites one comment in place
    /// on every push (<see cref="Marker"/>), so an order that came out of a dictionary or a hash seed
    /// would show an edit on every run with no change in the answer. Ordinal because a culture-aware
    /// comparison would sort the same handles differently on two machines, and
    /// <see cref="Enumerable.OrderBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/> is
    /// stable, so pages keep their path order inside a group.
    /// </para>
    /// <para>
    /// "Unowned" is <c>null</c> and only <c>null</c> — <see cref="Markdown.FrontmatterParser"/> has
    /// already collapsed a blank <c>owner:</c> — so this grouping and
    /// <see cref="DriftReport.UnownedCount"/> describe one partition rather than two that can disagree.
    /// </para>
    /// </remarks>
    private static List<OwnerGroup> Grouped(DriftReport report) => report.Pages
        .GroupBy(page => page.Owner, StringComparer.Ordinal)
        .OrderBy(group => group.Key is null)
        .ThenBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new OwnerGroup(group.Key, group.ToList()))
        .ToList();

    /// <summary>The line one group sits under.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The owner is not escaped</strong>, unlike the titles beneath it, and that is the whole
    /// point of the value: <c>@moberghr/lending</c> has to arrive at the forge as those exact bytes for
    /// the mention to notify anybody, and escaping the <c>_</c> in a handle like <c>@my_org/team</c> —
    /// which is what <see cref="Escape"/> would do — leaves a comment that renders almost right and
    /// pings nobody. A narrow set is neutralized instead, and the reason is a threat model rather than
    /// tidiness.
    /// </para>
    /// <para>
    /// <strong>On a drift comment the PR author is the adversary.</strong> They have push access by
    /// construction: they change the code and commit the crafted <c>owner:</c> in the same push, and the
    /// scaffolded workflow (<c>templates/workflows/docs-drift-pr.yml</c>) posts the result under the
    /// bot's identity as a sticky comment a reviewer trusts. Its fork guard does not touch this, and a
    /// consumer's own workflow need not carry one.
    /// </para>
    /// <para>
    /// <strong>The rule is not a count, and reading it as one is how the link below was missed.</strong>
    /// This method neutralizes a character when both halves hold: no forge handle can contain it,
    /// <em>and</em> leaving it alone lets the owner say something the tool never said — by ending this
    /// block, or by turning the rest of the line into a construct instead of text. Everything else is
    /// left exactly as written, whatever CommonMark thinks of it, and <c>_</c> and <c>*</c> are why:
    /// they are ordinary bytes in <c>@my_org/team</c>, <c>_platform_</c> and <c>*docs*</c>, so the full
    /// <see cref="Escape"/> can never be reached for here. The set is expected to grow when a construct
    /// is found that this list does not name; what it must never do is grow onto a byte a handle
    /// carries. Three constructs today:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <strong>A line ending — it leaves the line.</strong> <c>Owner</c> is a YAML scalar, and YAML
    /// carries a newline inside one two ways: a <c>\n</c> escape in a double-quoted scalar, and a
    /// <c>|</c> block scalar. <see cref="Markdown.FrontmatterParser"/> only collapses blank values, so a
    /// newline arrives here intact and everything after it is a fresh CommonMark block: an ATX heading
    /// forging a verdict ("### No drift detected"), or a raw HTML block.
    /// </description></item>
    /// <item><description>
    /// <strong><c>&lt;</c> — it stays on the line and stops being text.</strong> No newline needed.
    /// CommonMark passes inline raw HTML through unchanged and GitHub's sanitizer allows
    /// <c>&lt;details&gt;</c>/<c>&lt;summary&gt;</c>, so one unclosed <c>&lt;details&gt;</c> collapses
    /// the page list under this heading <em>and</em> every later owner's group behind a triangle the
    /// attacker labelled.
    /// </description></item>
    /// <item><description>
    /// <strong><c>[</c> and <c>]</c> — they stay on the line and stop being text.</strong> Also no
    /// newline, and no HTML: <c>owner: "[Resolved — see the fix](https://evil.example/login)"</c>
    /// renders an arbitrarily-labelled clickable link pointing anywhere, inside a comment carrying the
    /// bot's identity, and <c>![…](…)</c> is the same construct with an <c>!</c> in front that loads its
    /// target without a click. The pair travels together even though neutralizing <c>[</c> alone would
    /// close the link today: a link label is one construct with two ends, and pinning only the opener
    /// would make this line's safety depend on no neighbouring sink ever emitting a bare <c>[</c> into
    /// the same block — the containment shape this file gave up (see <see cref="Escape"/>).
    /// </description></item>
    /// </list>
    /// <para>
    /// <strong>Numeric character references rather than backslash escapes, and not only for symmetry
    /// with <c>&amp;lt;</c>.</strong> A backslash escape can be defeated by a backslash the author
    /// supplies. This method deliberately leaves <c>\</c> alone — no handle needs one — so writing
    /// <c>\[label](url)</c> would leave here as <c>\\[label](url)</c>: an escaped backslash rendering a
    /// literal <c>\</c>, followed by a live link. <c>&amp;#91;</c> has no such predecessor. CommonMark
    /// resolves it to a literal <c>[</c> that cannot open anything (§2.5: a character reference is never
    /// a structural character), and an author who prefixes it with a backslash only escapes the
    /// <c>&amp;</c> and gets <c>&amp;#91;</c> printed at the reader — ugly in a case no handle reaches,
    /// inert in every case. <see cref="Escape"/> has no such trap because it escapes <c>\</c> first.
    /// </para>
    /// <para>
    /// <strong>What is deliberately left standing.</strong> Every byte a handle can carry: <c>_</c>,
    /// <c>*</c>, <c>@</c>, <c>/</c>, <c>.</c>, <c>-</c>, spaces, and the rest. A bare URL in an owner
    /// still becomes clickable, because GitHub autolinks literal <c>https://</c> and <c>www.</c> runs —
    /// left alone knowingly, since an autolink's label <em>is</em> its href and so cannot claim to lead
    /// somewhere it does not, and breaking one would mean touching <c>/</c> or <c>.</c>, which
    /// <c>@moberghr/lending</c> and <c>mirko.budimir@moberg.hr</c> are made of. §3.1's verbatim claim
    /// survives all three constructs whole, because no handle on any forge contains a line break, a
    /// <c>&lt;</c> or a bracket: <c>@my_org/team</c>, <c>_platform_</c> and <c>*docs*</c> still arrive
    /// as those exact bytes and still mention. <c>DriftCommentTests</c> pins both halves, feeding the
    /// crafted values through the real parser.
    /// </para>
    /// <para>
    /// At the renderer rather than in the parser, because this is a markdown problem and not a data
    /// problem: SC6 says the parsed value is the frontmatter's own, and the same value also reaches
    /// <c>--format json</c> (where a newline is data) and the dashboard (which HTML-escapes it at
    /// <see cref="Dashboard.DashboardPage"/>). Each sink neutralizes what its own syntax makes dangerous.
    /// </para>
    /// <para>
    /// The unowned bucket carries its count for the reason <see cref="DriftReport.UnownedCount"/> is a
    /// count: a reader needs the proportion, and "3 of 3 pages here have no owner" is a different
    /// report from "1 of 40 does".
    /// </para>
    /// </remarks>
    private static string Heading(OwnerGroup group)
    {
        if (group.Owner is { } owner)
        {
            var line = owner
                .ReplaceLineEndings(" ")
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace("[", "&#91;", StringComparison.Ordinal)
                .Replace("]", "&#93;", StringComparison.Ordinal);

            return $"{OwnerHeading} {line}";
        }

        var pages = group.Pages.Count == 1 ? "page declares" : "pages declare";

        return $"{UnownedHeading} — {group.Pages.Count} {pages} no `owner:`, so this drift is addressed "
            + "to nobody.";
    }

    private static string Files(SourceMatch match)
    {
        var shown = match.Files.Take(MaxFilesPerPattern).Select(Code);
        var listed = string.Join(", ", shown);
        var hidden = match.Files.Count - MaxFilesPerPattern;

        return hidden > 0 ? $"{listed} and {hidden} more" : listed;
    }

    private static string Provenance(DriftReport report)
    {
        var files = report.ChangedFileCount == 1 ? "changed file" : "changed files";
        var exempted = report.Exempted.Count > 0 ? $", {report.Exempted.Count} exempted" : string.Empty;

        // The seal disclosure in the one line a reader takes in whether or not they scroll, next to the
        // other two narrowings (WriteSealed carries the pages themselves).
        var held = report.Sealed.Count > 0 ? $", {report.Sealed.Count} sealed" : string.Empty;

        // The commit disclosure (§6.4), in the same breath as the path one: a quiet verdict over a
        // narrowed diff must say the diff was narrowed.
        var commits = report.IgnoredCommitCount == 1 ? "commit" : "commits";
        var ignored = report.IgnoredCommitCount > 0
            ? $", {report.IgnoredCommitCount} {commits} ignored"
            : string.Empty;

        // Code rather than Escape, though a revision reads like a machine's sha: this line prints both
        // revisions AS CODE, and CommonMark gives a code span no escape mechanism at all — a backslash in
        // there is a backslash. `--baseline`, and the `baselineSha` it falls back to in the committed and
        // hand-editable `_meta/state.json`, are the PR author's bytes like everything else here.
        return $"<sub>`docume drift` — baseline {Code(report.Baseline)} → head "
            + $"{Code(report.Head)}, {report.ChangedFileCount} {files}{exempted}{held}{ignored}.</sub>";
    }

    /// <summary>
    /// Makes one untrusted string safe to drop into this comment <strong>as prose</strong>: it stays on
    /// the line the caller put it on, and it stays text rather than becoming formatting. Every value here
    /// is a consumer's own — a title off frontmatter or an H1, a <c>drift-ignore</c> reason, a seal date —
    /// and on a drift comment the author of those bytes is the adversary (see <see cref="Heading"/> for
    /// the threat model in full: they push the crafted value and the code in one PR, and a scaffolded
    /// workflow posts the result under a bot's identity as a sticky comment a reviewer trusts).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two neutralizations, in this order, and each answers a different attack.</strong>
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <strong>Line endings become spaces.</strong> This is the block-forgery defense and it is the whole
    /// reason this method is not just a metacharacter loop. A YAML scalar carries a newline two ways (a
    /// <c>\n</c> escape in a double-quoted scalar, a <c>|</c> block scalar) and
    /// <see cref="Markdown.FrontmatterParser"/> only collapses blank values, so one arrives here intact
    /// and everything after it is a <em>fresh CommonMark block</em>: a
    /// <c>title: |</c> of three lines forges its own <c>### No drift detected</c> heading inside the
    /// bot's comment, and escaping <c>#</c> would not help, because the danger is the line break rather
    /// than the character after it. <see cref="string.ReplaceLineEndings()"/> rather than a
    /// <c>\n</c> replace: on .NET 10 it collapses LF, CR, CRLF, FF, NEL (U+0085), LS (U+2028) and PS
    /// (U+2029) — verified by execution, and pinned by <c>DriftCommentTests</c> one case per separator,
    /// because a fix that knew only about <c>\n</c> would leave a lone CR re-opening the same forgery
    /// with the suite still green. VT (U+000B) and NUL survive it, and correctly so: CommonMark breaks a
    /// line on LF, CR or CRLF only, so neither can open a block, and NUL must be replaced with U+FFFD
    /// before parsing.
    /// </description></item>
    /// <item><description>
    /// <strong>Markdown metacharacters get a backslash.</strong> This is the ordinary-title defense and
    /// also the raw-HTML one: <c>Rates_and_Fees</c> would silently italicize and <c>&lt;Draft&gt;</c>
    /// would vanish into a tag, and <c>\&lt;</c> renders a literal <c>&lt;</c> that opens nothing, which
    /// is why the <c>&lt;details&gt;</c> collapse never reproduced through a title.
    /// </description></item>
    /// </list>
    /// <para>
    /// <strong>The values a caller prints as code — every path and glob, and both revisions — go through
    /// <see cref="Code"/> instead, and for the same reason rather than a weaker one.</strong> They are
    /// equally the PR author's bytes; what differs is only the syntax they land in, so each is neutralized
    /// against the syntax that renders it, and this method is the wrong one for a code span twice over: a
    /// backslash is not an escape in there (CommonMark gives code spans no escape mechanism), so the
    /// backtick it precedes still closes the span, and every other backslash it writes is visible to the
    /// reader. Earlier rounds
    /// argued those call sites safe from their producers instead — that a pattern only reaches a report
    /// by matching a real path, and that git C-quotes a path carrying a newline — and the argument was
    /// wrong on both halves. <c>DriftPlanner.NormalizePattern</c> trims before matching, so a glob whose
    /// raw spelling opens with a line break matches exactly what its trimmed spelling does and is then
    /// recorded raw; wiki page paths are not git output at all but
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> output
    /// (<c>Markdown/WikiTree.cs:255-258</c>), so a newline in a filename arrives literal; and git quotes
    /// control bytes but never a backtick, which closes a code span all by itself. Every one of those is
    /// a fact about somebody else's code, which is the deeper problem with the shape: a containment
    /// argument makes the safety of this file depend on three others not changing.
    /// </para>
    /// <para>
    /// <strong>What breaks if this is narrowed.</strong> Dropping the line-ending step restores the
    /// heading forgery through every caller at once, and the metacharacter loop cannot substitute for it.
    /// Narrowing it to <c>\n</c> restores it through a lone CR. Dropping <c>&lt;</c> from the character
    /// set restores the raw-HTML route. Widening it is safe in the other direction but costs: it must
    /// never be applied to the owner, which <see cref="Heading"/> neutralizes on its own narrow terms
    /// precisely so <c>@my_org/team</c> and <c>_platform_</c> still mention somebody.
    /// </para>
    /// </remarks>
    private static string Escape(string text)
    {
        // The line-ending pass is a whole-string operation rather than a case in the loop below: a line
        // ending is not one character (CRLF is two), and ReplaceLineEndings is the one place in the BCL
        // that carries the runtime's own idea of the complete set.
        var line = text.ReplaceLineEndings(" ");
        var escaped = new StringBuilder(line.Length);
        foreach (var character in line)
        {
            if (character is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Wraps one untrusted string in a code span that string cannot break out of: the value renders as
    /// its own literal bytes, on the line the caller put it on, with no backslashes in front of the
    /// punctuation a path is made of. Every path and glob this comment prints comes through here — page
    /// paths, matched files, <c>sources</c> globs, exempted paths and the <c>drift-ignore</c> globs that
    /// claimed them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why not <see cref="Escape"/>.</strong> It would work, and it would put visible backslashes
    /// inside the span: a code span renders its content literally, so <c>\</c> is a backslash there and
    /// <c>src\_gen/\*\*</c> is what a reviewer would be asked to find in their own frontmatter. The
    /// CommonMark-correct answer for "render these bytes as code" is a fence, and it costs nothing.
    /// </para>
    /// <para>
    /// <strong>Two neutralizations, in this order, and each answers a different attack</strong> —
    /// deliberately parallel to <see cref="Escape"/>, because the threat model is identical and only the
    /// syntax differs.
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <strong>Line endings become spaces</strong>, exactly as in <see cref="Escape"/> and for exactly
    /// that reason: a code span is an <em>inline</em> construct, so a line ending inside one does not
    /// merely widen the span, it ends the block. A wiki page filename carrying <c>\n\n### No drift
    /// detected</c> closes the list and opens a forged ATX heading at column 0, and inline parsing never
    /// gets as far as the backticks. This is reachable and committable: a page path is read off the
    /// working tree by <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
    /// (<c>Markdown/WikiTree.cs:255-258</c>), never out of git, so git's C-quoting of control bytes
    /// contains nothing here. <see cref="string.ReplaceLineEndings()"/> for the reason
    /// <see cref="Escape"/> gives — it is the one place in the BCL carrying the runtime's own idea of the
    /// complete set, and a <c>\n</c>-only replace would hand the forgery back through a lone CR.
    /// </description></item>
    /// <item><description>
    /// <strong>The fence is longer than the longest backtick run in the content.</strong> A code span is
    /// delimited by backtick <em>strings</em> of equal length (CommonMark §6.1), so a single backtick in
    /// a filename closes a single-backtick span early and the rest of the line goes live —
    /// <c>`domains/loans`&lt;details&gt;&lt;summary&gt;Resolved`.md`</c> is a path, a raw HTML tag GitHub
    /// allowlists, and a second span. No line ending needed, and git quotes a backtick in a path not at
    /// all: it is an ordinary printable byte, legal in a filename on every platform this runs on. An
    /// n+1 fence cannot be closed early because no run of n+1 exists inside the content.
    /// </description></item>
    /// </list>
    /// <para>
    /// <strong>The padding is what makes the fence hold at the edges.</strong> A backtick string is
    /// maximal, so content that starts or ends with a backtick would fuse with the fence and lengthen it.
    /// CommonMark strips one space from each end of a code span whose content both begins and ends with a
    /// space (and is not all spaces), so a space on <em>both</em> sides is invisible to the reader and
    /// keeps the fence its own string. Padding also preserves a path whose own name begins or ends with a
    /// space, which the strip rule would otherwise eat.
    /// </para>
    /// <para>
    /// <strong>What this does not do, on purpose.</strong> It does not touch <c>&lt;</c>: inside a
    /// closed code span every byte is literal, so the raw-HTML route <see cref="Escape"/> defends against
    /// is already shut by the span itself, and escaping it would put a literal <c>&amp;lt;</c> in front of
    /// a reviewer. It leaves VT and NUL standing, the same boundary and the same reasoning as
    /// <see cref="Escape"/>. It is not applied to the owner, which is not code and is neutralized by
    /// <see cref="Heading"/> on its own narrow terms.
    /// </para>
    /// </remarks>
    private static string Code(string text)
    {
        var line = text.ReplaceLineEndings(" ");

        var run = 0;
        var longest = 0;
        foreach (var character in line)
        {
            run = character == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        // An empty value pads too: `` on its own is a two-backtick string and no span at all. Nothing
        // reaching this method can be empty today (every producer filters blanks), which is exactly why
        // the guard is here rather than in a comment claiming it cannot happen.
        var edge = line.Length == 0 || line[0] is '`' or ' ' || line[^1] is '`' or ' ';
        var pad = edge ? " " : string.Empty;
        var fence = new string('`', longest + 1);

        return $"{fence}{pad}{line}{pad}{fence}";
    }

    /// <summary>One owner and the affected pages that declare them — <c>null</c> for the unowned bucket.</summary>
    private sealed record OwnerGroup(string? Owner, IReadOnlyList<DriftedPage> Pages);
}
