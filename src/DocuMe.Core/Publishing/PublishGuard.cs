using DocuMe.Core.Config;

namespace DocuMe.Core.Publishing;

/// <summary>
/// The publish write lock: decides whether a run may write into the configured space at all
/// (PLAN.md §5.1 <c>confluence.protectedSpaces</c>, CLAUDE.md §0.1, rule §1.4).
/// </summary>
/// <remarks>
/// <para>
/// A publish overwrites hand edits by design (rule §9.1) and <c>--prune</c> deletes orphan pages
/// (§6.2), so a run pointed at the wrong space is destructive and gives no warning while doing it.
/// Nothing in <see cref="Confluence.ConfluenceClient"/> knows which space it writes to, so the
/// refusal has to live here, in front of the pipeline, rather than at the HTTP layer.
/// </para>
/// <para>
/// Unlocking is a per-run decision a human makes (<c>--allow-protected-space</c>) and deliberately
/// not something the committed config can grant itself: a config that could unlock its own lock
/// would be a lock nobody had to think about.
/// </para>
/// </remarks>
public static class PublishGuard
{
    /// <summary>
    /// Why a real publish must refuse to write, or <c>null</c> when the target space is writable.
    /// </summary>
    /// <param name="confluence">The <c>confluence</c> config section (§5.1).</param>
    /// <param name="allowProtectedSpace">
    /// <c>--allow-protected-space</c>: the human override for one run.
    /// </param>
    /// <remarks>
    /// A refusal is reported rather than thrown so <c>--dry-run</c> can print the plan <em>and</em>
    /// the fact that a real run would be refused. The write path checks
    /// <see cref="PublishReport.CanWrite"/> before its first request.
    /// </remarks>
    public static string? WriteRefusal(ConfluenceConfig confluence, bool allowProtectedSpace)
    {
        ArgumentNullException.ThrowIfNull(confluence);

        if (allowProtectedSpace)
        {
            return null;
        }

        var spaceKey = confluence.SpaceKey?.Trim();

        // Case-insensitive, and a blank entry matches nothing: Confluence space keys are upper-case
        // by convention but a lower-case typo in docume.json still names the same space, and
        // over-refusing is the safe direction for a lock that guards destructive writes.
        var locked = confluence.ProtectedSpaces.Any(key =>
            !string.IsNullOrWhiteSpace(key)
            && string.Equals(key.Trim(), spaceKey, StringComparison.OrdinalIgnoreCase));

        if (!locked)
        {
            return null;
        }

        return $"confluence.spaceKey '{spaceKey}' is listed in confluence.protectedSpaces: this repo is "
            + "not cleared to publish there. Pass --allow-protected-space to override for a single run, "
            + "or remove the entry from docume.json to go live.";
    }
}
