using DocuMe.Core.Drift;
using Shouldly;

namespace DocuMe.Core.Tests.Drift;

/// <summary>
/// The seal check drift applies after the matcher (spec §3.3): a pure pass from a report plus two
/// fingerprint maps to a report, so every case here is three literals and no repository.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The positive case is the one worth writing first.</strong> Every other fact here still holds
/// for an implementation that seals nothing at all — a page that stays in <c>Pages</c> is what the
/// feature does when it declines, and a suite of declines would go green over a feature that never
/// fires. So the first fact below asserts the page moved, and the CLI-level
/// <c>CliDriftTests.A_page_whose_sources_are_byte_identical_to_its_seal_is_reported_sealed</c> asserts
/// that a real run computes a current fingerprint that can equal a real seal.
/// </para>
/// <para>
/// The hashes are opaque strings on purpose: what this pass knows about a fingerprint is that two of
/// them are the same string or are not (<see cref="SourcesFingerprintTests"/> owns the preimage), and a
/// test that computed them here would be pinning the fingerprint twice and this pass once.
/// </para>
/// </remarks>
public sealed class SealedVerdictsTests
{
    private const string Loans = "domains/loans.md";
    private const string Rates = "domains/rates.md";

    private const string Sealed =
        "sha256:a7cde5a477634f8dcdead23450de2f5b02e30f090a82d026dd7cc3edb3b788b7";

    private const string Moved =
        "sha256:6682b235fe153307561f748d02729860005ed7579650498d826d9c1f38657fdf";

    private const string SealedOn = "2026-08-19T09:12:44Z";

    /// <summary>
    /// The fingerprint of no files, the one value this pass refuses on both sides of the comparison
    /// (spec §3.1 as revised 2026-08-19). Spelled out rather than read off
    /// <see cref="SourcesFingerprint.EmptySet"/> so the refusal is pinned against a literal a state file
    /// could actually carry, not against whatever the constant happens to say today.
    /// </summary>
    private const string EmptySet =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// SC1, the fact the whole slice exists for: the diff touched this page's sources, and the bytes
    /// under them are the ones its published body was generated from, so it is disclosed rather than
    /// reported.
    /// </summary>
    [Fact]
    public void A_page_whose_sources_match_its_seal_leaves_the_report_and_is_disclosed()
    {
        var report = SealedVerdicts.Apply(
            ReportFor(Loans),
            Seals((Loans, Sealed)),
            Seals((Loans, Sealed)),
            Seals((Loans, SealedOn)));

        report.Pages.ShouldBeEmpty();

        var held = report.Sealed.ShouldHaveSingleItem();

        held.Path.ShouldBe(Loans);
        held.Title.ShouldBe("Loans");
        held.SealedAt.ShouldBe(SealedOn);
    }

    /// <summary>
    /// The exclusion is by construction, not by a second rule: <c>HasDrift</c>, <c>AffectedCount</c> and
    /// everything downstream of them read <c>Pages</c>, so holding a page out of that list is the whole
    /// change. A second rule is a second thing that can disagree.
    /// </summary>
    [Fact]
    public void The_verdict_a_sealed_page_leaves_behind_is_no_drift()
    {
        var report = SealedVerdicts.Apply(
            ReportFor(Loans),
            Seals((Loans, Sealed)),
            Seals((Loans, Sealed)));

        report.HasDrift.ShouldBeFalse();
        report.AffectedCount.ShouldBe(0);
    }

    /// <summary>SC2: a page whose sources really moved is exactly as it was.</summary>
    [Fact]
    public void A_page_whose_sources_really_moved_is_untouched_by_the_seal()
    {
        var report = SealedVerdicts.Apply(
            ReportFor(Loans),
            Seals((Loans, Sealed)),
            Seals((Loans, Moved)));

        report.Pages.ShouldHaveSingleItem().Path.ShouldBe(Loans);
        report.Sealed.ShouldBeEmpty();
        report.HasDrift.ShouldBeTrue();
    }

    /// <summary>
    /// SC3: a page no publish has sealed keeps today's range-based answer. This is every page in every
    /// wiki that has never published under this feature, so it is the behaviour the slice is additive to.
    /// </summary>
    [Fact]
    public void A_page_with_no_seal_keeps_todays_range_based_answer()
    {
        var report = SealedVerdicts.Apply(
            ReportFor(Loans),
            Seals(),
            Seals((Loans, Sealed)));

        report.Pages.ShouldHaveSingleItem().Path.ShouldBe(Loans);
        report.Sealed.ShouldBeEmpty();
    }

