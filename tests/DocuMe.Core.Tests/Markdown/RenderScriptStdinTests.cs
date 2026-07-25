using System.Diagnostics;
using Shouldly;

namespace DocuMe.Core.Tests.Markdown;

/// <summary>
/// The stdin half of the shipped <c>templates/tools/render-mermaid.mjs</c> contract ("in: diagram
/// source on stdin"), exercised by spawning the script directly rather than through
/// <see cref="DocuMe.Core.Markdown.MermaidRenderer"/>.
/// </summary>
/// <remarks>
/// Spawning it here is the point: the renderer writes the source the instant the process starts, so
/// through it the arrival timing cannot be varied, and timing is exactly what this script got wrong.
/// Skips when <c>beautiful-mermaid</c> is absent, like every other real-script test.
/// </remarks>
public sealed class RenderScriptStdinTests
{
    [Fact]
    public async Task Waits_for_a_source_that_arrives_after_the_process_started()
    {
        var script = BundledRenderScript.TryFind();
        Assert.SkipUnless(script is not null, BundledRenderScript.DependencyMissingReason);

        // Regression guard for the full-suite flake: .NET hands the child a NON-BLOCKING stdin
        // pipe, so a synchronous read of fd 0 returns EAGAIN whenever the source has not landed
        // yet, and the script died with exit 4 ("could not read the diagram source"). A publish
        // hits that timing on its own — one Node process per diagram, several at once — and it
        // failed 5 of 21 full test runs before the script started reading the stream instead.
        // The delay below turns that race into a certainty.
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(script!);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        var standardOutput = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        try
        {
            await process.StandardInput.WriteAsync(
                "graph TD\n  A --> B".AsMemory(),
                TestContext.Current.CancellationToken);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // A script that already gave up on its input broke this pipe. Say so with its own
            // diagnostic below rather than with a write stack that names neither cause.
        }

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var diagnostic = await standardError;
        diagnostic.ShouldNotContain("EAGAIN");
        process.ExitCode.ShouldBe(0, diagnostic);
        (await standardOutput).ShouldStartWith("<svg");
    }
}
