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
