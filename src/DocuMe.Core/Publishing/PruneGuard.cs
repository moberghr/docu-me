namespace DocuMe.Core.Publishing;

/// <summary>
/// Whether <c>--prune</c> may run at all (PLAN.md §6.2 "Orphans", rule §9.6, CLAUDE.md §0.1).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="PublishGuard"/>, which decides whether a run may <em>write</em>. Deleting is
/// the one verb in DocuMe with no undo beyond the space trash, so it carries two refusals a write does
/// not: it never runs unattended, and it never runs under a flag that contradicts it.
/// </para>
/// <para>
/// <strong>The write lock is checked first.</strong> A protected space must refuse a prune before it
/// refuses anything else — a run that reported "not in CI, nothing contradictory, now let me delete from
/// the space you locked" would have the order exactly backwards.
/// </para>
/// <para>
/// <strong>CI is a hard refusal, not a silent skip.</strong> §6.2 says a prune is confirmed
/// interactively and never runs in CI. A pipeline that quietly dropped the flag would report success for
/// a run that did not do what its command line said, and the orphans would sit undeleted with nothing
/// naming them; refusing loudly makes the operator move the prune to a human's terminal, which is where
/// rule §9.6 wants it.
/// </para>
/// </remarks>
public static class PruneGuard
{
    /// <summary>
    /// Environment variables whose presence means "no human is watching this shell".
    /// </summary>
    /// <remarks>
    /// <c>CI</c> is the near-universal one (GitHub Actions, GitLab, CircleCI, Travis, Buildkite, Drone);
    /// the rest are the mainstream runners that set no <c>CI</c> of their own. The list over-detects on
    /// purpose: a false positive costs one flag on a developer's terminal, a false negative deletes pages
    /// nobody confirmed.
    /// </remarks>
    private static readonly string[] CiVariables =
    [
        "CI",
        "GITHUB_ACTIONS",
        "TF_BUILD",
        "JENKINS_URL",
        "TEAMCITY_VERSION",
    ];

    /// <summary>Values that mean the variable is set but says "not CI" — some tools export <c>CI=false</c>.</summary>
    private static readonly string[] NegativeValues = ["false", "0", "no"];

    /// <summary>
    /// Why <c>--prune</c> must refuse to run, or <c>null</c> when it may proceed.
    /// </summary>
    /// <param name="report">
    /// The run's plan. Its <see cref="PublishReport.WriteRefusal"/> is the space lock, and its
    /// <see cref="PublishReport.Scope"/> is the flag combination.
    /// </param>
    /// <param name="readEnvironmentVariable">
    /// How to read an environment variable; defaults to the process environment. A parameter so the CI
    /// refusal is testable without mutating process-global state, which xUnit runs in parallel.
    /// </param>
    public static string? Refusal(
        PublishReport report,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        readEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        if (report.WriteRefusal is { } locked)
        {
            return locked;
        }

        if (DetectCi(readEnvironmentVariable) is { } variable)
        {
            return $"--prune deletes pages and needs a human to confirm it, but {variable} is set, so "
                + "this looks like CI (PLAN.md §6.2, rule §9.6). The publish itself is fine unattended; "
                + "run the prune from a terminal instead. Orphans are reported by every run, so nothing "
                + "is lost by leaving them for one.";
        }

        if (report.Scope?.Kind == PublishScopeKind.Pages)
        {
            return "--prune and --page contradict each other: an orphan is a state entry whose markdown "
                + "file is gone, and --page names pages that are in the tree, so no path can be both. "
                + "Drop --page to prune, or drop --prune to publish just those pages. (--prune does "
                + "compose with --changed-since, which sees deletions.)";
        }

        return null;
    }

    /// <summary>The variable that indicates CI, spelled as <c>NAME=value</c>, or <c>null</c>.</summary>
    private static string? DetectCi(Func<string, string?> readEnvironmentVariable)
    {
        foreach (var name in CiVariables)
        {
            var value = readEnvironmentVariable(name)?.Trim();

            if (string.IsNullOrEmpty(value)
                || NegativeValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            return $"{name}={value}";
        }

        return null;
    }
}
