using DocuMe.Core.Config;
using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// The publish write lock (PLAN.md §5.1 <c>confluence.protectedSpaces</c>, CLAUDE.md §0.1,
/// rule §1.4). These are the tests that make the lock a mechanism rather than a comment: a run
/// pointed at a space the repo is not cleared for must refuse, and only a per-run human override
/// may lift it.
/// </summary>
public sealed class PublishGuardTests
{
    [Fact]
    public void Unlisted_space_is_writable()
    {
        var refusal = PublishGuard.WriteRefusal(Confluence("DOCUMESBX", "AUR"), allowProtectedSpace: false);

        refusal.ShouldBeNull();
    }

    [Fact]
    public void No_protected_spaces_is_the_default_and_writable()
    {
        var refusal = PublishGuard.WriteRefusal(
            new ConfluenceConfig { SpaceKey = "AUR" },
            allowProtectedSpace: false);

        refusal.ShouldBeNull();
    }

    [Fact]
    public void Protected_space_is_refused_and_the_message_names_the_way_out()
    {
        var refusal = PublishGuard.WriteRefusal(Confluence("AUR", "AUR"), allowProtectedSpace: false);

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("AUR");
        refusal.ShouldContain("protectedSpaces");
        refusal.ShouldContain("--allow-protected-space");
    }

    [Fact]
    public void Protected_space_is_writable_only_with_the_per_run_override()
    {
        var refusal = PublishGuard.WriteRefusal(Confluence("AUR", "AUR"), allowProtectedSpace: true);

        refusal.ShouldBeNull();
    }

    /// <summary>
    /// A lower-case typo in <c>docume.json</c> still names the production space, so the lock cannot
    /// be case-sensitive. Over-refusing is the safe direction for a guard on destructive writes.
    /// </summary>
    [Theory]
    [InlineData("aur")]
    [InlineData("Aur")]
    [InlineData("  AUR  ")]
    public void Refusal_ignores_case_and_surrounding_whitespace(string spaceKey)
    {
        var refusal = PublishGuard.WriteRefusal(Confluence(spaceKey, " aur "), allowProtectedSpace: false);

        refusal.ShouldNotBeNull();
    }

    /// <summary>
    /// A blank entry is a config slip, not a wildcard: it must not lock every space, and it must not
    /// match a config that has no space key either.
    /// </summary>
    [Fact]
    public void Blank_protected_entries_match_nothing()
    {
        PublishGuard.WriteRefusal(Confluence("DOCUMESBX", string.Empty, "   "), allowProtectedSpace: false)
            .ShouldBeNull();

        PublishGuard.WriteRefusal(Confluence(null, string.Empty, "   "), allowProtectedSpace: false)
            .ShouldBeNull();
    }

    private static ConfluenceConfig Confluence(string? spaceKey, params string[] protectedSpaces) =>
        new() { SpaceKey = spaceKey, ProtectedSpaces = protectedSpaces };
}
