using DocuMe.Core.Drift;
using Shouldly;

namespace DocuMe.Core.Tests.Drift;

/// <summary>
/// The drift-ignore parser's contract. An exemption exists to cancel a page's stale flag, so the
/// failure modes pinned here are the quiet ones: a line read as broader than its author wrote it,
/// and a pattern that can never fire sitting in the file looking like protection.
/// </summary>
public sealed class DriftExemptionsTests
{
    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        var exemptions = DriftExemptions.Parse("""
            # generated code never means the docs moved

            src/Generated/**
            """);

        exemptions.Match("src/Generated/Api.g.cs").ShouldNotBeNull();
        exemptions.Match("docs/readme.md").ShouldBeNull();
    }

    [Fact]
    public void Parse_ReadsTheReasonBehindTheHash()
    {
        var exemptions = DriftExemptions.Parse("src/Generated/** # codegen sweep, docs describe the templates");

        var change = exemptions.Match("src/Generated/Api.g.cs").ShouldNotBeNull();
        change.Path.ShouldBe("src/Generated/Api.g.cs");
        change.Pattern.ShouldBe("src/Generated/**");
        change.Reason.ShouldBe("codegen sweep, docs describe the templates");
    }

    [Fact]
    public void Parse_LeavesTheReasonNullWhenALineGivesNone()
    {
        var exemptions = DriftExemptions.Parse("vendor/**");

        exemptions.Match("vendor/lib/a.js").ShouldNotBeNull().Reason.ShouldBeNull();
    }

    [Fact]
    public void Parse_TreatsABareTrailingHashAsNoReason()
    {
        // The dangling marker is noise, not an empty statement: JSON drops nulls, so a reason must
        // be words or absent, never "".
        var exemptions = DriftExemptions.Parse("vendor/** #");

        exemptions.Match("vendor/lib/a.js").ShouldNotBeNull().Reason.ShouldBeNull();
    }

    [Fact]
    public void Parse_ThrowsOnAReasonWithNoPatternBeforeIt()
    {
        // The gitignore convention: only a '#' opening the line comments it out. An indented '#'
        // is a reason whose pattern is missing, and skipping it as commentary would quietly load
        // less of the list than its author wrote.
        var exception = Should.Throw<DriftIgnoreFormatException>(() =>
            DriftExemptions.Parse("src/Generated/**\nvendor/**\n # a reason with no pattern"));

        exception.LineNumber.ShouldBe(3);
        exception.Message.ShouldContain("line 3");
    }

    [Fact]
    public void Parse_ThrowsNamingTheLineWhosePatternCanNeverMatch()
    {
        // A bare slash normalizes to nothing, so it would sit in the file matching no change ever
        // while its author believed the exemption was in force. Refused with the line number,
        // because "your drift-ignore is bad" without one means reading the whole file.
        var exception = Should.Throw<DriftIgnoreFormatException>(() => DriftExemptions.Parse("""
            # exemptions
            src/Generated/**
            / # an anchor with nothing behind it
            """));

        exception.LineNumber.ShouldBe(3);
        exception.Message.ShouldContain("line 3");
    }

    /// <summary>
    /// The reason marker is a <c>#</c> with whitespace before it, so a glob may itself contain
    /// <c>#</c> (a legal path character). The dangerous regression here is silent: a truncated
    /// pattern exempts a different file set than its author wrote, and the report quotes the
    /// truncated spelling back so it looks self-consistent.
    /// </summary>
    [Fact]
    public void Parse_KeepsAHashInsideAGlobOutOfTheReason()
    {
        var exemptions = DriftExemptions.Parse("vendor/#tmp/**");

        var exempted = exemptions.Match("vendor/#tmp/cache.bin").ShouldNotBeNull();
        exempted.Pattern.ShouldBe("vendor/#tmp/**");
        exempted.Reason.ShouldBeNull();
    }

    [Fact]
    public void Parse_SplitsTheReasonAfterAGlobThatContainsAHash()
    {
        var exemptions = DriftExemptions.Parse("a#b/** # why");

        var exempted = exemptions.Match("a#b/file.cs").ShouldNotBeNull();
        exempted.Pattern.ShouldBe("a#b/**");
        exempted.Reason.ShouldBe("why");
    }

    [Fact]
    public void Parse_TreatsAWhitespaceOnlyReasonAsNone()
    {
        var exemptions = DriftExemptions.Parse("vendor/** #   ");

        exemptions.Match("vendor/lib.js").ShouldNotBeNull().Reason.ShouldBeNull();
    }

    [Fact]
    public void Parse_OfAnEmptyFileExemptsNothing()
    {
        DriftExemptions.Parse(string.Empty).Match("src/Loans/LoanService.cs").ShouldBeNull();
    }

    [Fact]
    public void Match_ReturnsTheFirstPatternThatClaimsTheFile()
    {
        // The file reads top to bottom, so the report quotes the line its author would point at.
        var exemptions = DriftExemptions.Parse("""
            src/Generated/** # narrow
            src/** # broad
            """);

        exemptions.Match("src/Generated/Api.g.cs").ShouldNotBeNull().Pattern.ShouldBe("src/Generated/**");
        exemptions.Match("src/Loans/LoanService.cs").ShouldNotBeNull().Pattern.ShouldBe("src/**");
    }

    [Fact]
    public void Match_SpeaksTheSameGlobDialectAsSources()
    {
        // Trailing and leading slashes get the same straightening sources globs get (§6.4), and
        // matching stays Ordinal, because an exemption exists to cancel a sources match and the two
        // engines must agree file for file.
        var exemptions = DriftExemptions.Parse("""
            src/Generated/
            /vendor/**
            """);

        var directory = exemptions.Match("src/Generated/deep/Api.g.cs").ShouldNotBeNull();
        directory.Pattern.ShouldBe("src/Generated/");
        exemptions.Match("vendor/lib/a.js").ShouldNotBeNull();
        exemptions.Match("SRC/Generated/Api.g.cs").ShouldBeNull();
    }

    [Fact]
    public void None_MatchesNothing()
    {
        DriftExemptions.None.Match("src/Generated/Api.g.cs").ShouldBeNull();
        DriftExemptions.None.Match("vendor/lib/a.js").ShouldBeNull();
    }
}
