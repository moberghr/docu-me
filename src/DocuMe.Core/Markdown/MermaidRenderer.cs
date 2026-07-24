using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DocuMe.Core.Markdown;

/// <summary>
/// A rendered mermaid diagram: the SVG document to upload, and the attachment filename it
/// goes under.
/// </summary>
/// <param name="AttachmentFilename">
/// From <see cref="MermaidAttachmentName.ForSource"/> — a pure function of the diagram source,
/// so it is stable across publishes (PLAN.md §8).
/// </param>
/// <param name="Svg">The SVG document verbatim, exactly as the render script produced it.</param>
/// <param name="SvgWidth">
/// The <c>width</c> attribute of the SVG root element, verbatim (e.g. <c>212.64</c>), or
/// <c>null</c> when the document carries none.
/// </param>
/// <param name="SvgHeight">The <c>height</c> attribute of the SVG root element, verbatim.</param>
/// <remarks>
/// <para>
/// <paramref name="SvgWidth"/> and <paramref name="SvgHeight"/> settle an open spec question:
/// PLAN.md §7's mermaid row shows <c>&lt;ac:image ac:width="…"&gt;</c>, but the converter is a
/// pure text transform that never renders, so it cannot know a width. The render script can,
/// and does. Honoring §7's <c>ac:width</c> is therefore possible but not free: it needs
/// <see cref="MermaidDiagramResolver"/> to carry a width alongside the filename, which changes
/// the converter's seam and its goldens. Deferred to its own slice; the data is here so that
/// slice needs no new investigation.
/// </para>
/// </remarks>
public sealed record MermaidDiagram(
    string AttachmentFilename,
    string Svg,
    string? SvgWidth,
    string? SvgHeight);

/// <summary>
/// Thrown when a mermaid diagram cannot be rendered. Always loud, never a fallback: a diagram
/// that silently failed would publish a page with a broken image, and PLAN.md §7 makes an
/// unrenderable construct fail its page rather than degrade it.
/// </summary>
public sealed class MermaidRenderException : Exception
{
    public MermaidRenderException(string message)
        : base(message)
    {
    }

