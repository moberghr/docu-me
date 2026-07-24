using DocuMe.Core.Markdown;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// <see cref="MermaidRenderer"/>'s contract with the Node script (PLAN.md §4).
/// </summary>
/// <remarks>
/// <para>
/// Most of these drive <em>stub</em> scripts this class writes to a temp directory. That is
/// deliberate: the renderer's job is process plumbing and failure mapping, and a stub pins
/// every branch of it — exit codes, stderr, a non-SVG stdout, a hang, an output too big for a
/// pipe buffer — with no npm dependency and no flakiness.
/// </para>
/// <para>
/// The handful of tests that run the <em>real</em> <c>templates/tools/render-mermaid.mjs</c>
/// need <c>beautiful-mermaid</c> installed (<c>npm ci</c> at the repo root). They skip with a
/// reason rather than failing when it is absent, so a clone without Node packages still gets a
/// green <c>dotnet test</c> — the skip message says what to run.
/// </para>
/// </remarks>
public sealed class MermaidRendererTests : IDisposable
{
    private const string DependencyMissingReason = BundledRenderScript.DependencyMissingReason;

    private readonly string _stubDirectory = Path.Combine(
        Path.GetTempPath(),
        "docume-mermaid-tests",
        Guid.NewGuid().ToString("N"));

