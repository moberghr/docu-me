using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// Whether <c>--prune</c> may run at all (PLAN.md §6.2 "Orphans", rule §9.6). The environment is read
/// through an injected lookup, so nothing here mutates process-global state that xUnit runs in parallel.
/// </summary>
public sealed class PruneGuardTests
{
    /// <summary>Nothing set: the ordinary developer terminal.</summary>
    private static readonly Func<string, string?> NoVariables = _ => null;

    [Fact]
    public void A_plain_run_may_prune()
    {
        PruneGuard.Refusal(Report(), NoVariables).ShouldBeNull();
    }

    /// <summary>
    /// The write lock comes first. A run that reported "not in CI, nothing contradictory, now let me
    /// delete from the space you locked" would have the order exactly backwards (CLAUDE.md §0.1).
    /// </summary>
    [Fact]
    public void A_locked_space_refuses_before_anything_else_is_considered()
    {
        var report = Report(writeRefusal: "space AUR is protected", scope: PublishScope.ForPages(["a.md"]));

        var refusal = PruneGuard.Refusal(report, _ => "true");

        refusal.ShouldBe("space AUR is protected");
    }

    /// <summary>
    /// §6.2: a prune is confirmed interactively and never runs in CI. A pipeline that silently dropped
    /// the flag would report success for a run that did not do what its command line said.
    /// </summary>
    [Theory]
    [InlineData("CI")]
    [InlineData("GITHUB_ACTIONS")]
    [InlineData("TF_BUILD")]
    [InlineData("JENKINS_URL")]
    [InlineData("TEAMCITY_VERSION")]
    public void Ci_refuses_the_prune_and_names_the_variable(string variable)
    {
        var refusal = PruneGuard.Refusal(
            Report(),
            name => string.Equals(name, variable, StringComparison.Ordinal) ? "true" : null);

        refusal.ShouldNotBeNull();
        refusal.ShouldContain(variable);
        refusal.ShouldContain("--prune");
    }

    /// <summary>
    /// Some tools export <c>CI=false</c> to say the opposite. Treating that as CI would refuse a prune on
    /// a developer's own terminal for no reason.
    /// </summary>
    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("  ")]
    public void A_negative_ci_variable_is_not_ci(string value)
    {
        PruneGuard.Refusal(Report(), name => string.Equals(name, "CI", StringComparison.Ordinal) ? value : null)
            .ShouldBeNull();
    }

    /// <summary>
    /// An orphan is a state entry whose file is gone; <c>--page</c> names paths that are in the tree. No
    /// path can be both, so the combination is a contradiction worth refusing rather than a filter that
    /// silently matches nothing.
    /// </summary>
    [Fact]
    public void Prune_with_page_is_a_contradiction()
    {
        var refusal = PruneGuard.Refusal(Report(scope: PublishScope.ForPages(["a.md"])), NoVariables);

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("--page");
        refusal.ShouldContain("--changed-since");
    }

    /// <summary>
    /// The other scope composes: a deletion shows up in <c>git diff --name-only</c>, so
    /// <c>--changed-since</c> can narrow a prune to the orphans it names.
    /// </summary>
    [Fact]
    public void Prune_with_changed_since_is_allowed()
    {
        var scope = PublishScope.ForFilesChangedSince("c0ffee", ["a.md"]);

        PruneGuard.Refusal(Report(scope: scope), NoVariables).ShouldBeNull();
    }

    private static PublishReport Report(string? writeRefusal = null, PublishScope? scope = null) =>
        new("DOCUMESBX", new DateOnly(2026, 7, 25), [], [], [], writeRefusal, scope);
}
