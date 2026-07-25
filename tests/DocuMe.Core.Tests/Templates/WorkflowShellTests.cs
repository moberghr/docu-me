using System.Diagnostics;
using Shouldly;
using YamlDotNet.RepresentationModel;

namespace DocuMe.Core.Tests.Templates;

/// <summary>
/// The shell inside the workflow files, run rather than read. <see cref="WorkflowTemplateTests"/> asserts
/// what the yaml says; this class asserts what bash does with it.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>run:</c> block in this repository is code that executes for the first time in somebody else's
/// runner. Nothing else in the suite touches it: <c>dotnet build</c> does not read yaml, and a text
/// assertion cannot tell a script that works from one that dies on line nine. Two kinds of check live
/// here — <c>bash -n</c> over every block in every workflow, and real execution of the one step whose
/// failure destroys data.
/// </para>
/// <para>
/// That step is the carry: copy the machine-owned artifacts aside, force-switch to <c>docs/sync</c>, copy
/// them back, stage, commit, push, open the PR. It holds the page id of every published page and the
/// <c>repliedAt</c> stamp that stops a reviewer being answered twice, and the exit codes of the three
/// steps above it are deliberately held so that a failed run still reaches it. A carry that dies throws
/// away exactly what the holding was for, so the shapes below are the ones where it used to die.
/// </para>
/// <para>
/// The scripts are extracted from the shipped yaml, never retyped: a test against a retyped copy proves
/// nothing about the file <c>docume init</c> scaffolds. GitHub substitutes <c>${{ … }}</c> before bash
/// sees a script, so <see cref="Substitute"/> does the same with the real paths.
/// </para>
/// </remarks>
public sealed class WorkflowShellTests : IDisposable
{
    private const string StatePath = "docs/wiki/_meta/state.json";
    private const string InboxPath = "docs/wiki/_meta/feedback/inbox";
    private const string ArchivePath = "docs/wiki/_meta/feedback/archive";
    private const string StateBranch = "docs/sync";

    private static readonly string RepoRoot = Locate();

    private readonly List<string> _scratch = [];

