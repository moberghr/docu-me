using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Json;
using DocuMe.Core.State;

namespace DocuMe.Core.Scaffolding;

/// <summary>
/// Scaffolds a consumer repo for DocuMe (PLAN.md §6.1): a minimal <c>docume.json</c>
/// plus a <c>docs/wiki</c> skeleton. Idempotent — an existing file is never
/// overwritten; every target is reported as <see cref="ScaffoldAction.Created"/>
/// or <see cref="ScaffoldAction.Skipped"/>. Full templated <c>init</c>
/// (render-mermaid, workflows, --adopt) arrives in M6.
/// </summary>
public static class ProjectScaffolder
{
    [SuppressMessage(
        "Major Code Smell",
        "S1075:URIs should not be hardcoded",
        Justification = "The published $schema location is a fixed identifier, not a configurable endpoint (PLAN.md §5.1).")]
    private const string SchemaUrl =
        "https://raw.githubusercontent.com/moberg/docu-me/main/schema/docume.schema.json";

    [SuppressMessage(
        "Major Code Smell",
        "S1075:URIs should not be hardcoded",
        Justification = "Deliberate placeholder text written into the scaffolded docume.json for the user to replace.")]
    private const string BaseUrlPlaceholder = "https://your-domain.atlassian.net/wiki";

    private const string SpaceKeyPlaceholder = "SPACE";

    /// <summary>
    /// Writes the skeleton into <paramref name="targetDir"/>, filling
    /// <c>docume.json</c> from <paramref name="spaceKey"/>/<paramref name="baseUrl"/>
    /// when supplied (placeholders otherwise). Returns one result per target file,
    /// in creation order.
    /// </summary>
    public static IReadOnlyList<ScaffoldResult> Scaffold(
        string targetDir,
        string? spaceKey = null,
        string? baseUrl = null)
    {
        return
        [
            Write(targetDir, "docume.json", () => BuildConfigJson(spaceKey, baseUrl)),
            Write(targetDir, "docs/wiki/README.md", BuildReadme),
            Write(targetDir, "docs/wiki/_meta/STYLE.md", BuildStyleGuide),
            WriteState(targetDir, "docs/wiki/_meta/state.json"),
        ];
    }

    private static ScaffoldResult Write(string targetDir, string relativePath, Func<string> content)
    {
        var fullPath = Combine(targetDir, relativePath);
        if (File.Exists(fullPath))
        {
            return new ScaffoldResult(relativePath, ScaffoldAction.Skipped);
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content());
        return new ScaffoldResult(relativePath, ScaffoldAction.Created);
    }

    private static ScaffoldResult WriteState(string targetDir, string relativePath)
    {
        var fullPath = Combine(targetDir, relativePath);
        if (File.Exists(fullPath))
        {
            return new ScaffoldResult(relativePath, ScaffoldAction.Skipped);
        }

        StateStore.Save(fullPath, new DocumeState());
        return new ScaffoldResult(relativePath, ScaffoldAction.Created);
    }

    private static string Combine(string targetDir, string relativePath)
        => System.IO.Path.Combine([targetDir, .. relativePath.Split('/')]);

    private static string BuildConfigJson(string? spaceKey, string? baseUrl)
    {
        var config = new DocumeConfig
        {
            Schema = SchemaUrl,
            Confluence = new ConfluenceConfig
            {
                BaseUrl = baseUrl ?? BaseUrlPlaceholder,
                SpaceKey = spaceKey ?? SpaceKeyPlaceholder,
            },
        };

        return JsonSerializer.Serialize(config, DocumeJson.Options) + Environment.NewLine;
    }

    private static string BuildReadme() =>
        """
        # Documentation

        This wiki is the source of truth for the project's documentation. It is
        generated and verified against the code, then published to Confluence by
        the `docume` CLI. Edit the markdown here — hand edits in Confluence are
        overwritten on republish.

        See `_meta/STYLE.md` for authoring conventions.
        """ + Environment.NewLine;

    private static string BuildStyleGuide() =>
        """
        # Style guide

        Repo-specific conventions the docs-loop follows when generating this wiki.
        Fill these in for your project.

        - **Audience:** who reads these docs.
        - **Tone:** how they should read.
        - **Structure:** the section taxonomy (domains, services, etc.).
        - **Verification:** every claim needs a code citation; mark unverified
          statements with ⚠️ UNVERIFIED.
        """ + Environment.NewLine;
}
