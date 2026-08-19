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
    /// How many matched files one pattern lists before the rest become a count. A PR comment is read at
    /// a glance; the full list is what <c>--format json</c> is for, and the overflow is stated rather
    /// than silently dropped.
    /// </summary>
    private const int MaxFilesPerPattern = 5;

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
            var when = page.SealedAt is { Length: > 0 } at ? $" (sealed {at})" : string.Empty;
            text.AppendLine($"- **{Escape(page.Title)}** — `{page.Path}`{when}");
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
            text.AppendLine($"- `{exempted.Path}` (`{exempted.Pattern}`{reason})");
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
        text.AppendLine();

        foreach (var page in report.Pages)
        {
            text.AppendLine($"- **{Escape(page.Title)}** — `{page.Path}`");
            foreach (var match in page.Matches)
            {
                text.AppendLine($"  - `{match.Pattern}` → {Files(match)}");
            }
        }

        text.AppendLine();
        text.AppendLine(
            "Check whether these pages still describe the code. This is advisory: `docume drift` never "
            + "edits a page.");
    }

    private static string Files(SourceMatch match)
    {
        var shown = match.Files.Take(MaxFilesPerPattern).Select(file => $"`{file}`");
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

        return $"<sub>`docume drift` — baseline `{Escape(report.Baseline)}` → head "
            + $"`{Escape(report.Head)}`, {report.ChangedFileCount} {files}{exempted}{held}{ignored}.</sub>";
    }

    /// <summary>
    /// Escapes the markdown that turns text into formatting. Titles come from a consumer's frontmatter
    /// or an H1, so a title like <c>Rates_and_Fees</c> or <c>&lt;Draft&gt;</c> is ordinary; rendered raw
    /// it would silently italicize or vanish. Paths and patterns are wrapped in code spans by the caller
    /// instead, which is the stronger guarantee.
    /// </summary>
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
