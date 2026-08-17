using DocuMe.Core.Drift;
using Shouldly;

namespace DocuMe.Core.Tests.Drift;

/// <summary>
/// The drift-ignore-revs parser's contract. An ignored commit cancels a whole sweep's worth of
/// stale flags, so the failure modes pinned here are the quiet ones: a sha that stops matching over
/// its case, and an abbreviation that would sit in the file ignoring nothing.
/// </summary>
public sealed class DriftIgnoreRevsTests
{
    private const string Sweep = "0123456789abcdef0123456789abcdef01234567";
    private const string Unrelated = "fedcba9876543210fedcba9876543210fedcba98";

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        var revs = DriftIgnoreRevs.Parse($"""
            # the 2026-08 format sweep: whitespace only, docs describe the behavior

            {Sweep}
            """);

        revs.Ignores(Sweep).ShouldBeTrue();
        revs.Ignores(Unrelated).ShouldBeFalse();
        revs.Count.ShouldBe(1);
    }

    [Fact]
    public void Ignores_IsCaseInsensitive()
    {
        // git prints object names lowercase, but the file holds whatever a human's tooling gave
        // them to paste, and an exemption that stops firing over its case is exactly the quiet
        // failure this file must not have.
        DriftIgnoreRevs.Parse(Sweep.ToUpperInvariant()).Ignores(Sweep).ShouldBeTrue();
        DriftIgnoreRevs.Parse(Sweep).Ignores(Sweep.ToUpperInvariant()).ShouldBeTrue();
    }

    [Fact]
    public void Parse_ThrowsOnAnAbbreviatedShaNamingItsLine()
    {
        // Matching is exact against the full sha git reports, so an abbreviation would sit in the
        // file ignoring nothing while its author believed the sweep was exempt. Refused with the
        // line number, because "your drift-ignore-revs is bad" without one means reading the file.
        var exception = Should.Throw<DriftIgnoreRevsFormatException>(() => DriftIgnoreRevs.Parse($"""
            # sweeps
            {Sweep}
            d00dfeed
            """));

        exception.LineNumber.ShouldBe(3);
        exception.Line.ShouldBe("d00dfeed");
        exception.Message.ShouldContain("line 3");
    }

    [Fact]
    public void Parse_ThrowsOnFortyCharactersThatAreNotHex()
    {
        // Length alone is not a sha: the line must survive as hex, or a stray paste (a branch
        // name, a truncated-and-padded sha) would load as an entry that can never fire.
        var exception = Should.Throw<DriftIgnoreRevsFormatException>(() =>
            DriftIgnoreRevs.Parse("g123456789abcdef0123456789abcdef01234567"));

        exception.LineNumber.ShouldBe(1);
        exception.Message.ShouldContain("line 1");
    }

    /// <summary>
    /// Git 2.54 accepts a trailing comment, an indented comment and an indented sha in
    /// <c>blame.ignoreRevsFile</c>, and refuses only an abbreviation. The promise that one file can
    /// serve both tools holds only if this parser draws the line where git draws it.
    /// </summary>
    [Fact]
    public void Parse_AcceptsEveryLineShapeGitAccepts()
    {
        var revs = DriftIgnoreRevs.Parse($"{Sweep} # the format sweep\n  # indented comment\n  {Sweep}  ");

        revs.Count.ShouldBe(1);
        revs.Ignores(Sweep).ShouldBeTrue();
    }

    [Fact]
    public void Parse_OfAnEmptyFileIgnoresNothing()
    {
        var revs = DriftIgnoreRevs.Parse(string.Empty);

        revs.Ignores(Sweep).ShouldBeFalse();
        revs.Count.ShouldBe(0);
    }

    [Fact]
    public void None_IgnoresNothing()
    {
        DriftIgnoreRevs.None.Ignores(Sweep).ShouldBeFalse();
        DriftIgnoreRevs.None.Count.ShouldBe(0);
    }
}