    /// <summary>
    /// SC9: a page whose sources could not be fingerprinted now — a deleted directory, a file the
    /// process may not open, a checkout git cannot answer for — is absent from the current map, and an
    /// absent answer must never suppress a drift report.
    /// </summary>
    [Fact]
    public void A_page_whose_sources_could_not_be_read_stays_in_the_report()
    {
        var report = SealedVerdicts.Apply(
            ReportFor(Loans),
            Seals((Loans, Sealed)),
            Seals());

        report.Pages.ShouldHaveSingleItem().Path.ShouldBe(Loans);
        report.Sealed.ShouldBeEmpty();
    }

    /// <summary>
    /// The feature's worst failure mode, refused at the point it would do its damage. A state file
    /// written before this rule can carry the empty-set fingerprint, and it is the one value that equals
    /// itself for a structural reason rather than an evidential one: a glob with a typo in it matches
    /// nothing twice, and a sparse checkout cone'd away from <c>src/</c> reproduces it exactly. Honouring
    /// it would take a page whose sources were never read out of the report and call it verified — a
    /// green run nobody investigates, which is precisely what <c>DriftPlanner.NormalizePattern</c>'s own
    /// comment calls the failure mode of an advisory check that gets believed.
    /// </summary>
    [Fact]
    public void A_recorded_empty_set_seal_never_holds_a_page_out_even_against_itself()
    {
        var report = SealedVerdicts.Apply(
            ReportFor(Loans),
            Seals((Loans, EmptySet)),
            Seals((Loans, EmptySet)),
            Seals((Loans, SealedOn)));

        report.Pages.ShouldHaveSingleItem().Path.ShouldBe(Loans);
        report.Sealed.ShouldBeEmpty();
        report.HasDrift.ShouldBeTrue();
    }

    /// <summary>
    /// The other spelling of "no seal here", and the one a hand-edited or half-migrated state file
    /// produces: <c>"sourcesHash": ""</c>. An empty string is not a fingerprint, so it cannot match a
    /// real one and must not match an empty current value either.
    /// </summary>
    [Fact]
    public void A_recorded_seal_that_is_an_empty_string_is_not_a_seal()
    {
        var blank = string.Empty;

        var against = SealedVerdicts.Apply(ReportFor(Loans), Seals((Loans, blank)), Seals((Loans, Sealed)));

        against.Pages.ShouldHaveSingleItem().Path.ShouldBe(Loans);
        against.Sealed.ShouldBeEmpty();

        var itself = SealedVerdicts.Apply(ReportFor(Loans), Seals((Loans, blank)), Seals((Loans, blank)));

        itself.Pages.ShouldHaveSingleItem().Path.ShouldBe(Loans);
        itself.Sealed.ShouldBeEmpty();
    }

    /// <summary>
    /// Two fingerprints that differ only in case are two fingerprints. The spelling is pinned as
    /// lowercase hex (<see cref="SourcesFingerprintTests"/>), so a case-insensitive compare could only
    /// ever paper over a value some other tool wrote — and papering over it here would seal a page
    /// against bytes nobody in this codebase hashed.
    /// </summary>
    [Fact]
    public void A_seal_that_differs_only_in_case_is_not_a_match()
    {
        var report = SealedVerdicts.Apply(
            ReportFor(Loans),
            Seals((Loans, Sealed.ToUpperInvariant())),
            Seals((Loans, Sealed)));

        report.Pages.ShouldHaveSingleItem().Path.ShouldBe(Loans);
    }

    /// <summary>
    /// Both halves in one report, which is the shape a real run produces: one page sealed, one page
    /// genuinely drifted, and the reader able to account for both.
    /// </summary>
    [Fact]
    public void The_two_outcomes_partition_the_reported_pages()
    {
        var report = SealedVerdicts.Apply(
            Report([Page(Loans, "Loans"), Page(Rates, "Rates")]),
            Seals((Loans, Sealed), (Rates, Sealed)),
            Seals((Loans, Sealed), (Rates, Moved)));

        report.Pages.ShouldHaveSingleItem().Path.ShouldBe(Rates);
        report.Sealed.ShouldHaveSingleItem().Path.ShouldBe(Loans);
    }

