using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Json;
using DocuMe.Core.State;

namespace DocuMe.Core.Scaffolding;

/// <summary>
/// Scaffolds a consumer repo for DocuMe (PLAN.md §6.1): a minimal <c>docume.json</c>, a
/// <c>docs/wiki</c> skeleton, the <c>.github/workflows/docs-*.yml</c> lifecycle jobs of §10 and
/// the mermaid render script of §4. Idempotent — an existing file is never overwritten; every
/// target is reported as <see cref="ScaffoldAction.Created"/> or
/// <see cref="ScaffoldAction.Skipped"/> (rule §9.4). <c>--adopt</c> mode and the
/// <c>.gitignore</c> entries of §6.1 are still outstanding.
/// </summary>
/// <remarks>
/// The workflows and the render script are shipped from <see cref="BundledTemplates"/> byte for
/// byte, so a consumer gets the reviewed file rather than a paraphrase of it. Idempotency matters
/// most here: every workflow template carries an "EDIT BEFORE USE" header telling the consumer to
/// change <c>branches:</c> and <c>paths:</c>, so a re-run that overwrote them would silently undo
/// the one edit the file asks for.
/// </remarks>
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

    /// <summary>Where GitHub Actions looks; not configurable, so neither is this (PLAN.md §10).</summary>
    private const string WorkflowDirectory = ".github/workflows";

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
        List<ScaffoldResult> results =
        [
            Write(targetDir, ConfigLoader.DefaultFileName, () => BuildConfigJson(spaceKey, baseUrl)),
            Write(targetDir, "docs/wiki/README.md", BuildReadme),
            Write(targetDir, "docs/wiki/_meta/STYLE.md", BuildStyleGuide),
            WriteState(targetDir, "docs/wiki/_meta/state.json"),
        ];

        results.AddRange(BundledTemplates.WorkflowFileNames.Select(name => Copy(
            targetDir,
            $"{WorkflowDirectory}/{name}",
            () => BundledTemplates.ReadWorkflow(name))));

        // After the config write, so a fresh repo reads the file it just got rather than a
        // duplicate of the defaults that built it.
        var renderer = ResolveRendererPath(targetDir);
        results.Add(Copy(
            targetDir,
            renderer.RelativePath,
            BundledTemplates.ReadRenderScript,
            renderer.Note));

        return results;
    }

    /// <summary>
    /// Where the mermaid render script goes: wherever the target repo's <c>docume.json</c> points
    /// <c>mermaid.renderer</c>, since that is the path <c>docume publish</c> will run (PLAN.md §5.1,
    /// §6.2 step 3). Scaffolding it to the default while the config named somewhere else would ship
    /// a script nothing ever executes.
    /// </summary>
    private static (string RelativePath, string? Note) ResolveRendererPath(string targetDir)
    {
        var configPath = System.IO.Path.Combine(targetDir, ConfigLoader.DefaultFileName);
        var fallback = new MermaidConfig().Renderer;

        string configured;
        try
        {
            configured = ConfigLoader.Load(configPath).Mermaid.Renderer;
        }
        catch (Exception exception) when (exception is ConfigNotFoundException
            or ConfigValidationException
            or JsonException)
        {
            // init is the command a consumer runs to get *out* of a broken setup, so an unreadable
            // config cannot be fatal here. It is still said out loud: every other command will
            // refuse outright, and the note is the only hint init can give about why.
            return (fallback, $"docume.json could not be read ({exception.Message.ReplaceLineEndings(" ")}), "
                + $"so the default renderer path was used. Move the script if mermaid.renderer names another.");
        }

        if (!IsInsideTarget(targetDir, configured))
        {
            return (fallback, $"docume.json names mermaid.renderer '{configured}', which is not a path "
                + "inside this directory; the default was used instead.");
        }

        return (configured.Replace('\\', '/'), null);
    }

    /// <summary>
    /// Whether a configured path resolves to somewhere under the scaffolded directory. A rooted or
    /// <c>..</c>-escaping value would make an idempotent scaffold write outside the repo it was
    /// pointed at, which no consumer asked for by editing one config field. Compared after
    /// resolution rather than by inspecting segments, so <c>tools/../tools/x.mjs</c> (harmless)
    /// and <c>../../x.mjs</c> (not) are told apart by where they land.
    /// </summary>
    private static bool IsInsideTarget(string targetDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var root = System.IO.Path.GetFullPath(targetDir);
        var resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));

        return resolved.StartsWith(
            root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
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

    /// <summary>
    /// Writes a bundled template's bytes verbatim. Bytes rather than text because the file in
    /// <c>templates/</c> is the reviewed artifact — re-encoding it would make the shipped copy a
    /// near-match instead of the same file, and the tests assert byte equality against the tree.
    /// </summary>
    private static ScaffoldResult Copy(
        string targetDir,
        string relativePath,
        Func<byte[]> content,
        string? note = null)
    {
        var fullPath = Combine(targetDir, relativePath);
        if (File.Exists(fullPath))
        {
            return new ScaffoldResult(relativePath, ScaffoldAction.Skipped, note);
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content());
        return new ScaffoldResult(relativePath, ScaffoldAction.Created, note);
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
