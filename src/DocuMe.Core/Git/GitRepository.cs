using System.ComponentModel;
using System.Diagnostics;

namespace DocuMe.Core.Git;

/// <summary>
/// The two git questions DocuMe asks: which commit a publish was made from (PLAN.md §6.2 step 8, §5.3)
/// and which files changed since a given commit (<c>publish --changed-since</c> in §6.2, and §6.4's
/// <c>drift</c>).
/// </summary>
/// <remarks>
/// <para>
/// It shells out rather than reading <c>.git/</c>: a packed ref, a linked worktree and a detached head
/// each spell <c>HEAD</c> differently while <c>rev-parse</c> answers all three, and a diff against a
/// working tree is not a file format at all.
/// </para>
/// <para>
/// <strong>The two questions fail differently, on purpose.</strong> A missing sha costs a stamp and
/// nothing else — a wiki published from a tarball is still a valid publish — so
/// <see cref="TryReadHeadAsync"/> answers <c>null</c> and lets the run continue. A caller that asked to
/// narrow a run to one commit must not be handed the whole tree instead, so
/// <see cref="ChangedFilesSinceAsync"/> throws <see cref="GitException"/>: publishing 79 pages because
/// git could not parse a sha is exactly the failure worth being loud about.
/// </para>
/// </remarks>
public static class GitRepository
{
    /// <summary>How long one git invocation gets before it is killed.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static readonly string[] HeadArguments = ["rev-parse", "HEAD"];

    /// <summary>
    /// The commit <paramref name="directory"/> is checked out at, or <c>null</c> when that is unknowable
    /// — no git on PATH, not a checkout, or a repository with no commit yet.
    /// </summary>
    /// <param name="directory">Any directory inside the repository.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<string?> TryReadHeadAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        GitOutput output;
        try
        {
            output = await RunAsync(directory, HeadArguments, cancellationToken).ConfigureAwait(false);
        }
        catch (GitException)
        {
            // Best effort by design: git absent, or git hung. Neither is worth failing a publish over,
            // and the caller reports the missing stamp once, in its own words.
            return null;
        }

        if (output.ExitCode != 0)
        {
            return null;
        }

        var sha = output.StandardOutput.Trim();

        return sha.Length == 0 ? null : sha;
    }

    /// <summary>
    /// The files under <paramref name="directory"/> that differ between <paramref name="sha"/> and the
    /// working tree, as paths relative to <paramref name="directory"/> with <c>/</c> separators.
    /// </summary>
    /// <param name="directory">
    /// The directory the answer is scoped and relative to — the wiki root for <c>--changed-since</c>,
    /// which is why the caller gets wiki-root-relative paths back and needs no prefix arithmetic.
    /// </param>
    /// <param name="sha">The commit to compare against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// git is absent, hung, or could not compare — including the common case of a sha this repository
    /// does not have.
    /// </exception>
    /// <remarks>
    /// Files git knows about, only: <c>git diff</c> cannot see a page that has never been added, so a
    /// brand-new untracked file is not in the answer. That is the flag's contract (§6.2 compares against
    /// a commit) and one more reason a whole-tree run stays the default.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> ChangedFilesSinceAsync(
        string directory,
        string sha,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        // --relative answers in paths relative to `directory` rather than to the repository root; those
        // are the same thing only when the wiki root happens to sit at the top of the repo. --no-renames
        // makes the answer independent of the caller's diff.renames config: a renamed page then shows up
        // as both paths, and both are facts a scope wants to see.
        string[] arguments = ["diff", "--name-only", "--relative", "--no-renames", sha, "--", "."];

        var output = await RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false);

        if (output.ExitCode != 0)
        {
            throw new GitException(
                $"git could not compare '{sha}' against the working tree in {directory}. Check that the "
                + $"sha exists here (`git -C {directory} rev-parse {sha}`) and that the directory is a git "
                + $"checkout. git said: {Describe(output.StandardError)}");
        }

        return [.. output.StandardOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static string Describe(string standardError)
    {
        var message = standardError.Trim();

        return message.Length == 0 ? "(nothing)" : message;
    }

    private static async Task<GitOutput> RunAsync(
        string directory,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // -C rather than WorkingDirectory so a directory that is not a checkout comes back as git's own
        // error message rather than as a process-start failure the caller has to translate.
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(directory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new GitException(
                $"git could not be started: {ex.Message} Is git installed and on PATH?", ex);
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
            throw new GitException(
                $"`git {arguments[0]}` did not finish within {Budget.TotalSeconds} seconds and was stopped.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        var error = await standardError.ConfigureAwait(false);
        var output = await standardOutput.ConfigureAwait(false);

        return new GitOutput(process.ExitCode, output, error);
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

    private sealed record GitOutput(int ExitCode, string StandardOutput, string StandardError);
}
