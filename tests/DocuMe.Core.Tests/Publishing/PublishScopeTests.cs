using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// The write-set predicate behind <c>--changed-since</c> and <c>--page</c> (PLAN.md §6.2, last
/// paragraph): which pages a scoped run may write, and which requested paths named nothing.
/// </summary>
/// <remarks>
/// What the scope does to a plan is <see cref="PublishPipelineTests"/>'s subject. These tests pin the
/// two rules that make the flag safe rather than merely small: a changed asset reaches the pages that
/// show it, and a path that matches nothing is visible rather than silently empty.
/// </remarks>
public sealed class PublishScopeTests
{
    private static readonly PlannedAttachment Logo =
        new("images_logo.png", AttachmentKind.Asset, "images/logo.png", null, "sha256:logo");

    private static readonly PlannedAttachment Diagram =
        new("mermaid-abc123.svg", AttachmentKind.Diagram, null, "graph TD\n  A --> B", null);

    [Fact]
    public void A_page_is_in_scope_when_its_own_path_is()
    {
        var scope = PublishScope.ForPages(["guides/setup.md"]);

        scope.Includes("guides/setup.md", []).ShouldBeTrue();
        scope.Includes("README.md", []).ShouldBeFalse();
    }

    /// <summary>
    /// An image's bytes can move without a byte of markdown moving, so a scope keyed off markdown alone
    /// would make <c>--changed-since</c> the one publish that cannot ship a changed image.
    /// </summary>
    [Fact]
    public void A_changed_asset_pulls_in_the_pages_that_reference_it()
    {
        var scope = PublishScope.ForFilesChangedSince("abc1234", ["images/logo.png"]);

        scope.Includes("README.md", [Logo]).ShouldBeTrue();
        scope.Includes("guides/setup.md", [Logo, Diagram]).ShouldBeTrue();
        scope.Includes("notes.md", []).ShouldBeFalse();
    }

    /// <summary>
    /// A diagram carries no source file, and its attachment name is a hash of its source — so a changed
    /// fence changes the page body, and the page's own path is what puts it in scope.
    /// </summary>
    [Fact]
    public void A_diagram_never_widens_the_scope()
    {
        var scope = PublishScope.ForFilesChangedSince("abc1234", ["mermaid-abc123.svg"]);

        scope.Includes("guides/setup.md", [Diagram]).ShouldBeFalse();
    }

    [Fact]
    public void Paths_are_normalized_so_a_hand_typed_path_and_gits_answer_agree()
    {
        var scope = PublishScope.ForPages(
            ["./guides/setup.md", "guides\\setup.md", "/guides/setup.md", "  guides/setup.md  ", string.Empty]);

        scope.Paths.ShouldBe(["guides/setup.md"]);
        scope.Includes("guides/setup.md", []).ShouldBeTrue();
    }

    /// <summary>
    /// Ordinal, like every other path in the tool. A mistyped case is therefore a miss, which
    /// <see cref="PublishScope.MissingFrom"/> then turns into a loud failure rather than a run that
    /// publishes nothing and exits 0.
    /// </summary>
    [Fact]
    public void Comparison_is_ordinal_so_a_mistyped_case_matches_nothing()
    {
        var scope = PublishScope.ForPages(["Guides/Setup.md"]);

        scope.Includes("guides/setup.md", []).ShouldBeFalse();
        scope.MissingFrom(["guides/setup.md"]).ShouldBe(["Guides/Setup.md"]);
    }

    [Fact]
    public void MissingFrom_lists_only_the_paths_that_name_nothing()
    {
        var scope = PublishScope.ForPages(["a.md", "gone.md", "./b.md"]);

        scope.MissingFrom(["a.md", "b.md", "c.md"]).ShouldBe(["gone.md"]);
    }

    [Fact]
    public void A_scope_says_which_flag_produced_it()
    {
        PublishScope.ForPages(["a.md"]).Description.ShouldBe("--page");
        PublishScope.ForFilesChangedSince("abc1234", []).Description.ShouldBe("--changed-since abc1234");
    }

    /// <summary>Nothing changed since the sha, so nothing is in scope and the run writes nothing.</summary>
    [Fact]
    public void An_empty_scope_includes_nothing()
    {
        var scope = PublishScope.ForFilesChangedSince("abc1234", []);

        scope.Paths.ShouldBeEmpty();
        scope.Includes("README.md", [Logo]).ShouldBeFalse();
    }

    [Fact]
    public void A_scope_rejects_arguments_it_cannot_act_on()
    {
        Should.Throw<ArgumentNullException>(() => PublishScope.ForPages(null!));
        Should.Throw<ArgumentNullException>(() => PublishScope.ForFilesChangedSince("abc1234", null!));
        Should.Throw<ArgumentException>(() => PublishScope.ForFilesChangedSince(" ", []));

        var scope = PublishScope.ForPages(["a.md"]);
        Should.Throw<ArgumentException>(() => scope.Includes(string.Empty, []));
        Should.Throw<ArgumentNullException>(() => scope.Includes("a.md", null!));
        Should.Throw<ArgumentNullException>(() => scope.MissingFrom(null!));
    }
}
