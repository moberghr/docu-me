using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DocuMe.Core.State;
using Shouldly;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Cli;

/// <summary>
/// SC11 (docs/specs/2026-08-19-sealed-source-verdicts.md §4b): a real <c>docume publish</c> seals. The
/// four inputs the seal needs — the repo root, each page's <c>sources</c> globs, git's tracked files and
/// the moment — exist only where the CLI assembles them, so nothing below the command can prove the
/// feature is switched on. Batch 1 shipped the seal with no caller and it was inert in the binary
/// (defect C); this is the test that would have said so.
/// </summary>
/// <remarks>
/// <para>
/// A class of its own rather than more cases in <see cref="CliConfluenceTests"/> because the fixture is
/// different in kind: this one needs a git repository around the scaffolded consumer repo, which is what
/// makes <c>git ls-files</c> answerable and therefore what makes a seal happen at all. Every other CLI
/// fixture deliberately runs in a bare temp directory.
/// </para>
/// <para>
/// Nothing here can reach Confluence (rule §4.2): the base URL is a loopback port WireMock opened for
/// this test, and <c>docume init</c> is pointed at it so the address travels the real route.
/// </para>
/// </remarks>
public sealed class CliPublishTests : IDisposable
{
    private const string SpaceKey = "SBX";
    private const string SpaceId = "98304";

    /// <summary>The one page <c>docume init</c> scaffolds, as the state file keys it.</summary>
    private const string HomePath = "README.md";

    /// <summary>The page this fixture adds, the one that declares <c>sources</c>.</summary>
    private const string GuidePath = "guides/setup.md";

    /// <summary>The one source file the guide's glob matches, and its bytes.</summary>
    private const string SourceFile = "src/Loans/Rate.cs";

    private const string SourceBytes = "rate\n";

    /// <summary>
    /// The fingerprint of the corpus <see cref="SourceFile"/> alone makes up: <c>sha256</c> of
    /// <c>"src/Loans/Rate.cs\nsha256:6682b…7fdf\n"</c>, whose per-file hash is <c>sha256</c> of
    /// <c>"rate\n"</c>. Computed with <c>shasum -a 256</c> outside this code, like every other pinned
    /// hash here: a value read back out of <c>SourcesFingerprint</c> would pin nothing.
    /// </summary>
    private const string RateOnly = "sha256:d37215c86a098d53e6a93103dd719a3183df07732e3fc215b8e7b57d520cb1fc";

    /// <summary>
    /// The fingerprint of no files at all — the one value no publish may ever record, named here so the
    /// assertions below can say what the failure they guard against would have looked like in state.
    /// </summary>
    private const string EmptySet = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly WireMockServer _server = WireMockServer.Start();

    private readonly string _root = Directory.CreateTempSubdirectory("docume-cli-publish").FullName;

    private int _nextPageId = 800000;

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// SC11. The assertion is the fingerprint's pinned value rather than "a verdict is present", because
    /// the failure this guards against is not a missing seal — it is a seal computed against the wrong
    /// root, which is a real-looking <c>sha256:</c> that no later run can ever match.
    /// </summary>
    [Fact]
    public void Publish_seals_the_sources_of_every_page_it_writes()
    {
        var work = Scaffolded(nameof(Publish_seals_the_sources_of_every_page_it_writes));
        WritePage(work, GuidePath, "src/**");
        WriteFile(work, SourceFile, SourceBytes);
        var sha = Commit(work);

        StubSpace();
        StubCreate();

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);

        var guide = State(work).Pages[GuidePath].Verdict;

        guide.ShouldNotBeNull($"`docume publish` sealed nothing.{Environment.NewLine}{run.Diagnostics}");
        guide!.SourcesHash.ShouldBe(RateOnly, run.Diagnostics);

        // The run's own commit, which is what makes a seal auditable rather than merely trusted (§3.2):
        // a publish from a dirty tree seals uncommitted bytes and only this field can reveal it.
        guide.RepoSha.ShouldBe(sha, run.Diagnostics);

