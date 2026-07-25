using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocuMe.Core.Config;
using DocuMe.Core.Json;
using DocuMe.Core.State;

namespace DocuMe.Core.Scaffolding;

/// <summary>
/// Scaffolds a consumer repo for DocuMe (PLAN.md §6.1): a minimal <c>docume.json</c>, a
/// <c>docs/wiki</c> skeleton, the <c>.github/workflows/docs-*.yml</c> lifecycle jobs of §10, the
/// mermaid render script of §4, the <c>.config/dotnet-tools.json</c> pin of §12 and the
/// <c>.gitignore</c> entry the render script needs. Idempotent — a file DocuMe owns is never
/// overwritten, and a file it only contributes to is added to rather than replaced; every target is
/// reported as <see cref="ScaffoldAction.Created"/>, <see cref="ScaffoldAction.Updated"/> or
/// <see cref="ScaffoldAction.Skipped"/> (rule §9.4). <c>--adopt</c> mode is still outstanding.
/// </summary>
/// <remarks>
/// <para>
/// The workflows and the render script are shipped from <see cref="BundledTemplates"/> byte for
/// byte, so a consumer gets the reviewed file rather than a paraphrase of it. Idempotency matters
/// most here: every workflow template carries an "EDIT BEFORE USE" header telling the consumer to
/// change <c>branches:</c> and <c>paths:</c>, so a re-run that overwrote them would silently undo
/// the one edit the file asks for.
/// </para>
/// <para>
/// The tool manifest is not decoration. Every one of those six workflows runs
/// <c>dotnet tool restore</c> before <c>dotnet tool run docume</c>, and <c>dotnet tool restore</c>
/// in a repo with no manifest fails — so without this a fresh consumer would run <c>init</c>, push,
/// and get a red check on its first docs job.
/// </para>
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

    /// <summary>Where <c>dotnet tool restore</c> looks; likewise fixed by the SDK, not by us.</summary>
    private const string ToolManifestPath = ".config/dotnet-tools.json";

    private const string ToolPackageId = "DocuMe.Cli";
    private const string ToolCommandName = "docume";

    /// <summary>
    /// The package id as the .NET SDK spells it inside a manifest: lowercased. Pinned as a literal
    /// rather than lowercased from <see cref="ToolPackageId"/> because the SDK's normalization is the
    /// contract here, not ours — verified against what <c>dotnet tool install DocuMe.Cli --local</c>
    /// actually writes.
    /// </summary>
    private const string ToolManifestKey = "docume.cli";

    private const string GitignoreEntry = "node_modules/";

    private const string GitignoreComment =
        "# Node packages for the DocuMe mermaid renderer (`npm install beautiful-mermaid`).";

    /// <summary>
    /// Spellings of <see cref="GitignoreEntry"/> that already ignore the same thing. A consumer repo
    /// that writes <c>node_modules</c> without the slash needs nothing appended, and appending anyway
    /// would grow their file by one redundant line on every <c>init</c>.
    /// </summary>
    private static readonly string[] GitignoreEquivalents =
    [
        "node_modules",
        "node_modules/",
        "/node_modules",
        "/node_modules/",
        "**/node_modules",
        "**/node_modules/",
    ];

    /// <summary>
    /// The version the scaffolded manifest pins: the version of the assembly doing the scaffolding.
    /// That is the version of the tool the consumer just installed, and by §12's single-version rule
    /// (one <c>&lt;Version&gt;</c> in <c>Directory.Build.props</c> covers CLI, Core, plugin and
    /// action) it is the <c>DocuMe.Cli</c> package's version too. Read rather than hardcoded so a
    /// release cannot ship an <c>init</c> that pins the previous version.
    /// </summary>
    private static readonly string ToolVersion = ResolveToolVersion();

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

        // Directly after the workflows, because it is what makes them run at all.
        results.Add(MergeToolManifest(targetDir));

        // After the config write, so a fresh repo reads the file it just got rather than a
        // duplicate of the defaults that built it.
        var renderer = ResolveRendererPath(targetDir);
        results.Add(Copy(
            targetDir,
            renderer.RelativePath,
            BundledTemplates.ReadRenderScript,
            renderer.Note));

        // Last, and after the render script, since the entry exists for that script's dependencies.
        results.Add(MergeGitignore(targetDir));

        return results;
    }

    /// <summary>
    /// Pins <c>DocuMe.Cli</c> in the repo-local tool manifest (PLAN.md §12) so the scaffolded
    /// workflows' <c>dotnet tool restore</c> has something to restore. A manifest is shared ground —
    /// the consumer keeps their own tools in it — so an existing one is added to rather than replaced,
    /// and an existing <c>docume</c> pin is left exactly as it is: a consumer who deliberately held
    /// the tool at an older version did not ask <c>init</c> to undo that.
    /// </summary>
    private static ScaffoldResult MergeToolManifest(string targetDir)
    {
        var fullPath = Combine(targetDir, ToolManifestPath);

        if (!File.Exists(fullPath))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, SerializeManifest(NewManifest()));
            return new ScaffoldResult(ToolManifestPath, ScaffoldAction.Created);
        }

        JsonObject manifest;
        try
        {
            // Parsed strictly, without the comment tolerance DocuMe's own files get: the SDK reads
            // this file too and does not accept comments, and a lenient read followed by a rewrite
            // would silently delete any a consumer had put there.
            manifest = JsonNode.Parse(File.ReadAllText(fullPath)) as JsonObject
                ?? throw new JsonException("the manifest's root is not a JSON object");
        }
        catch (JsonException exception)
        {
            var note = $"could not be read ({exception.Message.ReplaceLineEndings(" ")}), so "
                + $"{ToolCommandName} was not pinned. Fix the file and run `dotnet tool install "
                + $"{ToolPackageId}`, or the scaffolded workflows will fail at `dotnet tool restore`.";

            return new ScaffoldResult(ToolManifestPath, ScaffoldAction.Skipped, note);
        }

        var tools = manifest["tools"] as JsonObject;
        if (tools is null && manifest.ContainsKey("tools"))
        {
            const string note =
                $"has a 'tools' member that is not a JSON object, so {ToolCommandName} was not "
                + $"pinned. Fix the file and run `dotnet tool install {ToolPackageId}`.";

            return new ScaffoldResult(ToolManifestPath, ScaffoldAction.Skipped, note);
        }

        if (tools is null)
        {
            tools = [];
            manifest["tools"] = tools;
        }

        if (tools[ToolManifestKey] is JsonObject pinned)
        {
            return new ScaffoldResult(ToolManifestPath, ScaffoldAction.Skipped, DescribePin(pinned));
        }

        tools[ToolManifestKey] = NewToolEntry();
        File.WriteAllText(fullPath, SerializeManifest(manifest));

        var added = $"pinned {ToolManifestKey} {ToolVersion} in the existing manifest; its other tools "
            + "were left alone.";

        return new ScaffoldResult(ToolManifestPath, ScaffoldAction.Updated, added);
    }

    /// <summary>
    /// What to say about a pin that is already there: nothing when it matches this build, and the
    /// mismatch when it does not. A consumer whose workflows run an older <c>docume</c> than the one
    /// they just scaffolded templates from has a reason to know which.
    /// </summary>
    private static string? DescribePin(JsonObject pinned)
    {
        // Read through TryGetValue rather than GetValue: a manifest with a numeric "version" is
        // malformed, and GetValue would throw out of the command a consumer runs to fix such things.
        var version = pinned["version"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

        if (string.Equals(version, ToolVersion, StringComparison.Ordinal))
        {
            return null;
        }

        return $"already pins {ToolManifestKey} {version ?? "an unreadable version"} while this is "
            + $"{ToolVersion}; left as it is. Run `dotnet tool update {ToolPackageId}` to move it.";
    }

    private static JsonObject NewManifest() => new()
    {
        ["version"] = 1,
        ["isRoot"] = true,
        ["tools"] = new JsonObject { [ToolManifestKey] = NewToolEntry() },
    };

    /// <summary>
    /// The entry shape the SDK itself writes, field for field (verified against
    /// <c>dotnet tool install DocuMe.Cli --local</c>). <c>rollForward: false</c> included rather than
    /// omitted: it is the SDK's own default spelling, and a pinned tool that quietly rolled onto a
    /// newer runtime would defeat the point of pinning it.
    /// </summary>
    private static JsonObject NewToolEntry() => new()
    {
        ["version"] = ToolVersion,
        ["commands"] = new JsonArray(ToolCommandName),
        ["rollForward"] = false,
    };

    private static string SerializeManifest(JsonObject manifest)
        => manifest.ToJsonString(DocumeJson.Options) + Environment.NewLine;

    private static string ResolveToolVersion()
    {
        var assembly = typeof(ProjectScaffolder).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            // No SDK-generated attribute at all. The four-part assembly version still names the
            // release; it just cannot carry a prerelease suffix.
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // The SDK appends "+<commit sha>" (SourceLink ships on by default since .NET 8). NuGet
        // publishes 0.1.0, never 0.1.0+sha, so a manifest keeping the metadata restores nothing.
        var metadata = informational.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? informational : informational[..metadata];
    }

    /// <summary>
    /// Adds the one entry <c>init</c> earns the right to add: the Node dependency tree the mermaid
    /// render script it just scaffolded needs (<c>npm install beautiful-mermaid</c>). Deliberately one
    /// line rather than a plausible-looking set — everything else DocuMe writes into a consumer repo
    /// is meant to be committed (<c>_meta/state.json</c> and the feedback inbox both travel in the
    /// docs PRs, PLAN.md §5.3, §5.4), and every scratch file the workflows make goes to
    /// <c>$RUNNER_TEMP</c>.
    /// </summary>
    private static ScaffoldResult MergeGitignore(string targetDir)
    {
        const string relativePath = ".gitignore";
        var fullPath = Combine(targetDir, relativePath);

        if (!File.Exists(fullPath))
        {
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(
                fullPath,
                GitignoreComment + Environment.NewLine + GitignoreEntry + Environment.NewLine);
            return new ScaffoldResult(relativePath, ScaffoldAction.Created);
        }

        var existing = File.ReadAllText(fullPath);
        var covered = existing
            .Split('\n')
            .Any(line => GitignoreEquivalents.Contains(line.Trim(), StringComparer.Ordinal));

        if (covered)
        {
            return new ScaffoldResult(relativePath, ScaffoldAction.Skipped);
        }

        var appended = existing + Separator(existing) + GitignoreComment + Environment.NewLine
            + GitignoreEntry + Environment.NewLine;
        File.WriteAllText(fullPath, appended);

        return new ScaffoldResult(
            relativePath,
            ScaffoldAction.Updated,
            $"appended {GitignoreEntry} to the existing .gitignore.");
    }

    /// <summary>
    /// What has to go between a consumer's <c>.gitignore</c> and the appended entry: a blank line to
    /// separate the sections, plus a line terminator first if their file did not end with one (which
    /// would otherwise glue our comment onto the end of their last rule).
    /// </summary>
    private static string Separator(string existing) => existing.Length switch
    {
        0 => string.Empty,
        _ when existing.EndsWith('\n') => Environment.NewLine,
        _ => Environment.NewLine + Environment.NewLine,
    };

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
