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
/// <paramref name="SvgWidth"/> is what PLAN.md §7's <c>&lt;ac:image ac:width="…"&gt;</c> is measured
/// from. The converter cannot do it — a pure text transform never renders, so it never knows a
/// width — so the publish path adds the attribute to the body it uploads, after the §8 hash was
/// taken over the converter's output. <see cref="Publishing.DiagramImageWidth"/> holds that step and
/// the reasoning behind it.
/// </para>
/// <para>
/// <paramref name="SvgHeight"/> is read but not published: <c>ac:height</c> is not in §7, and a
/// height alongside a width is what distorts a diagram when Confluence would otherwise scale it
/// proportionally. Kept because it costs one regex on a string already in hand, and a caller that
/// wants to report a diagram's shape should not have to re-parse the SVG for half of it.
/// </para>
/// </remarks>
public sealed record MermaidDiagram(
    string AttachmentFilename,
    string Svg,
    string? SvgWidth,
    string? SvgHeight);

/// <summary>Who is at fault when a diagram does not render.</summary>
/// <remarks>
/// A bulk run needs this distinction to stay honest. A rejected diagram is one finding among many
/// and the run should keep going and count it; a broken setup rejects <em>every</em> diagram
/// identically, so tallying those as findings would report a broken machine as a broken corpus
/// (<see cref="Acceptance.MermaidAcceptance"/>).
/// </remarks>
public enum MermaidRenderFault
{
    /// <summary>
    /// This diagram was refused. Another diagram may still render, so a bulk run counts it and
    /// carries on. A render that timed out is classified here too: it is one diagram's pathology,
    /// and a genuinely stuck Node shows up as every diagram timing out.
    /// </summary>
    Diagram,

    /// <summary>
    /// Nothing could have rendered: Node missing, the script missing, <c>beautiful-mermaid</c> not
    /// installed, or a script that returned something other than an SVG.
    /// </summary>
    Setup,
}

/// <summary>
/// Thrown when a mermaid diagram cannot be rendered. Always loud, never a fallback: a diagram
/// that silently failed would publish a page with a broken image, and PLAN.md §7 makes an
/// unrenderable construct fail its page rather than degrade it.
/// </summary>
public sealed class MermaidRenderException : Exception
{
    public MermaidRenderException(string message, MermaidRenderFault fault)
        : base(message) => Fault = fault;

    public MermaidRenderException(string message, MermaidRenderFault fault, Exception innerException)
        : base(message, innerException) => Fault = fault;

    /// <summary>Whether the diagram was refused or the setup made rendering impossible.</summary>
    /// <remarks>
    /// A constructor parameter rather than a defaulted property on purpose: a future throw site
    /// that forgot to classify itself would silently become a row in an acceptance report.
    /// </remarks>
    public MermaidRenderFault Fault { get; }
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
                + "docume.json -> mermaid.renderer at it (PLAN.md §4, §5.1).",
                MermaidRenderFault.Setup);
        }

        var result = await RunScriptAsync(mermaidSource, cancellationToken).ConfigureAwait(false);
        var svg = result.StandardOutput;

        if (result.ExitCode == DependencyMissingExitCode)
        {
            throw new MermaidRenderException(
                "The mermaid render script could not load beautiful-mermaid. Install it where "
                + $"Node resolves it from '{_scriptPath}' (usually the repo root): "
                + $"npm install beautiful-mermaid. Script said: {Describe(result.StandardError)}",
                MermaidRenderFault.Setup);
        }

        if (result.ExitCode != 0)
        {
            throw new MermaidRenderException(
                $"The mermaid render script failed (exit {result.ExitCode}) on this diagram: "
                + $"{Describe(result.StandardError)}",
                MermaidRenderFault.Diagram);
        }

        // A script that printed a warning, a banner or nothing at all would otherwise be
        // uploaded verbatim as the diagram — the silent failure this check exists to prevent.
        if (!LooksLikeSvg(svg))
        {
            throw new MermaidRenderException(
                "The mermaid render script exited successfully but did not return an SVG "
                + $"document. It wrote: {Describe(svg)}",
                MermaidRenderFault.Setup);
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
                MermaidRenderFault.Setup,
                ex);
        }

        // Drain both pipes before writing stdin: a diagram whose SVG outgrows the pipe buffer
        // would otherwise deadlock, the child blocked on write and us blocked on stdin.
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        await WriteSourceAsync(process, mermaidSource, cancellationToken).ConfigureAwait(false);

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
                + "The diagram may be pathologically large, or Node may be stuck.",
                MermaidRenderFault.Diagram);
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

    /// <summary>
    /// Feeds the diagram to the script, tolerating a script that stopped listening.
    /// </summary>
    /// <remarks>
    /// A script that refuses the run before reading stdin breaks this pipe — a missing
    /// <c>beautiful-mermaid</c> (exit 3) is the everyday case, and it is a race, since a script
    /// that exits at once may or may not do so before the write lands. The script's own verdict is
    /// its exit code plus stderr, both read below, so surfacing the broken pipe instead would
    /// replace an install instruction with an OS error decided by process scheduling.
    /// </remarks>
    private static async Task WriteSourceAsync(
        Process process,
        string mermaidSource,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(mermaidSource.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Nothing to add: the exit code and stderr say what happened, and a script that
            // never wanted the source cannot have failed because of it.
        }
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