    public void Dispose()
    {
        foreach (var directory in _scratch.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Every <c>run:</c> block in every workflow — the six consumer templates and this repository's own
    /// CI and release workflows — parses as bash.
    /// </summary>
    /// <remarks>
    /// The release workflow earns this most: its version guard runs once, on a tag push, and a syntax
    /// error there is found by the release it refuses to cut. Its notes heredoc also dedents to column 0
    /// out of a yaml block scalar, which is exactly the shape that is easy to break by re-indenting.
    /// </remarks>
    [Fact]
    public void Every_run_block_in_every_workflow_parses_as_bash()
    {
        var blocks = AllRunBlocks();

        // A list-driven assertion that found nothing would pass while proving nothing: 8 files carry run
        // blocks, so an empty list means the extractor broke, not that the shell is clean.
        const string vacuous = "the extractor found almost no run blocks, so this test checked almost nothing.";
        blocks.Count.ShouldBeGreaterThan(40, vacuous);

        var broken = new List<string>();

        foreach (var block in blocks)
        {
            var script = CreateFile(NewScratch("bash-n"), "step.sh", Substitute(block.Script));
            var result = Run("bash", ["-n", script], Path.GetDirectoryName(script)!);

            if (result.Code is not 0)
            {
                broken.Add($"{block.Source} [{block.Job}] {block.Name}: {result.Error.Trim()}");
            }
        }

        broken.ShouldBeEmpty($"bash rejected {broken.Count} of {blocks.Count} run blocks.");
    }

    /// <summary>
    /// The first run: nothing has ever been published, so the state file is untracked and the branch does
    /// not exist. Also the guard that stops every other case here passing vacuously — if the harness could
    /// not drive one successful carry, a step that died early would look like a pass.
    /// </summary>
    [Theory]
    [InlineData("docs-publish.yml")]
    [InlineData("docs-sync.yml")]
    public void Opens_the_pull_request_on_the_first_run(string template)
    {
        var repo = NewConsumerRepo(template, prExists: false);
        WriteState(repo, """{"pages":{"123":"Home"}}""");

        var result = RunCarry(repo);

        result.Code.ShouldBe(0, result.Diagnostics);
        BranchFiles(repo).ShouldContain(StatePath);
        repo.GhCalls.ShouldContain(call => call.StartsWith("pr create", StringComparison.Ordinal));
    }

    /// <summary>Nothing dirty means no branch, no commit and no PR — and no <c>gh</c> call at all.</summary>
    [Theory]
    [InlineData("docs-publish.yml")]
    [InlineData("docs-sync.yml")]
    public void Commits_nothing_when_nothing_changed(string template)
    {
        var repo = NewConsumerRepo(template, prExists: false);
        WriteState(repo, """{"pages":{}}""");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "state");

        var result = RunCarry(repo);

        result.Code.ShouldBe(0, result.Diagnostics);
        BranchExists(repo).ShouldBeFalse("a no-op run pushed a branch.");
        repo.GhCalls.ShouldBeEmpty("a no-op run called gh.");
    }

    /// <summary>
    /// A run that has feedback to carry but no state file to carry with it. The guard above the carry
    /// passes when <em>any</em> of its paths is dirty, so this reaches the copy — and it used to die there
    /// on <c>cp: No such file or directory</c>, discarding the reviewer's comment.
    /// </summary>
    /// <remarks>
    /// Reachable in both templates. In <c>docs-sync.yml</c> it is the job's own documented case: ingestion
    /// writes inbox items before <c>StateStore.Save</c> runs, so a sync that died leaves items and no
    /// state, and the held exit code exists precisely so the carry still rescues them. In
    /// <c>docs-publish.yml</c> it needs a publish that failed before writing state — a 401 hard-stops per
    /// rule §1.2 — on a repo whose inbox was already dirty.
    /// </remarks>
    [Theory]
    [InlineData("docs-publish.yml")]
    [InlineData("docs-sync.yml")]
    public void Carries_the_feedback_when_there_is_no_state_file_to_carry(string template)
    {
        var repo = NewConsumerRepo(template, prExists: false);
        WriteFeedback(repo, "inbox", "f-009.json", """{"id":"f-009","status":"untriaged"}""");

        var result = RunCarry(repo);

        result.Code.ShouldBe(0, result.Diagnostics);
        BranchFiles(repo).ShouldContain($"{InboxPath}/f-009.json");
    }

    /// <summary>
    /// A state file git tracks and the working tree has lost. <c>git status</c> reports the deletion, so
    /// the guard lets the run through, and the PR it opens must still carry a state file — page ids are the
    /// one thing here that cannot be reconstructed, since approvals come back from the live labels on the
    /// next sync but a lost id makes the next publish create a second page per title.
    /// </summary>
    /// <remarks>
    /// What saves it is the forced switch, not the staging guard: <c>git checkout -f</c> restores whatever
    /// the target ref tracks, so the file is back before <c>git add</c> runs and the deletion never reaches
    /// the index. Measured, not assumed — reverting the staging guard leaves this test green and turns
    /// <see cref="Carries_the_feedback_when_there_is_no_state_file_to_carry"/> red instead, because there
    /// the path is in no ref at all and <c>git add</c> is fatal.
    /// </remarks>
    [Theory]
    [InlineData("docs-publish.yml")]
    [InlineData("docs-sync.yml")]
    public void Carries_a_state_file_even_when_the_tree_lost_its_tracked_copy(string template)
    {
        var repo = NewConsumerRepo(template, prExists: false);
        WriteState(repo, """{"pages":{"123":"Home"}}""");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "state");
        Git(repo, "push", "origin", "main");
        File.Delete(Path.Combine(repo.Work, StatePath));
        WriteFeedback(repo, "inbox", "f-010.json", """{"id":"f-010"}""");

        var result = RunCarry(repo);

        result.Code.ShouldBe(0, result.Diagnostics);
        var files = BranchFiles(repo);
        files.ShouldContain($"{InboxPath}/f-010.json");
        const string deleted = "the PR proposes deleting the state file, and every page id with it.";
        files.Contains(StatePath, StringComparer.Ordinal).ShouldBeTrue(deleted);
    }

    /// <summary>
    /// The same shape, with a good state file already on the branch: the run has nothing better to offer,
    /// so it must leave that copy exactly as it found it.
    /// </summary>
    [Theory]
    [InlineData("docs-publish.yml")]
    [InlineData("docs-sync.yml")]
    public void Leaves_the_branch_state_alone_when_the_tree_lost_its_own(string template)
    {
        var repo = NewConsumerRepo(template, prExists: true);
        WriteState(repo, """{"pages":{"123":"Home"}}""");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "state");
        Git(repo, "push", "origin", "main");
        SeedStateBranch(repo, """{"pages":{"123":"Home","456":"Other"}}""");

        File.Delete(Path.Combine(repo.Work, StatePath));
        WriteFeedback(repo, "inbox", "f-011.json", """{"id":"f-011"}""");

        var result = RunCarry(repo);

