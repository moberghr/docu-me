using System.ComponentModel;
using System.Diagnostics;

namespace DocuMe.Cli;

/// <summary>
/// Reads the consumer repo's <c>HEAD</c> commit, which a publish stamps into state as
/// <c>lastPublishedSha</c> (PLAN.md §6.2 step 8, §5.3).
/// </summary>
/// <remarks>
/// <para>
/// Best effort by design: a wiki published from a tarball, or from a directory that is not a git
/// checkout, is still a valid publish, so a missing sha costs the stamp and nothing else.
/// </para>
/// <para>
/// It shells out to <c>git rev-parse</c> rather than reading <c>.git/HEAD</c> because a packed ref, a
/// linked worktree and a detached head each spell that file differently while <c>rev-parse</c> answers
/// all three.
/// </para>
/// </remarks>
internal static class GitHead
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>The commit <paramref name="repoRoot"/> is checked out at, or <c>null</c> if unknowable.</summary>
    public static async Task<string?> TryReadAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repoRoot);
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            // No git on PATH. Nothing to report: the caller says so once, in its own words.
            return null;
        }

        // Both pipes are drained before the wait, so git can never block writing into a full one.
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Budget);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return null;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        _ = await standardError.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            // Not a repository, or a repository with no commit yet. Both are "no sha", not a failure.
            return null;
        }

        var sha = (await standardOutput.ConfigureAwait(false)).Trim();

        return sha.Length == 0 ? null : sha;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone between the timeout firing and the kill.
        }
    }
}