        // Round-tripped as the timestamp §5.3 spells everywhere else, and in UTC.
        DateTimeOffset
            .TryParseExact(
                guide.SealedAt,
                "yyyy-MM-ddTHH:mm:ssZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out _)
            .ShouldBeTrue($"sealedAt was '{guide.SealedAt}'.{Environment.NewLine}{run.Diagnostics}");

        // A page that declares no sources matches no file, so it is not sealed at all (spec §3.1 as
        // revised 2026-08-19). Sealing it would put the empty-set fingerprint in state, and that value
        // recomputes identically under every condition that produced it.
        State(work).Pages[HomePath].Verdict.ShouldBeNull(run.Diagnostics);
    }

    /// <summary>
    /// The feature's worst failure mode, at the point it enters the system. <c>git ls-files</c> exits 0
    /// and prints nothing in a checkout with an empty index — and prints no source file at all in a
    /// sparse checkout cone'd to <c>docs/</c>, which is an ordinary CI job. Every page's globs then match
    /// nothing, every page seals the empty-set constant, and a later drift run under the same structural
    /// condition recomputes that constant, matches it, and holds the whole wiki out of the report. So a
    /// successful-but-empty answer is refused exactly as a failed one is, and said out loud.
    /// </summary>
    [Fact]
    public void Publish_against_an_empty_tracked_file_list_seals_nothing_and_says_so()
    {
        var work = Scaffolded(nameof(Publish_against_an_empty_tracked_file_list_seals_nothing_and_says_so));
        WritePage(work, GuidePath, "src/**");
        WriteFile(work, SourceFile, SourceBytes);

        // A real checkout with nothing in its index: `git ls-files` exits 0 and answers nothing, which
        // no exception guard can see.
        Git(work, "init", "-q", "-b", "main");
        Git(work, "config", "user.email", "loop@example.com");
        Git(work, "config", "user.name", "DocuMe loop");

        StubSpace();
        StubCreate();

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);
        run.Flowed.ShouldContain("not being sealed", customMessage: run.Diagnostics);

        var state = State(work);
        var because = $"An empty `git ls-files` sealed {EmptySet} onto the page, which a later run in "
            + $"the same checkout would recompute and match.{Environment.NewLine}{run.Diagnostics}";