        result.Code.ShouldBe(0, result.Diagnostics);
        const string lost = "the branch's state file was overwritten with a worse one, or lost.";
        BranchFile(repo, StatePath).Contains("456", StringComparison.Ordinal).ShouldBeTrue(lost);
    }

    /// <summary>
    /// A <c>docs/sync</c> that carries no <c>_meta</c> directory — a branch the consumer already had under
    /// that name. The forced switch prunes the directory along with the tracked state file, so the
    /// copy-back needs its parent created first, which is why the <c>mkdir</c> now precedes it.
    /// </summary>
    [Theory]
    [InlineData("docs-publish.yml")]
    [InlineData("docs-sync.yml")]
    public void Copies_the_state_back_onto_a_branch_that_carries_no_meta_directory(string template)
    {
        var repo = NewConsumerRepo(template, prExists: false);
        SeedStateBranch(repo, stateBody: null);

        WriteState(repo, """{"pages":{"123":"Home"}}""");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "state");
        Git(repo, "push", "origin", "main");
        WriteState(repo, """{"pages":{"123":"Home","789":"New"}}""");

        var result = RunCarry(repo);

        result.Code.ShouldBe(0, result.Diagnostics);
        BranchFile(repo, StatePath).ShouldContain("789");
    }

    /// <summary>
    /// <c>docs-publish.yml</c> only: the reply pass edits an archive item in place, and that
    /// <c>repliedAt</c> stamp is the only thing stopping the next publish answering the same comment
    /// again. It has to survive the copy-aside, the forced switch and the copy-back.
    /// </summary>
    [Fact]
    public void Carries_the_replied_stamp_on_an_archive_item_edited_in_place()
    {
        var repo = NewConsumerRepo("docs-publish.yml", prExists: false);
        WriteFeedback(repo, "archive", "f-001.json", """{"id":"f-001","status":"archived"}""");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "archived item");
        Git(repo, "push", "origin", "main");

        WriteState(repo, """{"pages":{"123":"Home"}}""");
        const string stamped = """{"id":"f-001","status":"archived","repliedAt":"2026-07-25T00:00:00Z"}""";
        WriteFeedback(repo, "archive", "f-001.json", stamped);

        var result = RunCarry(repo);

        result.Code.ShouldBe(0, result.Diagnostics);
        BranchFile(repo, $"{ArchivePath}/f-001.json").ShouldContain("repliedAt");
    }

    /// <summary>
    /// The paths this class substitutes into the carry are the paths the workflow's own <c>paths</c> step
    /// emits. Without this, a change to the <c>_meta</c> layout would leave every execution test above
    /// exercising paths no runner ever produces, and all of them would stay green.
    /// </summary>
    [Theory]
    [InlineData("docs-publish.yml")]
    [InlineData("docs-sync.yml")]
    public void Substitutes_the_paths_the_workflow_itself_derives(string template)
    {
        var paths = RunBlocks(template).Single(step => step.Id is "paths").Script;

        paths.ShouldContain("_meta/state.json");
        paths.ShouldContain("_meta/feedback/inbox");
    }

    // ---- extraction -------------------------------------------------------------------------------
    private static List<RunBlock> AllRunBlocks()
    {
        string[] directories = ["templates/workflows", ".github/workflows"];
        var blocks = new List<RunBlock>();

        foreach (var directory in directories)
        {
            var full = Path.Combine(RepoRoot, directory);

            foreach (var file in Directory.EnumerateFiles(full, "*.yml").Order(StringComparer.Ordinal))
            {
                blocks.AddRange(RunBlocksIn(Path.Combine(directory, Path.GetFileName(file))));
            }
        }

        return blocks;
    }

    private static List<RunBlock> RunBlocks(string template)
        => RunBlocksIn(Path.Combine("templates/workflows", template));

    private static List<RunBlock> RunBlocksIn(string relativePath)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot, relativePath)));

        // Load, not Deserialize: an indentation error throws here, which is the failure hand-written yaml
        // actually has.
        stream.Load(reader);

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children.Single(child => IsKey(child.Key, "jobs")).Value;
        var blocks = new List<RunBlock>();

        foreach (var job in jobs.Children)
        {
            var steps = ((YamlMappingNode)job.Value).Children
                .SingleOrDefault(child => IsKey(child.Key, "steps"))
                .Value;

            if (steps is not YamlSequenceNode sequence)
            {
                continue;
            }

            foreach (var step in sequence.Children.OfType<YamlMappingNode>())
            {
                var run = Scalar(step, "run");

                if (run.Length is not 0)
                {
                    blocks.Add(new RunBlock(
                        relativePath, Scalar(job.Key), Scalar(step, "id"), Scalar(step, "name"), run));
                }
            }
        }

        return blocks;
    }

    /// <summary>
    /// <c>${{ … }}</c> replaced the way a runner replaces it, before bash is handed the script. Scanned
    /// rather than matched with a pattern, so a nested brace cannot be read as the end of an expression.
    /// </summary>
    private static string Substitute(string script)
    {
        var result = new System.Text.StringBuilder(script.Length);
        var rest = script.AsSpan();

        while (true)
        {
            var open = rest.IndexOf("${{", StringComparison.Ordinal);

            if (open < 0)
            {
                result.Append(rest);
                return result.ToString();
            }

            result.Append(rest[..open]);
            var tail = rest[open..];
            var close = tail.IndexOf("}}", StringComparison.Ordinal);

            close.ShouldBeGreaterThan(-1, "an unterminated ${{ expression is a yaml bug.");
            result.Append(Value(tail[3..close].Trim().ToString()));
            rest = tail[(close + 2)..];
        }
    }

    /// <summary>
    /// What each expression stands for. The three <c>paths</c> outputs are the real thing; everything else
    /// is a secret or a runner value that only has to be a word for the syntax check.
    /// </summary>
    private static string Value(string expression) => expression switch
    {
        "steps.paths.outputs.state" => StatePath,
        "steps.paths.outputs.inbox" => InboxPath,
        "steps.paths.outputs.archive" => ArchivePath,
        _ => "GHA_EXPR",
    };

    private static bool IsKey(YamlNode node, string name)
        => string.Equals(((YamlScalarNode)node).Value, name, StringComparison.Ordinal);

    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value ?? string.Empty;

    private static string Scalar(YamlMappingNode node, string key)
        => node.Children.FirstOrDefault(child => IsKey(child.Key, key)).Value is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;

    // ---- the throwaway consumer repository --------------------------------------------------------
    /// <summary>
    /// A repository shaped like a consumer's, with an on-disk bare origin and a <c>gh</c> that records what
    /// it was asked. Everything the carry step does is local git except one API call, so this answers all
    /// of it.
    /// </summary>
    private ConsumerRepo NewConsumerRepo(string template, bool prExists)
    {
        var root = NewScratch("carry");
        var directory = Path.Combine(root, "repo");
        var origin = Path.Combine(root, "origin.git");
        var runnerTemp = Path.Combine(root, "runner-temp");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(runnerTemp);

        Run("git", ["init", "--bare", "-q", "-b", "main", origin], root).Code.ShouldBe(0);

        var repo = new ConsumerRepo(template, directory, origin, runnerTemp, StubGh(root, prExists));
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "config", "user.email", "loop@example.com");
        Git(repo, "config", "user.name", "DocuMe loop");
        Git(repo, "config", "commit.gpgsign", "false");
        CreateFile(directory, "docume.json", """{"wiki":{"root":"docs/wiki"}}""");
        CreateFile(Path.Combine(directory, "docs", "wiki"), "index.md", "# Home\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "initial");
        Git(repo, "remote", "add", "origin", origin);
        Git(repo, "push", "-q", "-u", "origin", "main");

        return repo;
    }

    /// <summary>
    /// A <c>gh</c> on <c>PATH</c> that logs its arguments and answers <c>pr view</c> per the scenario —
    /// found means the PR is already open, so the step must not create a second one.
    /// </summary>
    private static string StubGh(string root, bool prExists)
    {
        var bin = Path.Combine(root, "stub-bin");
        var log = Path.Combine(root, "gh-calls.log");
        var exit = prExists ? 0 : 1;
        var script = $"""
            #!/bin/bash
            printf '%s\n' "$*" >> '{log}'
            if [ "$1" = 'pr' ] && [ "$2" = 'view' ]; then exit {exit}; fi
            exit 0
            """;
        var path = CreateFile(bin, "gh", script);

        // The stub has to be executable, and every scenario that uses it runs bash anyway.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return bin;
    }

    /// <summary>Runs the repository's carry step, extracted from its own template.</summary>
    private StepResult RunCarry(ConsumerRepo repo)
    {
        var carry = RunBlocks(repo.Template).Single(step => step.Script.Contains("gh pr create", StringComparison.Ordinal));
        var script = CreateFile(repo.Work, ".carry-step.sh", Substitute(carry.Script));
        var output = Path.Combine(repo.RunnerTemp, "github-output");
        File.WriteAllText(output, string.Empty);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = $"{repo.StubBin}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
            ["RUNNER_TEMP"] = repo.RunnerTemp,
            ["GITHUB_SHA"] = "abcdef1234567890abcdef1234567890abcdef12",
            ["GITHUB_OUTPUT"] = output,
            ["STATE_BRANCH"] = StateBranch,
            ["GH_TOKEN"] = "stub-token",
        };

        var result = Run("bash", [script], repo.Work, environment);
        var log = Path.Combine(Path.GetDirectoryName(repo.Work)!, "gh-calls.log");
        repo.GhCalls.AddRange(File.Exists(log)
            ? File.ReadAllLines(log).Where(line => line.Length is not 0)
            : []);

        return new StepResult(result.Code, result.Output, result.Error, carry.Name);
    }

    /// <summary>Seeds <c>origin/docs/sync</c>, with or without a state file of its own.</summary>
    private void SeedStateBranch(ConsumerRepo repo, string? stateBody)
    {
        var seed = Path.Combine(NewScratch("seed"), "clone");
        Run("git", ["clone", "-q", repo.Origin, seed], Path.GetDirectoryName(seed)!).Code.ShouldBe(0);

        var clone = repo with { Work = seed };
        Git(clone, "config", "user.email", "loop@example.com");
        Git(clone, "config", "user.name", "DocuMe loop");
        Git(clone, "config", "commit.gpgsign", "false");
        Git(clone, "checkout", "-q", "-b", StateBranch);

        if (stateBody is null)
        {
            CreateFile(seed, "UNRELATED.md", "a branch the consumer already had\n");
        }

        if (stateBody is not null)
        {
            CreateFile(Path.Combine(seed, "docs", "wiki", "_meta"), "state.json", stateBody);
        }

        Git(clone, "add", "-A");
        Git(clone, "commit", "-q", "-m", "branch content");
        Git(clone, "push", "-q", "origin", StateBranch);
    }

    private static void WriteState(ConsumerRepo repo, string body)
        => CreateFile(Path.Combine(repo.Work, "docs", "wiki", "_meta"), "state.json", body);

    private static void WriteFeedback(ConsumerRepo repo, string bucket, string file, string body)
        => CreateFile(
            Path.Combine(repo.Work, "docs", "wiki", "_meta", "feedback", bucket), file, body);

    /// <summary>The paths on <c>origin/docs/sync</c>, or empty when the branch was never pushed.</summary>
    private static List<string> BranchFiles(ConsumerRepo repo)
    {
        var result = Run("git", ["ls-tree", "-r", "--name-only", StateBranch], repo.Origin);

        if (result.Code is not 0)
        {
            return [];
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static bool BranchExists(ConsumerRepo repo)
        => Run("git", ["rev-parse", "--verify", StateBranch], repo.Origin).Code is 0;

    private static string BranchFile(ConsumerRepo repo, string path)
    {
        var result = Run("git", ["show", $"{StateBranch}:{path}"], repo.Origin);
        result.Code.ShouldBe(0, $"{path} is not on {StateBranch}: {result.Error}");

        return result.Output;
    }

    private static void Git(ConsumerRepo repo, params string[] arguments)
    {
        var result = Run("git", arguments, repo.Work);
        result.Code.ShouldBe(0, $"git {string.Join(' ', arguments)} failed: {result.Error}");
    }

    // ---- process plumbing -------------------------------------------------------------------------
    private static ProcessResult Run(
        string file, string[] arguments, string workingDirectory, Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            info.Environment.Clear();

            foreach (var (key, value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"{file} did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, output, error);
    }

    private string NewScratch(string prefix)
    {
        var directory = Directory.CreateTempSubdirectory($"docume-shell-{prefix}").FullName;
        _scratch.Add(directory);

        return directory;
    }

    private static string CreateFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);

        return path;
    }

    /// <summary>
    /// The workflows live in the tree, not beside the test assembly: <c>init</c> scaffolds these exact
    /// files, so the test has to read the shipped copy.
    /// </summary>
    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the workflows cannot be found.");
    }

    private sealed record RunBlock(string Source, string Job, string Id, string Name, string Script);

    private sealed record ProcessResult(int Code, string Output, string Error);

    private sealed record StepResult(int Code, string Output, string Error, string Step)
    {
        /// <summary>Everything a failure needs, since the interesting half is usually on stderr.</summary>
        internal string Diagnostics => $"""
            "{Step}" exited {Code}.
            stdout: {Output}
            stderr: {Error}
            """;
    }

    private sealed record ConsumerRepo(
        string Template, string Work, string Origin, string RunnerTemp, string StubBin)
    {
        internal List<string> GhCalls { get; } = [];
    }
}