    /// <summary>
    /// Ordinal by path, like every other list in this report: it ends up in a PR comment a bot rewrites
    /// in place, so it has to be a function of the inputs and nothing else.
    /// </summary>
    [Fact]
    public void The_sealed_list_is_ordinal_by_path()
    {
        var pages = new[] { Page(Rates, "Rates"), Page(Loans, "Loans"), Page("domains/fx.md", "FX") };

        var report = SealedVerdicts.Apply(
            Report(pages),
            Seals((Loans, Sealed), (Rates, Sealed), ("domains/fx.md", Sealed)),
            Seals((Loans, Sealed), (Rates, Sealed), ("domains/fx.md", Sealed)));

        report.Sealed.Select(page => page.Path).ShouldBe(["domains/fx.md", Loans, Rates]);
    }

    /// <summary>
    /// A seal whose date the state file does not carry — a page sealed by a run that recorded the hash
    /// and nothing else — is still a seal. The date is disclosure, not evidence.
    /// </summary>
    [Fact]
    public void A_seal_with_no_recorded_date_is_still_a_seal()
    {
        var report = SealedVerdicts.Apply(
            ReportFor(Loans),
            Seals((Loans, Sealed)),
            Seals((Loans, Sealed)));

        report.Sealed.ShouldHaveSingleItem().SealedAt.ShouldBeNull();
    }

    /// <summary>
    /// Everything the report says about the diff and the tree survives: the seal narrows which pages are
    /// reported, and narrows nothing else. The two denominators in particular, because they are what
    /// make "0 affected" mean something.
    /// </summary>
    [Fact]
    public void The_rest_of_the_report_is_carried_through_untouched()
    {
        var original = Report([Page(Loans, "Loans")]) with
        {
            Exempted = [new ExemptedChange("vendor/lib.js", "vendor/**", null)],
            IgnoredCommitCount = 2,
        };

        var report = SealedVerdicts.Apply(
            original,
            Seals((Loans, Sealed)),
            Seals((Loans, Sealed)));

        report.Baseline.ShouldBe(original.Baseline);
        report.Head.ShouldBe(original.Head);
        report.ChangedFileCount.ShouldBe(original.ChangedFileCount);
        report.PageCount.ShouldBe(original.PageCount);
        report.PagesWithSourcesCount.ShouldBe(original.PagesWithSourcesCount);
        report.Exempted.ShouldBe(original.Exempted);
        report.IgnoredCommitCount.ShouldBe(original.IgnoredCommitCount);
    }

    /// <summary>
    /// A wiki with nothing sealed pays nothing and reads identically: the common case for a repo that
    /// has never published under this feature, and the property that makes the slice additive.
    /// </summary>
    [Fact]
    public void A_report_nothing_is_sealed_in_comes_back_as_it_went_in()
    {
        var original = Report([Page(Loans, "Loans")]);

        var report = SealedVerdicts.Apply(original, Seals(), Seals());

        report.Pages.ShouldBe(original.Pages);
        report.Sealed.ShouldBeEmpty();
    }

    /// <summary>
    /// A seal for a page the diff never flagged changes nothing. The map is built from state, which
    /// holds every page ever published, and only the reported pages are the seal's business.
    /// </summary>
    [Fact]
    public void A_seal_for_an_unreported_page_is_ignored()
    {
        var report = SealedVerdicts.Apply(
            ReportFor(Loans),
            Seals((Loans, Sealed), (Rates, Sealed)),
            Seals((Loans, Moved), (Rates, Sealed)));

        report.Sealed.ShouldBeEmpty();
        report.Pages.ShouldHaveSingleItem().Path.ShouldBe(Loans);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Should.Throw<ArgumentNullException>(() => SealedVerdicts.Apply(null!, Seals(), Seals()));
        Should.Throw<ArgumentNullException>(() => SealedVerdicts.Apply(ReportFor(Loans), null!, Seals()));
        Should.Throw<ArgumentNullException>(() => SealedVerdicts.Apply(ReportFor(Loans), Seals(), null!));
    }

    private static Dictionary<string, string> Seals(params (string Path, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Path, entry => entry.Value, StringComparer.Ordinal);

    private static DriftReport ReportFor(string path, string title = "Loans") =>
        Report([Page(path, title)]);

    private static DriftReport Report(IReadOnlyList<DriftedPage> pages) => new()
    {
        Baseline = "abc1234",
        Head = "HEAD",
        ChangedFileCount = 3,
        PageCount = 4,
        PagesWithSourcesCount = 3,
        Pages = pages,
    };

    private static DriftedPage Page(string path, string title) =>
        new(path, title, [new SourceMatch("src/**", ["src/Thing.cs"])]);
}
