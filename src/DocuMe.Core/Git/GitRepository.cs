using System.ComponentModel;
using System.Diagnostics;

namespace DocuMe.Core.Git;

/// <summary>
/// The git questions DocuMe asks: which commit a publish was made from (PLAN.md §6.2 step 8, §5.3),
/// which files changed since a given commit (<c>publish --changed-since</c> in §6.2, and §6.4's
/// <c>drift</c> — as one flat list, or commit by commit when <c>drift-ignore-revs</c> needs to know
/// who touched what), and which files the repository tracks at all
/// (<see cref="TrackedFilesAsync"/>, the sealed verdict's candidate set).
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

    private static readonly string[] TrackedArguments = ["ls-files"];

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

        return await DiffAsync(
            directory,
            sha,
            $"compare '{sha}' against the working tree",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The files under <paramref name="directory"/> that differ between two commits, as paths relative
    /// to <paramref name="directory"/> with <c>/</c> separators — §6.4's <c>drift</c> input.
    /// </summary>
    /// <param name="directory">
    /// The directory the answer is scoped and relative to. For <c>drift</c> that is the directory
    /// holding <c>docume.json</c>, because a page's <c>sources</c> globs are written relative to it
    /// (§5.1) and the two have to line up for a glob to match anything at all.
    /// </param>
    /// <param name="baseline">The commit to compare from.</param>
    /// <param name="head">The commit to compare to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// git is absent, hung, or could not compare — including a revision this repository does not have,
    /// which a shallow CI clone or a force-pushed branch produces routinely.
    /// </exception>
    /// <remarks>
    /// <strong>It throws rather than answering empty, and that is the whole point.</strong> Zero drift
    /// is what a green advisory check shows, so a zero that really means "git could not resolve the
    /// baseline" is the one wrong answer here that a reviewer would believe.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> ChangedFilesBetweenAsync(
        string directory,
        string baseline,
        string head,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline);
        ArgumentException.ThrowIfNullOrWhiteSpace(head);

        var range = $"{baseline}..{head}";

        return await DiffAsync(directory, range, $"compare '{range}'", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Every commit in <c>baseline..head</c> with the files it touched under
    /// <paramref name="directory"/>, newest first as <c>git log</c> answers, paths relative to
    /// <paramref name="directory"/> with <c>/</c> separators — §6.4's <c>drift</c> attribution for
    /// when <c>_meta/drift-ignore-revs</c> asks whole commits, not paths, to be held out of the scan.
    /// </summary>
    /// <param name="directory">
    /// The directory the file lists are scoped and relative to — the same directory
    /// <see cref="ChangedFilesBetweenAsync"/> gets, so the union over surviving commits spells every
    /// path exactly as the single diff it replaces would, and the two routes through §6.4 can never
    /// disagree about a file's name.
    /// </param>
    /// <param name="baseline">The commit the range opens after (itself excluded).</param>
    /// <param name="head">The commit the range runs to (itself included).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// git is absent, hung, or could not walk the range — including a revision this repository does
    /// not have, which a shallow CI clone or a force-pushed branch produces routinely. Like
    /// <see cref="ChangedFilesBetweenAsync"/> it throws rather than answering empty, because an empty
    /// answer that really means "git could not resolve the baseline" is the one wrong answer a
    /// reviewer would believe.
    /// </exception>
    /// <remarks>
    /// <para>
    /// One <c>git log --format=%H --name-only</c> invocation answers the whole range: the commit
    /// count of a sweep-heavy branch must not become a process count.
    /// </para>
    /// <para>
    /// <strong>A merge commit contributes no files.</strong> <c>--name-only</c> prints a merge as a
    /// bare sha with no list, so a merge's work arrives here attributed to the merged commits
    /// themselves, which are in the range and carry their own lists. What escapes is a change born in
    /// the merge itself — a conflict resolution — and the attribution path that consumes this answer
    /// documents that trade where its readers look.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<CommitChanges>> ChangedFilesByCommitAsync(
        string directory,
        string baseline,
        string head,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline);
        ArgumentException.ThrowIfNullOrWhiteSpace(head);

        var range = $"{baseline}..{head}";

        // --relative and --no-renames for the same reasons DiffAsync gives them: this answer must
        // spell every path exactly as the single diff it replaces would, or the revs route through
        // §6.4 and the plain route would disagree whenever the wiki repo nests its config. No
        // `-- .` pathspec, deliberately, though DiffAsync passes one: a pathspec simplifies
        // history and drops out-of-scope commits from the walk entirely, and the ignored-commit
        // count must see every commit in the range, empty-listed or not.
        string[] arguments = ["log", "--format=%H", "--name-only", "--relative", "--no-renames", range];

        var output = await RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false);

        if (output.ExitCode != 0)
        {
            throw CouldNot($"walk '{range}' commit by commit", directory, range, output.StandardError);
        }

        return ParseLog(output.StandardOutput);
    }

    /// <summary>
    /// Every file git tracks under <paramref name="directory"/>, as paths relative to it with <c>/</c>
    /// separators — the candidate set a sealed source fingerprint may match
    /// (docs/specs/2026-08-19-sealed-source-verdicts.md §3.1, as amended).
    /// </summary>
    /// <param name="directory">
    /// The directory the answer is scoped and relative to. For the seal that is the directory holding
    /// <c>docume.json</c>, because a page's <c>sources</c> globs are written relative to it (§5.1) — the
    /// same directory <see cref="ChangedFilesBetweenAsync"/> is asked about, and <c>git ls-files</c>
    /// answers relative to its own working directory exactly as <c>--relative</c> makes the diff do.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GitException">
    /// git is absent, hung, or could not list — most often because this directory is not a checkout at
    /// all, which is a publish from a tarball or an unpacked archive.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <strong>This is the index, which is exactly the point.</strong> A gitignored path is not in it,
    /// and neither is an untracked one — so a page with <c>sources: ["src/**"]</c> in a repo that builds
    /// in-tree cannot seal <c>bin/</c> into its fingerprint and unseal itself on the next rebuild
    /// (spec §4b defect F). The list drift matches against comes from <c>git diff</c>, which reports
    /// neither either, so the seal and the check see one universe by construction.
    /// </para>
    /// <para>
    /// <strong>It throws rather than answering empty</strong>, for
    /// <see cref="ChangedFilesBetweenAsync"/>'s reason turned around: an empty candidate list is a
    /// legitimate value that seals the empty-set fingerprint, so a failure that returned one would seal
    /// "this page documents nothing" onto every page in the wiki. The caller decides what an
    /// unanswerable question means — the CLI publishes without sealing and says so.
    /// </para>
    /// <para>
    /// A tracked file deleted from the working tree is still listed, because the index still holds it.
    /// That reaches the fingerprint as an unreadable file rather than as a missing one, which is the safe
    /// direction: deleting a documented source is drift, and the page must not seal through it.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<string>> TrackedFilesAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var output = await RunAsync(directory, TrackedArguments, cancellationToken).ConfigureAwait(false);

        if (output.ExitCode != 0)
        {
            throw new GitException(
                $"git could not list the files it tracks in {directory}. Check that the directory is a "
                + $"git checkout (`git -C {directory} rev-parse --git-dir`). git said: "
                + Describe(output.StandardError));
        }

        return Lines(output.StandardOutput);
    }

    private static async Task<IReadOnlyList<string>> DiffAsync(
        string directory,
        string revisions,
        string attempt,
        CancellationToken cancellationToken)
    {
        // --relative answers in paths relative to `directory` rather than to the repository root; those
        // are the same thing only when the wiki root happens to sit at the top of the repo. --no-renames
        // makes the answer independent of the caller's diff.renames config: a renamed page then shows up
        // as both paths, and both are facts a scope wants to see.
        string[] arguments = ["diff", "--name-only", "--relative", "--no-renames", revisions, "--", "."];

        var output = await RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false);

        if (output.ExitCode != 0)
        {
            throw CouldNot(attempt, directory, revisions, output.StandardError);
        }

        return Lines(output.StandardOutput);
    }

    /// <summary>
    /// A one-path-per-line answer as a list. One spelling for every path question, so the diff and
    /// <see cref="TrackedFilesAsync"/> cannot end up disagreeing about how a path is written.
    /// </summary>
    /// <remarks>
    /// Nothing is unquoted. Under the default <c>core.quotePath</c> git renders a non-ASCII path as a
    /// C-quoted string (<c>"src/R\303\251.cs"</c>) — and it does so identically in
    /// <c>diff --name-only</c> and in <c>ls-files</c>, verified against git 2.54. Decoding it here would
    /// have to be done identically on both sides forever to keep the seal and the drift check matching
    /// the same spelling, so the cheaper guarantee is to decode on neither: such a path matches a glob in
    /// both places or in neither, which is the property this file exists to hold.
    /// </remarks>
    private static IReadOnlyList<string> Lines(string standardOutput) =>
        [.. standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// The failure every range question shares, worded for a terminal: what was asked, what to check,
    /// and what git said. One spelling on purpose, so the "check the revisions exist" guidance cannot
    /// drift between the flat diff and the per-commit walk.
    /// </summary>
    private static GitException CouldNot(
        string attempt,
        string directory,
        string revisions,
        string standardError) =>
        new($"git could not {attempt} in {directory}. Check that the revisions exist here "
            + $"(`git -C {directory} rev-parse {revisions}`) and that the directory is a git "
            + $"checkout. git said: {Describe(standardError)}");

    /// <summary>
    /// Parses <c>git log --format=%H --name-only</c> output. The block shape (pinned against git
    /// 2.54): a commit is its <c>%H</c> line, a blank line, then one path per line; a merge — or a
    /// commit whose changes all fall outside the asked-about directory — is a bare <c>%H</c> with
    /// the next one right behind it. So a 40-hex line opens a commit, any other non-blank line is a
    /// file of the commit above it, and blank lines carry no information. A path spelled as exactly
    /// forty hex characters would be mistaken for a commit; a line-based format cannot tell them
    /// apart, and no real tree names its files that way.
    /// </summary>
    private static List<CommitChanges> ParseLog(string standardOutput)
    {
        var commits = new List<CommitChanges>();
        string? sha = null;
        var files = new List<string>();

        foreach (var raw in standardOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsCommitSha(line))
            {
                Flush();
                sha = line;
            }
            else if (sha is not null)
            {
                files.Add(line);
            }
        }

        Flush();

        return commits;

        void Flush()
        {
            if (sha is not null)
            {
                commits.Add(new CommitChanges(sha, [.. files]));
                files.Clear();
            }
        }
    }

    /// <summary>A full 40-character hex object name — how <c>--format=%H</c> spells a commit.</summary>
    private static bool IsCommitSha(string line) =>
        line.Length == 40 && line.All(char.IsAsciiHexDigit);

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

/// <summary>One commit in a <c>baseline..head</c> walk and the files it touched.</summary>
/// <param name="Sha">The commit's full 40-hex object name, lowercase as <c>git log</c> prints it.</param>
/// <param name="Files">
/// The files this commit touched under the directory the walk was asked about, relative to it with
/// <c>/</c> separators. Empty for a merge commit (<c>--name-only</c> gives a merge no list) and for a
/// commit whose changes all fall outside that directory.
/// </param>
public sealed record CommitChanges(string Sha, IReadOnlyList<string> Files);