    public MermaidRenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Renders <c>```mermaid</c> fence bodies to SVG by shelling out to Node and the bundled
/// <c>render-mermaid.mjs</c> (PLAN.md §4). This is the half of the mermaid path that the
/// converter deliberately does not own: the converter stays a filesystem-free, process-free
/// text transform and consumes only <see cref="MermaidDiagramResolver"/>, while rendering,
/// caching and upload belong to the publish pipeline (§6.2 step 3).
/// </summary>
/// <remarks>
/// <para>
/// Verified on beautiful-mermaid 1.1.3: the SVG for a given source is byte-identical across
/// separate Node processes, which is what keeps the <c>attachments</c> hashes in
/// <c>_meta/state.json</c> (§5.3) stable and stops every publish from re-uploading every
/// diagram.
/// </para>
/// </remarks>
public sealed partial class MermaidRenderer
{
    /// <summary>Exit code the script uses for "beautiful-mermaid is not installed".</summary>
    private const int DependencyMissingExitCode = 3;

    private const int MatchTimeoutMilliseconds = 1000;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly string _scriptPath;
    private readonly string _nodeExecutable;
    private readonly TimeSpan _timeout;

    /// <param name="scriptPath">
    /// Path to <c>render-mermaid.mjs</c>. In a consumer repo this comes from
    /// <c>docume.json</c> → <c>mermaid.renderer</c> (§5.1).
    /// </param>
    /// <param name="nodeExecutable">
    /// The Node executable, resolved through <c>PATH</c> by default. §4 requires Node ≥ 20.
    /// </param>
    /// <param name="timeout">
    /// How long one diagram may take before the process is killed. Guards a bulk publish
    /// against a hung renderer; defaults to 30 seconds.
    /// </param>
    public MermaidRenderer(string scriptPath, string nodeExecutable = "node", TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeExecutable);

        _scriptPath = scriptPath;
        _nodeExecutable = nodeExecutable;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Renders one diagram. The source goes to the script on stdin and the SVG comes back on
    /// stdout, so nothing is written to disk and the caller decides where the bytes land.
    /// </summary>
    /// <exception cref="MermaidRenderException">
    /// Node is missing, the script is missing, the script rejected the diagram, it timed out,
    /// or what came back on stdout is not an SVG document.
    /// </exception>
    public async Task<MermaidDiagram> RenderAsync(
        string mermaidSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mermaidSource);

        if (!File.Exists(_scriptPath))
        {
            throw new MermaidRenderException(
                $"The mermaid render script was not found at '{_scriptPath}'. It ships with "
                + "DocuMe and is scaffolded by `docume init` as tools/render-mermaid.mjs; point "
                + "docume.json -> mermaid.renderer at it (PLAN.md §4, §5.1).");
        }

        var result = await RunScriptAsync(mermaidSource, cancellationToken).ConfigureAwait(false);
        var svg = result.StandardOutput;

        if (result.ExitCode == DependencyMissingExitCode)
        {
            throw new MermaidRenderException(
                "The mermaid render script could not load beautiful-mermaid. Install it where "
                + $"Node resolves it from '{_scriptPath}' (usually the repo root): "
                + $"npm install beautiful-mermaid. Script said: {Describe(result.StandardError)}");
        }

        if (result.ExitCode != 0)
        {
            throw new MermaidRenderException(
                $"The mermaid render script failed (exit {result.ExitCode}) on this diagram: "
                + $"{Describe(result.StandardError)}");
        }

        // A script that printed a warning, a banner or nothing at all would otherwise be
        // uploaded verbatim as the diagram — the silent failure this check exists to prevent.
        if (!LooksLikeSvg(svg))
        {
            throw new MermaidRenderException(
                "The mermaid render script exited successfully but did not return an SVG "
                + $"document. It wrote: {Describe(svg)}");
        }

        var (width, height) = ReadRootSize(svg);
        return new MermaidDiagram(MermaidAttachmentName.ForSource(mermaidSource), svg, width, height);
    }

    private async Task<ScriptResult> RunScriptAsync(string mermaidSource, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_nodeExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(_scriptPath);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            // §4's named requirement: a clear message when Node is missing, not a raw OS error.
            throw new MermaidRenderException(
                $"Could not start Node ('{_nodeExecutable}') to render a mermaid diagram. "
                + "DocuMe renders diagrams by shelling out to Node (≥ 20 required, PLAN.md §4); "
                + "install it, or remove the ```mermaid fences from the page.",
                ex);
        }

        // Drain both pipes before writing stdin: a diagram whose SVG outgrows the pipe buffer
        // would otherwise deadlock, the child blocked on write and us blocked on stdin.
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.StandardInput.WriteAsync(mermaidSource.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            var seconds = _timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            throw new MermaidRenderException(
                $"The mermaid render script did not finish within {seconds}s and was killed. "
                + "The diagram may be pathologically large, or Node may be stuck.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new ScriptResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone between the timeout firing and the kill — nothing to do.
        }
    }

    private static bool LooksLikeSvg(string standardOutput)
    {
        var trimmed = standardOutput.AsSpan().TrimStart();
        return trimmed.StartsWith("<svg", StringComparison.Ordinal)
            || trimmed.StartsWith("<?xml", StringComparison.Ordinal);
    }

    private static (string? Width, string? Height) ReadRootSize(string svg)
    {
        var start = svg.IndexOf("<svg", StringComparison.Ordinal);
        if (start < 0)
        {
            return (null, null);
        }

        var end = svg.IndexOf('>', start);
        if (end < 0)
        {
            return (null, null);
        }

        var rootTag = svg[start..end];
        return (MatchAttribute(WidthAttribute(), rootTag), MatchAttribute(HeightAttribute(), rootTag));
    }

    private static string? MatchAttribute(Regex pattern, string rootTag)
    {
        var match = pattern.Match(rootTag);
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>
    /// Quotes a diagnostic for an exception message, so an empty or huge one stays readable.
    /// </summary>
    private static string Describe(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return "(nothing)";
        }

        const int limit = 400;
        var text = trimmed.Length <= limit
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, limit), "… (truncated)");

        return $"'{text}'";
    }

    // The lookbehind keeps these off stroke-width= and any other hyphenated attribute.
    // Both patterns are linear, but they run over renderer output, so they carry a timeout
    // rather than trusting that (MA0009).
    [GeneratedRegex(
        "(?<![-\\w])width=\"(?<value>[^\"]*)\"",
        RegexOptions.ExplicitCapture,
        MatchTimeoutMilliseconds)]
    private static partial Regex WidthAttribute();

    [GeneratedRegex(
        "(?<![-\\w])height=\"(?<value>[^\"]*)\"",
        RegexOptions.ExplicitCapture,
        MatchTimeoutMilliseconds)]
    private static partial Regex HeightAttribute();

    private sealed record ScriptResult(int ExitCode, string StandardOutput, string StandardError);
}