        state.Pages[GuidePath].Verdict.ShouldBeNull(because);
        state.Pages[HomePath].Verdict.ShouldBeNull(because);
    }

    /// <summary>
    /// The per-page half of the same class: git answers a full tracked list, and this page's glob still
    /// matches none of it — a typo, a directory that was renamed, a page pointed at code that has moved.
    /// The page publishes, seals nothing, and the run says which page and why, because a glob that can
    /// never fire is the failure mode of an advisory check that gets believed.
    /// </summary>
    [Fact]
    public void Publish_seals_nothing_for_a_page_whose_globs_match_no_tracked_file()
    {
        var work = Scaffolded(nameof(Publish_seals_nothing_for_a_page_whose_globs_match_no_tracked_file));
        WritePage(work, GuidePath, "src/Deposits/**");
        WriteFile(work, SourceFile, SourceBytes);
        Commit(work);

        StubSpace();
        StubCreate();

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);

        var because = $"A glob that matched nothing sealed {EmptySet}, which every later run in this "
            + $"tree recomputes and matches.{Environment.NewLine}{run.Diagnostics}";

        State(work).Pages[GuidePath].Verdict.ShouldBeNull(because);

        run.FlowedAll.ShouldContain(
            $"{GuidePath} published, but its `sources` globs matched none of the files git tracks",
            customMessage: run.Diagnostics);
    }

    /// <summary>
    /// SC12 through the command: the build output sitting under the page's own glob is gitignored, so it
    /// is not in <c>git ls-files</c> and must not be in the seal. A walking implementation seals it here
    /// and the page never matches its own fingerprint again after a rebuild — the feature failing safe
    /// and no-opping for the commonest glob shape there is (spec §4b defect F).
    /// </summary>
    [Fact]
    public void Publish_seals_nothing_that_lives_in_a_gitignored_directory()
    {
        var work = Scaffolded(nameof(Publish_seals_nothing_that_lives_in_a_gitignored_directory));
        WritePage(work, GuidePath, "src/**");
        WriteFile(work, SourceFile, SourceBytes);

        // `docume init` writes the .gitignore this appends to (node_modules/ for the renderer).
        File.AppendAllText(Path.Combine(work, ".gitignore"), "bin/\nobj/\n");
        Commit(work);

        // After the commit, so nothing can stage them: this is the state a repo is in the moment after
        // `dotnet build`, and the file is under `src/**` by every glob engine's reading.
        WriteFile(work, "src/Loans/bin/Debug/DocuMe.dll", "MZ not really a dll");
        WriteFile(work, "src/Loans/obj/project.assets.json", "{ }");

        StubSpace();
        StubCreate();

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);

        var because = "The seal moved when a build dropped a gitignored file under the page's glob, so "
            + $"the page can never match it again.{Environment.NewLine}{run.Diagnostics}";

        State(work).Pages[GuidePath].Verdict!.SourcesHash.ShouldBe(RateOnly, because);
    }

    /// <summary>
    /// A consumer repo git cannot answer for — an unpacked archive, a tarball — publishes exactly as it
    /// did before this feature existed, and says so. The quiet version is the one that must not happen:
    /// an empty candidate list would seal the empty-set fingerprint onto every page, which reads as "this
    /// page documents nothing" and would hold the whole wiki out of a later drift report.
    /// </summary>
    [Fact]
    public void Publish_outside_a_checkout_seals_nothing_and_says_so()
    {
        var work = Scaffolded(nameof(Publish_outside_a_checkout_seals_nothing_and_says_so));
        WritePage(work, GuidePath, "src/**");
        WriteFile(work, SourceFile, SourceBytes);

        StubSpace();
        StubCreate();

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);
        run.Flowed.ShouldContain("not being sealed", customMessage: run.Diagnostics);

        var state = State(work);
        state.Pages[GuidePath].Verdict.ShouldBeNull(run.Diagnostics);
        state.Pages[HomePath].Verdict.ShouldBeNull(run.Diagnostics);
    }

    /// <summary>
    /// <c>--dry-run</c> asks git nothing it would not have asked before: the seal is written by the
    /// executor, and a run that writes no page has nothing to seal. Asserted through the promise that
    /// matters — the state file is untouched — rather than by counting processes.
    /// </summary>
    [Fact]
    public void A_dry_run_seals_nothing()
    {
        var work = Scaffolded(nameof(A_dry_run_seals_nothing));
        WritePage(work, GuidePath, "src/**");
        WriteFile(work, SourceFile, SourceBytes);
        Commit(work);

        var before = File.ReadAllBytes(StatePath(work));
        var run = Invoke(work, "publish", "--dry-run");

        run.Code.ShouldBe(0, run.Diagnostics);
        File.ReadAllBytes(StatePath(work)).ShouldBe(before, "`publish --dry-run` rewrote the state file.");
    }

    private static DocumeState State(string work) => StateStore.Load(StatePath(work));

    private static string StatePath(string work) =>
        Path.Combine(work, "docs", "wiki", "_meta", "state.json");

    /// <summary>Adds a page that declares one <c>sources</c> glob, wiki-root-relative.</summary>
    private static void WritePage(string work, string path, string glob)
    {
        var markdown = $"---\ntitle: Setup Guide\nsources:\n  - {glob}\n---\n\n# Setup\n\nHow to set it up.\n";

        WriteFile(work, $"docs/wiki/{path}", markdown);
    }

    /// <summary>
    /// Writes a repo-root-relative file byte for byte — never through a helper that would translate
    /// <c>\n</c> on Windows, since the fingerprint is over raw bytes and this fixture pins its value.
    /// </summary>
    private static void WriteFile(string work, string path, string content)
    {
        var full = Path.Combine(work, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// Makes <paramref name="work"/> a git checkout with everything in it committed, and answers the
    /// commit's sha. Identity and signing come from flags, so a developer's global git config cannot
    /// change the outcome (<c>GitRepositoryTests</c> does the same).
    /// </summary>
    private static string Commit(string work)
    {
        Git(work, "init", "-q", "-b", "main");
        Git(work, "config", "user.email", "loop@example.com");
        Git(work, "config", "user.name", "DocuMe loop");
        Git(work, "config", "commit.gpgsign", "false");
        Git(work, "add", "-A");
        Git(work, "commit", "-q", "-m", "the consumer repo");

        return Git(work, "rev-parse", "HEAD").Trim();
    }

    private static string Git(string work, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(work);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo).ShouldNotBeNull();

        // stderr drained concurrently with stdout: a sequential double read deadlocks once the child
        // fills the unread pipe (see GitRepositoryTests.Git).
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var error = errorTask.GetAwaiter().GetResult();

        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)} failed: {error}{output}");

        return output;
    }

    private static JsonElement Payload(IRequestMessage request)
    {
        request.Body.ShouldNotBeNull();
        using var document = JsonDocument.Parse(request.Body!);

        return document.RootElement.Clone();
    }

    private static CliRun Invoke(string workingDirectory, params string[] args) =>
        DocumeCli.Invoke(workingDirectory, args);

    private static IResponseBuilder Json(string body) => Response.Create()
        .WithStatusCode(HttpStatusCode.OK)
        .WithHeader("Content-Type", "application/json")
        .WithBody(body);

    private static IResponseBuilder Json(Func<IRequestMessage, string> body) => Response.Create()
        .WithStatusCode(HttpStatusCode.OK)
        .WithHeader("Content-Type", "application/json")
        .WithBody(body);

    /// <summary>
    /// A consumer repo as `docume init` leaves it, pointed at this test's own server. Scaffolded by the
    /// CLI rather than written here so the fixture cannot drift from what a real consumer gets.
    /// </summary>
    private string Scaffolded(string name)
    {
        var work = Path.Combine(_root, name);
        Directory.CreateDirectory(work);

        var run = Invoke(work, "init", "--space", SpaceKey, "--base-url", $"{_server.Url}/wiki");

        run.Code.ShouldBe(0, $"The fixture's own `docume init` failed.{Environment.NewLine}{run.Diagnostics}");

        return work;
    }

    private void StubSpace() =>
        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/spaces").UsingGet())
            .RespondWith(Json($$"""
                {
                  "results": [{ "id": "{{SpaceId}}", "key": "{{SpaceKey}}", "name": "DocuMe Sandbox" }],
                  "_links": {}
                }
                """));

    /// <summary>
    /// Answers a create with an id invented at request time, and the managed-marker stamp that follows
    /// every create — registered together because they arrive together.
    /// </summary>
    private void StubCreate()
    {
        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/wiki/api/v2/pages/*/properties"))
                .UsingPost())
            .RespondWith(Json(_ => """{ "id": "9001", "key": "docume", "value": { "managed": true } }"""));

        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingPost())
            .RespondWith(Json(request =>
            {
                var id = Interlocked.Increment(ref _nextPageId).ToString(CultureInfo.InvariantCulture);
                var title = Payload(request).GetProperty("title").GetString();

                return $$"""
                    {
                      "id": "{{id}}",
                      "status": "current",
                      "title": {{JsonSerializer.Serialize(title)}},
                      "spaceId": "{{SpaceId}}",
                      "version": { "number": 1 }
                    }
                    """;
            }));
    }
}
