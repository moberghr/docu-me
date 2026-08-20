using System.Diagnostics.CodeAnalysis;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DocuMe.Core.Markdown;

/// <summary>
/// Extracts and strips the YAML frontmatter from a wiki markdown file and
/// resolves the page title (PLAN.md §5.2, §6.2 step 1). Detection uses Markdig's
/// frontmatter extension so only a leading <c>---</c> block at line 1 counts;
/// a mid-document <c>---</c> stays a thematic break.
/// </summary>
public static class FrontmatterParser
{
    // Kept in lockstep with ConfluenceStorageConverter.Pipeline (see the note
    // there): enabling an extension changes inline parsing, so the two pipelines
    // must agree or the title's H1 could be found here and not there.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .UsePipeTables(new PipeTableOptions { UseHeaderForColumnCount = true })
        .Use<GfmTaskListExtension>()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .Build();

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Parses <paramref name="markdown"/> into frontmatter, resolved title, and stripped body.</summary>
    public static ParsedPage Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();

        PageFrontmatter frontmatter;
        string body;
        if (yamlBlock is not null)
        {
            frontmatter = Deserialize(RawYaml(markdown, yamlBlock));
            var after = yamlBlock.Span.End + 1;
            body = (after < markdown.Length ? markdown[after..] : string.Empty).TrimStart('\r', '\n');
        }
        else
        {
            frontmatter = new PageFrontmatter();
            body = markdown;
        }

        var title = frontmatter.Title ?? FirstHeadingText(document);
        return new ParsedPage(frontmatter, title, body);
    }

    /// <summary>Slices the block's source span and drops the <c>---</c> fence lines.</summary>
    private static string RawYaml(string markdown, YamlFrontMatterBlock block)
    {
        var raw = markdown
            .Substring(block.Span.Start, block.Span.Length)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = raw.Split('\n').ToList();
        if (lines.Count > 0 && string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && string.Equals(lines[^1].Trim(), "---", StringComparison.Ordinal))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    private static PageFrontmatter Deserialize(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new PageFrontmatter();
        }

        // Deserialize into a mutable DTO — YamlDotNet needs settable properties
        // and a concrete collection; map onto the immutable record afterwards.
        var dto = Yaml.Deserialize<FrontmatterDto>(yaml) ?? new FrontmatterDto();
        return new PageFrontmatter
        {
            Sources = dto.Sources is { Count: > 0 } ? dto.Sources : [],
            Title = string.IsNullOrWhiteSpace(dto.Title) ? null : dto.Title,
            PageId = string.IsNullOrWhiteSpace(dto.PageId) ? null : dto.PageId,

            // Blank collapses to null exactly as Title and PageId do above; the value is otherwise
            // untouched. No trim, no case fold, no `@` — see PageFrontmatter.Owner for why that
            // refusal is load-bearing rather than an oversight.
            Owner = string.IsNullOrWhiteSpace(dto.Owner) ? null : dto.Owner,
            Publish = dto.Publish ?? true,
        };
    }

    /// <summary>The first H1's plain text, or <c>null</c> when there is no H1.</summary>
    private static string? FirstHeadingText(MarkdownDocument document)
    {
        var h1 = document.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1);
        if (h1?.Inline is null)
        {
            return null;
        }

        // Collect visible text in document order across inline types so a title
        // like `# The `docume` CLI` (inline code) or `# **Bold** title`
        // (emphasis wraps a literal) keeps its full text.
        var text = new StringBuilder();
        foreach (var inline in h1.Inline.Descendants())
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
            }
        }

        var title = text.ToString().Trim();
        return title.Length == 0 ? null : title;
    }

    [SuppressMessage(
        "Minor Code Smell",
        "S1144:Unused private types or members should be removed",
        Justification = "The setters are invoked by YamlDotNet through reflection, which the analyzer cannot see.")]
    private sealed class FrontmatterDto
    {
        public List<string>? Sources { get; set; }

        public string? Title { get; set; }

        public string? PageId { get; set; }

        public string? Owner { get; set; }

        public bool? Publish { get; set; }
    }
}
