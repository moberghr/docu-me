using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

public sealed class FrontmatterParserTests
{
    [Fact]
    public void Parse_extracts_sources_title_and_pageId_and_strips_the_block()
    {
        var parsed = FrontmatterParser.Parse(
            """
            ---
            sources:
              - Loans/**
              - AppApi/Services/LoanService.cs
            title: Loans Domain
            pageId: "123456"
            ---

            # Heading From Body

            Body text.
            """);

        parsed.Frontmatter.Sources.ShouldBe(["Loans/**", "AppApi/Services/LoanService.cs"]);
        parsed.Frontmatter.Title.ShouldBe("Loans Domain");
        parsed.Frontmatter.PageId.ShouldBe("123456");

        // Frontmatter override wins over the first H1.
        parsed.Title.ShouldBe("Loans Domain");

        // The YAML block is gone; the body begins at the first markdown line.
        parsed.Body.ShouldNotContain("sources:");
        parsed.Body.TrimStart().ShouldStartWith("# Heading From Body");
    }

    [Fact]
    public void Parse_falls_back_to_first_H1_when_title_is_absent()
    {
        var parsed = FrontmatterParser.Parse(
            """
            ---
            sources:
              - src/Widgets/**
            ---

            # Widget Catalogue

            ## Section

            Text.
            """);

        parsed.Frontmatter.Title.ShouldBeNull();
        parsed.Title.ShouldBe("Widget Catalogue");
        parsed.Frontmatter.Sources.ShouldBe(["src/Widgets/**"]);
    }

    [Fact]
    public void Parse_without_frontmatter_returns_defaults_and_body_unchanged()
    {
        const string markdown = "# Plain Page\n\nNo frontmatter here.\n";

        var parsed = FrontmatterParser.Parse(markdown);

        parsed.Frontmatter.Sources.ShouldBeEmpty();
        parsed.Frontmatter.Title.ShouldBeNull();
        parsed.Frontmatter.PageId.ShouldBeNull();
        parsed.Title.ShouldBe("Plain Page");
        parsed.Body.ShouldBe(markdown);
    }

    [Fact]
    public void Parse_reads_unquoted_numeric_pageId_as_string()
    {
        var parsed = FrontmatterParser.Parse(
            """
            ---
            pageId: 987654
            ---

            # Adopted Page
            """);

        parsed.Frontmatter.PageId.ShouldBe("987654");
    }

    [Fact]
    public void Parse_preserves_inline_code_text_in_H1_title_fallback()
    {
        var parsed = FrontmatterParser.Parse("# The `docume` CLI\n\nBody.\n");

        // The inline-code text must survive — not collapse to "The  CLI".
        parsed.Title.ShouldBe("The docume CLI");
    }

    [Fact]
    public void Parse_keeps_this_parser_in_extension_lockstep_with_the_converter()
    {
        // Both pipelines must enable the same extensions. Strikethrough-only emphasis
        // extras means a single '~' is not a delimiter here either, so a title keeps
        // its tilde while struck text still contributes its plain words.
        FrontmatterParser.Parse("# Sizing ~10 Nodes\n\nBody.\n").Title.ShouldBe("Sizing ~10 Nodes");
        FrontmatterParser.Parse("# The ~~old~~ new CLI\n\nBody.\n").Title.ShouldBe("The old new CLI");
    }

    [Fact]
    public void Parse_with_no_H1_and_no_title_leaves_title_null()
    {
        var parsed = FrontmatterParser.Parse(
            """
            ---
            sources:
              - src/**
            ---

            ## Only A Subheading

            Text.
            """);

        parsed.Title.ShouldBeNull();
    }

    [Fact]
    public void Parse_reads_publish_false_as_a_draft()
    {
        var parsed = FrontmatterParser.Parse(
            """
            ---
            publish: false
            ---

            # Draft Page
            """);

        parsed.Frontmatter.Publish.ShouldBeFalse();
    }

    [Fact]
    public void Parse_reads_explicit_publish_true()
    {
        var parsed = FrontmatterParser.Parse(
            """
            ---
            publish: true
            ---

            # Published Page
            """);

        parsed.Frontmatter.Publish.ShouldBeTrue();
    }

    [Fact]
    public void Parse_defaults_publish_to_true_when_the_key_is_absent()
    {
        var parsed = FrontmatterParser.Parse(
            """
            ---
            title: Keyless Page
            ---

            # Keyless Page
            """);

        parsed.Frontmatter.Publish.ShouldBeTrue();

        // A page with no frontmatter at all is publishable too.
        FrontmatterParser.Parse("# Plain Page\n\nBody.\n").Frontmatter.Publish.ShouldBeTrue();
    }
}
