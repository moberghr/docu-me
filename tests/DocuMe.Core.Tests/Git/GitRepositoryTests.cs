using System.Diagnostics;
using DocuMe.Core.Git;
using Shouldly;

namespace DocuMe.Core.Tests.Git;

/// <summary>
/// The two git questions DocuMe asks (PLAN.md §6.2), against a real throwaway repository: a stub would
/// only prove that the code can parse output it invented, and the part worth pinning is what git actually
/// prints — which paths, relative to what.
/// </summary>
public sealed class GitRepositoryTests : IDisposable
{
    private const string WikiRoot = "docs/wiki";

    private readonly string _dir = Directory.CreateTempSubdirectory("docume-git-tests").FullName;
    private readonly string _notARepo = Directory.CreateTempSubdirectory("docume-git-tests-bare").FullName;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitRepositoryTests"/> class with an empty repository
    /// whose identity and signing come from flags, so a developer's global git config cannot change the
    /// outcome.
    /// </summary>
    public GitRepositoryTests()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "loop@example.com");
        Git("config", "user.name", "DocuMe loop");
        Git("config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_notARepo, recursive: true);
    }

    [Fact]
    public async Task Reads_the_commit_a_directory_is_checked_out_at()
    {
        Write($"{WikiRoot}/a.md", "# A\n");
        var sha = Commit("first");

        var head = await GitRepository.TryReadHeadAsync(_dir, TestContext.Current.CancellationToken);

        head.ShouldBe(sha);
        head.ShouldNotBeNull().Length.ShouldBe(40);
    }

    /// <summary>
    /// The answer <c>--changed-since</c> needs is wiki-root-relative, because that is what the plan keys
    /// on. A repo-relative answer would silently match nothing whenever <c>docume.json</c> is not at the
    /// top of the repository — which it is not, in the consumer repos this tool is for.
    /// </summary>
    [Fact]
    public async Task Answers_in_paths_relative_to_the_directory_it_was_asked_about()
    {
        Write($"{WikiRoot}/a.md", "# A\n");
        Write($"{WikiRoot}/images/logo.png", "not really a png");
        Write("README.md", "# The repo itself\n");
        var sha = Commit("first");

        Write($"{WikiRoot}/a.md", "# A, rewritten\n");
        Write("README.md", "# The repo itself, rewritten\n");
        Commit("second");

        var changed = await GitRepository.ChangedFilesSinceAsync(
            Path.Combine(_dir, "docs", "wiki"), sha, TestContext.Current.CancellationToken);

        // Wiki-root-relative, and the repo's own README — outside the wiki root — is not in the answer.
        changed.ShouldBe(["a.md"]);
    }

    /// <summary>
    /// A publish runs against the working tree, not against a commit, so an edit that is not committed yet
    /// still has to be in scope.
    /// </summary>
    [Fact]
    public async Task Sees_a_working_tree_edit_that_is_not_committed_yet()
    {
        Write($"{WikiRoot}/a.md", "# A\n");
        Write($"{WikiRoot}/b.md", "# B\n");
        var sha = Commit("first");

        Write($"{WikiRoot}/b.md", "# B, edited but not committed\n");

        var changed = await GitRepository.ChangedFilesSinceAsync(
            Path.Combine(_dir, "docs", "wiki"), sha, TestContext.Current.CancellationToken);

        changed.ShouldBe(["b.md"]);
    }

    [Fact]
    public async Task Reports_no_change_as_an_empty_answer_rather_than_a_failure()
    {
        Write($"{WikiRoot}/a.md", "# A\n");
        var sha = Commit("first");

        var changed = await GitRepository.ChangedFilesSinceAsync(
            Path.Combine(_dir, "docs", "wiki"), sha, TestContext.Current.CancellationToken);

        changed.ShouldBeEmpty();
    }

    /// <summary>
    /// The asymmetry that matters: a caller who asked to narrow a run cannot be handed the whole tree
    /// because git could not parse a sha, while a missing <c>lastPublishedSha</c> stamp costs nothing.
    /// </summary>
    [Fact]
    public async Task Refuses_a_sha_this_repository_does_not_have()
    {
        Write($"{WikiRoot}/a.md", "# A\n");
        Commit("first");

        var thrown = await Should.ThrowAsync<GitException>(async () => await GitRepository
            .ChangedFilesSinceAsync(_dir, "d00dfeed", TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("d00dfeed");
    }

    /// <summary>
    /// §6.4's question is between two commits, not against the working tree: a PR check compares the
    /// merge base with the branch tip, and an uncommitted edit on the CI runner is not part of the PR.
    /// </summary>
    [Fact]
    public async Task Compares_two_commits_and_ignores_the_working_tree()
    {
        Write("src/a.cs", "// a\n");
        var first = Commit("first");

        Write("src/b.cs", "// b\n");
        var second = Commit("second");

        Write("src/c.cs", "// uncommitted\n");

        var changed = await GitRepository.ChangedFilesBetweenAsync(
            _dir, first, second, TestContext.Current.CancellationToken);

        changed.ShouldBe(["src/b.cs"]);
    }

    [Fact]
    public async Task Refuses_a_range_this_repository_cannot_resolve()
    {
        Write("src/a.cs", "// a\n");
        var sha = Commit("first");

        var thrown = await Should.ThrowAsync<GitException>(async () => await GitRepository
            .ChangedFilesBetweenAsync(_dir, "d00dfeed", sha, TestContext.Current.CancellationToken));

        // The sha has to be in the message: a shallow CI clone hits this, and "drift check failed" with
        // no revision named sends the reader to the wrong place.
        thrown.Message.ShouldContain("d00dfeed");
    }

    /// <summary>
    /// The per-commit grain of the same §6.4 question: when <c>drift-ignore-revs</c> discards whole
    /// commits, each commit's own files must be knowable — newest first, as <c>git log</c> answers,
    /// and a file touched by two commits listed under both, because discarding one of them must not
    /// discard the other's evidence.
    /// </summary>
    [Fact]
    public async Task Attributes_changed_files_to_the_commits_that_touched_them()
    {
        Write("src/a.cs", "// a\n");
        var baseline = Commit("baseline");

        Write("src/a.cs", "// a, edited\n");
        Write("src/b.cs", "// b\n");
        var second = Commit("second");

        Write("src/b.cs", "// b, edited\n");
        var third = Commit("third");

        Write("src/a.cs", "// a, edited again\n");
        Write("src/c.cs", "// c\n");
        var fourth = Commit("fourth");

        var commits = await GitRepository.ChangedFilesByCommitAsync(
            _dir, baseline, fourth, TestContext.Current.CancellationToken);

        commits.Select(commit => commit.Sha).ShouldBe([fourth, third, second]);
        commits[0].Files.ShouldBe(["src/a.cs", "src/c.cs"]);
        commits[1].Files.ShouldBe(["src/b.cs"]);
        commits[2].Files.ShouldBe(["src/a.cs", "src/b.cs"]);
    }

    /// <summary>
    /// The two block shapes the parser must not misread, earned from git rather than assumed: a
    /// merge commit prints a bare sha with no file list under <c>--name-only</c>, and a commit
    /// whose changes all fall outside the asked-about directory prints the same bare-sha shape
    /// under <c>--relative</c> while still occupying the range. Both must come back as entries
    /// with empty file lists, in range, so the ignored-commit count sees them; and the in-scope
    /// commit's path must be spelled relative to the asked-about directory, which is the parity
    /// the flat diff's <c>--relative</c> promises.
    /// </summary>
    [Fact]
    public async Task Attributes_a_merge_and_an_out_of_scope_commit_as_bare_entries()
    {
        Write("sub/inside.cs", "// in\n");
        Write("outside.cs", "// out\n");
        var baseline = Commit("baseline");

        Write("outside.cs", "// out, edited\n");
        var outside = Commit("outside only");

        Git("checkout", "-q", "-b", "feature", baseline);
        Write("sub/inside.cs", "// in, edited on a branch\n");
        var branched = Commit("inside on a branch");

        Git("checkout", "-q", "-");
        Git("merge", "-q", "--no-ff", "-m", "merge feature", "feature");
        var merge = Git("rev-parse", "HEAD").Trim();

        var sub = Path.Combine(_dir, "sub");
        var commits = await GitRepository.ChangedFilesByCommitAsync(
            sub, baseline, merge, TestContext.Current.CancellationToken);

        // The merge is newest and leads; the two parents share a commit second, so git's traversal
        // order between them is not worth pinning — membership and per-commit attribution are.
        commits.Count.ShouldBe(3);
        commits[0].Sha.ShouldBe(merge);
        commits[0].Files.ShouldBeEmpty();

        var bySha = commits.ToDictionary(commit => commit.Sha, StringComparer.Ordinal);
        bySha[branched].Files.ShouldBe(["inside.cs"]);
        bySha[outside].Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_to_attribute_a_range_this_repository_cannot_resolve()
    {
        Write("src/a.cs", "// a\n");
        var sha = Commit("first");

        var thrown = await Should.ThrowAsync<GitException>(async () => await GitRepository
            .ChangedFilesByCommitAsync(_dir, "d00dfeed", sha, TestContext.Current.CancellationToken));

        // Same bar as the flat diff: the revision has to be in the message, because a shallow CI
        // clone hits this and an unnamed revision sends the reader to the wrong place.
        thrown.Message.ShouldContain("d00dfeed");
    }

    /// <summary>
    /// SC12: the candidate set a seal may match is git's index, so a gitignored build artifact is not in
    /// it and neither is a file nobody has added. That is what makes the seal and the drift check see one
    /// universe — <c>git diff</c> reports neither either — and what stops a page with
    /// <c>sources: ["src/**"]</c> in a repo that builds in-tree from sealing <c>bin/</c> and unsealing
    /// itself on the next rebuild (spec §4b defect F).
    /// </summary>
    [Fact]
    public async Task Lists_the_files_it_tracks_without_the_ones_it_ignores()
    {
        Write(".gitignore", "bin/\n");
        Write("src/Loans/Rate.cs", "// rate\n");
        Write("src/Loans/bin/Debug/DocuMe.dll", "not really a dll");
        Commit("first");

        // Added after the commit and never staged: untracked rather than ignored, and out either way.
        Write("src/Loans/Scratch.cs", "// scratch\n");

        var tracked = await GitRepository.TrackedFilesAsync(_dir, TestContext.Current.CancellationToken);

        tracked.ShouldBe([".gitignore", "src/Loans/Rate.cs"]);
    }

    /// <summary>
    /// Scoped and spelled like the diff's answer, because the two are matched against one page's globs:
    /// a seal computed over repo-root-relative paths and a drift check over directory-relative ones would
    /// disagree about every page in a repo where <c>docume.json</c> is not at the top.
    /// </summary>
    [Fact]
    public async Task Answers_tracked_files_relative_to_the_directory_it_was_asked_about()
    {
        Write($"{WikiRoot}/a.md", "# A\n");
        Write($"{WikiRoot}/images/logo.png", "not really a png");
        Write("README.md", "# The repo itself\n");
        Commit("first");

        var tracked = await GitRepository.TrackedFilesAsync(
            Path.Combine(_dir, "docs", "wiki"), TestContext.Current.CancellationToken);

        tracked.ShouldBe(["a.md", "images/logo.png"]);
    }

    /// <summary>
    /// A tracked file the working tree no longer has is still listed, because the index still holds it.
    /// It reaches the fingerprint as an unreadable file rather than as a missing one, which is the safe
    /// direction: deleting a documented source is drift, and the page must not seal through it.
    /// </summary>
    [Fact]
    public async Task Still_lists_a_tracked_file_deleted_from_the_working_tree()
    {
        Write("src/Loans/Rate.cs", "// rate\n");
        Commit("first");

        File.Delete(Path.Combine(_dir, "src", "Loans", "Rate.cs"));

        var tracked = await GitRepository.TrackedFilesAsync(_dir, TestContext.Current.CancellationToken);

        tracked.ShouldBe(["src/Loans/Rate.cs"]);
    }

    /// <summary>
    /// It throws rather than answering empty, which is
    /// <see cref="GitRepository.ChangedFilesBetweenAsync"/>'s rule turned around: an empty list is a
    /// legitimate answer that seals the empty-set fingerprint, so a
    /// failure returning one would seal "this page documents nothing" onto every page in the wiki.
    /// </summary>
    [Fact]
    public async Task Refuses_to_list_the_tracked_files_of_a_directory_that_is_not_a_checkout()
    {
        var thrown = await Should.ThrowAsync<GitException>(async () => await GitRepository
            .TrackedFilesAsync(_notARepo, TestContext.Current.CancellationToken));

        // The directory has to be in the message: the reader is at a terminal wondering which of their
        // paths git objected to.
        thrown.Message.ShouldContain(_notARepo);
    }

    [Fact]
    public async Task Treats_a_directory_that_is_not_a_checkout_as_no_sha_but_refuses_to_diff_it()
    {
        (await GitRepository.TryReadHeadAsync(_notARepo, TestContext.Current.CancellationToken))
            .ShouldBeNull();

        await Should.ThrowAsync<GitException>(async () => await GitRepository
            .ChangedFilesSinceAsync(_notARepo, "HEAD", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_repository_with_no_commit_yet_has_no_head()
    {
        (await GitRepository.TryReadHeadAsync(_dir, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>
    /// What git's path quoting actually contains, pinned by execution rather than by a doc comment. Three
    /// rounds of review retired a markdown-injection finding on the strength of "git quotes it", so the
    /// boundary is worth a test: git C-quotes a byte that would break a line, in <c>diff --name-only</c>
    /// and in <c>ls-files</c> alike, and it leaves a <strong>backtick</strong> exactly as it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a fact about git, not a claim any DocuMe output depends on.</strong>
    /// <c>DriftComment.Code</c> neutralizes every path it renders regardless, precisely so this file's
    /// behaviour is not load-bearing for another file's safety. What the test protects is the reasoning:
    /// a future reader who wants to lean on the quoting can see here how far it goes.
    /// </para>
    /// <para>
    /// <c>core.quotePath</c> is <strong>not</strong> the switch for any of this — it governs whether
    /// non-ASCII bytes are octal-escaped, and control bytes are C-quoted either way. Both invocations
    /// below are asserted under the setting turned off, which is the hostile direction; passing
    /// <c>-c core.quotePath=true</c> in <see cref="GitRepository"/> would therefore buy no containment
    /// this test does not already show is unconditional.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Quotes_a_line_break_in_a_path_and_leaves_a_backtick_alone()
    {
        const string windows = "Neither a line break nor a backtick is a legal filename byte on Windows; "
            + "the fact under test is about the platforms this suite's CI runs on.";

        Assert.SkipWhen(OperatingSystem.IsWindows(), windows);

        Git("config", "core.quotePath", "false");
        Write("src/plain.cs", "one");
        var sha = Commit("first");

        Write("src/break\nhere.cs", "two");
        Write("src/back`tick.cs", "three");
        Commit("second");

        var changed = await GitRepository.ChangedFilesSinceAsync(
            Path.Combine(_dir, "src"), sha, TestContext.Current.CancellationToken);

        // The line break never reaches the caller as a line break: git renders the whole path as one
        // C-quoted token, so `Lines` still sees one path per line.
        changed.ShouldBe([@"""break\nhere.cs""", "back`tick.cs"], ignoreOrder: true);
        changed.ShouldAllBe(path => !path.Contains('\n', StringComparison.Ordinal));

        // And the backtick is untouched — an ordinary printable byte, which is why a producer-side
        // containment argument could never have covered the code-span half of the class.
        var tracked = await GitRepository.TrackedFilesAsync(
            Path.Combine(_dir, "src"), TestContext.Current.CancellationToken);

        tracked.ShouldContain("back`tick.cs");
        tracked.ShouldContain(@"""break\nhere.cs""");
    }

    /// <summary>Commits everything in the tree and answers the new commit's sha.</summary>
    private string Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);

        return Git("rev-parse", "HEAD").Trim();
    }

    private string Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(_dir);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo).ShouldNotBeNull();

        // stderr drained concurrently with stdout: a sequential double read deadlocks once the
        // child fills the unread pipe (see ReleaseWorkflowTests.GitResult).
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var error = errorTask.GetAwaiter().GetResult();

        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)} failed: {error}{output}");

        return output;
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