    public MermaidRendererTests() => Directory.CreateDirectory(_stubDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_stubDirectory))
        {
            Directory.Delete(_stubDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Feeds_the_source_on_stdin_and_returns_stdout_as_the_svg()
    {
        // Echoing stdin back inside an <svg> wrapper proves the whole pipe: the script receives
        // the diagram source verbatim and the renderer keeps what came back byte-for-byte.
        var script = WriteStub(
            """
            import { readFileSync } from 'node:fs';
            const source = readFileSync(0, 'utf8');
            process.stdout.write(`<svg width="212.64" height="297.5">${source}</svg>`);
            """);

        var result = await new MermaidRenderer(script).RenderAsync(
            "graph TD\n  A --> B",
            TestContext.Current.CancellationToken);

        result.Svg.ShouldBe("<svg width=\"212.64\" height=\"297.5\">graph TD\n  A --> B</svg>");
        result.SvgWidth.ShouldBe("212.64");
        result.SvgHeight.ShouldBe("297.5");
        result.AttachmentFilename.ShouldBe(MermaidAttachmentName.ForSource("graph TD\n  A --> B"));
    }

    [Fact]
    public async Task Reads_the_root_size_past_a_hyphenated_attribute()
    {
        // stroke-width= must not be mistaken for width=.
        var script = WriteStub("""process.stdout.write('<svg stroke-width="3" width="10" height="20"><g/></svg>');""");

        var result = await new MermaidRenderer(script).RenderAsync("graph TD\n  A --> B", TestContext.Current.CancellationToken);

        result.SvgWidth.ShouldBe("10");
        result.SvgHeight.ShouldBe("20");
    }

    [Fact]
    public async Task Survives_an_svg_larger_than_a_pipe_buffer()
    {
        // Regression guard for the deadlock this renderer is written to avoid: if it wrote
        // stdin before draining stdout, a big diagram would hang both processes forever.
        var script = WriteStub(
            """
            process.stdout.write('<svg width="1" height="1">');
            process.stdout.write('x'.repeat(2_000_000));
            process.stdout.write('</svg>');
            """);

        var result = await new MermaidRenderer(script).RenderAsync("graph TD\n  A --> B", TestContext.Current.CancellationToken);

        result.Svg.Length.ShouldBeGreaterThan(2_000_000);
    }

    [Fact]
    public async Task Missing_node_fails_loud_and_names_the_prerequisite()
    {
        // PLAN.md §4 names this requirement outright: "Fallback error message if node missing".
        var script = WriteStub("""process.stdout.write('<svg/>');""");
        var renderer = new MermaidRenderer(script, nodeExecutable: "docume-no-such-node-executable");

        var error = await Should.ThrowAsync<MermaidRenderException>(
            () => renderer.RenderAsync("graph TD\n  A --> B", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("docume-no-such-node-executable");
        error.Message.ShouldContain("Node");
    }

    [Fact]
    public async Task Missing_script_fails_loud_and_names_the_path()
    {
        var missing = Path.Combine(_stubDirectory, "not-scaffolded.mjs");
        var renderer = new MermaidRenderer(missing);

        var error = await Should.ThrowAsync<MermaidRenderException>(
            () => renderer.RenderAsync("graph TD\n  A --> B", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("not-scaffolded.mjs");
        error.Message.ShouldContain("docume init");
    }

    [Fact]
    public async Task Dependency_missing_exit_code_becomes_an_install_instruction()
    {
        // Exit 3 is the script's "beautiful-mermaid is not installed". The fix is npm install,
        // not editing the diagram, so it must not be reported as a bad diagram.
        var script = WriteStub(
            """
            process.stderr.write('render-mermaid: cannot load beautiful-mermaid');
            process.exitCode = 3;
            """);

        var error = await Should.ThrowAsync<MermaidRenderException>(
            () => new MermaidRenderer(script).RenderAsync("graph TD\n  A --> B", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("npm install beautiful-mermaid");
        error.Message.ShouldContain("cannot load beautiful-mermaid");
    }

    [Fact]
    public async Task Render_failure_surfaces_the_scripts_own_diagnostic()
    {
        var script = WriteStub(
            """
            process.stderr.write('render-mermaid: the diagram did not render: Invalid mermaid header');
            process.exitCode = 2;
            """);

        var error = await Should.ThrowAsync<MermaidRenderException>(
            () => new MermaidRenderer(script).RenderAsync("nosuch TD\n  A --> B", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("exit 2");
        error.Message.ShouldContain("Invalid mermaid header");
    }

    [Fact]
    public async Task Success_that_did_not_return_svg_fails_loud()
    {
        // The silent failure this guard exists for: whatever lands on stdout is uploaded as the
        // diagram, so a script that printed a banner instead of SVG must not pass as rendered.
        var script = WriteStub("""process.stdout.write('Update available: beautiful-mermaid 2.0.0');""");

        var error = await Should.ThrowAsync<MermaidRenderException>(
            () => new MermaidRenderer(script).RenderAsync("graph TD\n  A --> B", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("did not return an SVG");
        error.Message.ShouldContain("Update available");
    }

    [Fact]
    public async Task Empty_stdout_fails_loud_rather_than_publishing_an_empty_attachment()
    {
        var script = WriteStub("""process.exitCode = 0;""");

        var error = await Should.ThrowAsync<MermaidRenderException>(
            () => new MermaidRenderer(script).RenderAsync("graph TD\n  A --> B", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("did not return an SVG");
        error.Message.ShouldContain("(nothing)");
    }

    [Fact]
    public async Task A_hung_script_is_killed_at_the_timeout()
    {
        // A bulk publish walks dozens of diagrams; one stuck Node must not stall the run.
        var script = WriteStub("""setTimeout(() => process.stdout.write('<svg/>'), 60_000);""");
        var renderer = new MermaidRenderer(script, timeout: TimeSpan.FromSeconds(2));

        var error = await Should.ThrowAsync<MermaidRenderException>(
            () => renderer.RenderAsync("graph TD\n  A --> B", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("did not finish within 2s");
    }

    [Fact]
    public async Task Empty_source_is_refused_before_a_process_starts()
    {
        var renderer = new MermaidRenderer(Path.Combine(_stubDirectory, "never-read.mjs"));

        await Should.ThrowAsync<ArgumentException>(
            () => renderer.RenderAsync("   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Renders_a_real_diagram_through_the_bundled_script()
    {
        var script = TryFindRealScript();
        Assert.SkipUnless(script is not null, DependencyMissingReason);

        const string source = "graph TD\n  A[Loan request] --> B{Approved?}";
        var result = await new MermaidRenderer(script!).RenderAsync(source, TestContext.Current.CancellationToken);

        result.Svg.ShouldStartWith("<svg");
        result.Svg.ShouldEndWith("</svg>");
        result.AttachmentFilename.ShouldBe(MermaidAttachmentName.ForSource(source));
        result.SvgWidth.ShouldNotBeNullOrWhiteSpace();
        result.SvgHeight.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_real_renderer_is_byte_stable_across_processes()
    {
        var script = TryFindRealScript();
        Assert.SkipUnless(script is not null, DependencyMissingReason);

        // This is what keeps _meta/state.json's attachment hashes (§5.3) still: if the same
        // diagram rendered differently twice, every publish would re-upload every diagram.
        const string source = "graph TD\n  A --> B";
        var renderer = new MermaidRenderer(script!);

        var first = await renderer.RenderAsync(source, TestContext.Current.CancellationToken);
        var second = await renderer.RenderAsync(source, TestContext.Current.CancellationToken);

        second.Svg.ShouldBe(first.Svg);
        second.AttachmentFilename.ShouldBe(first.AttachmentFilename);
    }

    [Fact]
    public async Task The_real_renderer_rejects_a_header_semicolon_rather_than_guessing()
    {
        var script = TryFindRealScript();
        Assert.SkipUnless(script is not null, DependencyMissingReason);

        // Pins a known dialect gap, discovered by probe: beautiful-mermaid is a
        // reimplementation of mermaid, and `graph TD;` — which mermaid.js and GitHub both
        // accept — is rejected. It fails the page loudly instead of drawing something else.
        // Whether to shim it is a decision for the 79-page run, not for this renderer.
        var error = await Should.ThrowAsync<MermaidRenderException>(
            () => new MermaidRenderer(script!).RenderAsync("graph TD;\nA --> B", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("Invalid mermaid header");
    }

    private static string? TryFindRealScript() => BundledRenderScript.TryFind();

    private string WriteStub(string body)
    {
        var path = Path.Combine(_stubDirectory, "stub.mjs");
        File.WriteAllText(path, body);
        return path;
    }
}
