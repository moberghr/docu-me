using System.Text;
using DocuMe.Core.Drift;
using Shouldly;

namespace DocuMe.Core.Tests.Drift;

/// <summary>
/// The sealed fingerprint's preimage and spelling, pinned the way <c>ContentHashTests</c> pins the body
/// hash's and for a worse failure mode: a value computed here is compared against one an earlier run
/// committed to <c>_meta/state.json</c>, so a changed preimage does not unseal one page — it unseals
/// every page in every consumer repo at once, and each goes quietly back to range-based drift.
/// </summary>
/// <remarks>
/// <para>
/// The two constants below were computed outside this code (<c>shasum -a 256</c> over the preimage
/// written by hand), never by calling <see cref="SourcesFingerprint"/> and pasting what it said, which
/// would pin nothing at all. They survived the reader half's contract change unchanged, which is the
/// point of pinning them: <see cref="SourcesFingerprint.Compute"/> now matches a supplied candidate list
/// instead of walking a directory, and the value a page seals is the same value it sealed before.
/// </para>
/// <para>
/// <see cref="_tracked"/> stands in for <c>git ls-files</c>: the write helpers maintain it the way
/// <c>git add -A</c> would, so a test that adds a file to the tree without adding it to the index is
/// making the statement it looks like it is making — that the file is gitignored or untracked.
/// </para>
/// </remarks>
public sealed class SourcesFingerprintTests : IDisposable
{
    /// <summary>The fingerprint of no files: <c>sha256</c> of the empty preimage.</summary>
    private const string EmptySet =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// The fingerprint of <see cref="Corpus"/>: <c>sha256</c> of
    /// <c>"src/Loans/Fee.cs\nsha256:7ea02…4833\nsrc/Loans/Rate.cs\nsha256:6682b…7fdf\n"</c>, whose two
    /// per-file hashes are <c>sha256</c> of <c>"fee\n"</c> and <c>"rate\n"</c>.
    /// </summary>
    private const string CorpusFingerprint =
        "sha256:a7cde5a477634f8dcdead23450de2f5b02e30f090a82d026dd7cc3edb3b788b7";

    private const string FeeHash =
        "sha256:7ea02e2393ad290842b6aa9e66c2562930a526ecd06a48a7ba2282c2365f4833";

    private const string RateHash =
        "sha256:6682b235fe153307561f748d02729860005ed7579650498d826d9c1f38657fdf";

    private readonly string _root = Directory.CreateTempSubdirectory("docume-sources-fingerprint").FullName;

    /// <summary>What git would report as tracked here, maintained by the write helpers.</summary>
    private readonly List<string> _tracked = [];

    /// <summary>The two files both halves of the contract are pinned against, in reverse path order.</summary>
    private static (string Path, string Hash)[] Corpus =>
    [
        ("src/Loans/Rate.cs", RateHash),
        ("src/Loans/Fee.cs", FeeHash),
    ];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Of_NoFiles_IsTheEmptySetFingerprint()
    {
        // The pure function's value is unchanged by the §3.1 revision of 2026-08-19: combining no files
        // still yields sha256 of the empty preimage. What changed is above this layer — no publish ever
        // records it and no drift run ever matches on it (SealedVerdicts, PublishExecutor.SealSources),
        // because every structural way of matching zero files lands on this one constant and a later run
        // under the same condition would recompute it and call the page verified.
        SourcesFingerprint.Of([]).ShouldBe(EmptySet);

        // The constant the two callers refuse by name is this value and not a second opinion about it.
        SourcesFingerprint.EmptySet.ShouldBe(EmptySet);
        SourcesFingerprint.IsEmptySet(EmptySet).ShouldBeTrue();
        SourcesFingerprint.IsEmptySet(CorpusFingerprint).ShouldBeFalse();

        // The two spellings of "no value at all" answer the same question the same way, so a caller
        // asking "may this be a verdict?" never has to ask twice.
        SourcesFingerprint.IsEmptySet(null).ShouldBeTrue();
        SourcesFingerprint.IsEmptySet(string.Empty).ShouldBeTrue();
    }

    [Fact]
    public void Of_PinsThePreimageAndTheSpelling()
    {
        var fingerprint = SourcesFingerprint.Of(Corpus);

        fingerprint.ShouldBe(CorpusFingerprint);
        fingerprint.ShouldStartWith("sha256:");
        fingerprint.Length.ShouldBe("sha256:".Length + 64);
        fingerprint.ShouldBe(fingerprint.ToLowerInvariant());
    }

    [Fact]
    public void Of_OrdersByPathOrdinally_SoTheCallersEnumerationOrderCannotMove()
    {
        // Compute runs one matcher per pattern, so the order files arrive in is a property of the glob
        // list a page happens to declare — which must not be a property of the seal.
        SourcesFingerprint.Of([.. Corpus.Reverse()]).ShouldBe(CorpusFingerprint);
    }

    [Fact]
    public void Of_ARenamedFileWithTheSameBytes_MovesTheFingerprint()
    {
        // Why the path is in the preimage: every byte in the corpus is untouched by a rename.
        SourcesFingerprint.Of([("src/Loans/Charge.cs", FeeHash), ("src/Loans/Rate.cs", RateHash)])
            .ShouldNotBe(CorpusFingerprint);
    }

    [Fact]
    public void Of_Null_Throws()
    {
        Should.Throw<ArgumentNullException>(() => SourcesFingerprint.Of(null!));
    }

    [Fact]
    public void Compute_AgreesWithThePinnedPreimage()
    {
        // The two halves meet here: whatever the reader enumerates has to combine to the value the pure
        // half pins, or the seal a publish writes is not the seal the constants describe.
        WriteCorpus();

        Compute("src/Loans/**").ShouldBe(CorpusFingerprint);
    }

    [Fact]
    public void Compute_IsDeterministicAcrossRuns()
    {
        WriteCorpus();

        Compute("src/**").ShouldBe(Compute("src/**"));
    }

    [Fact]
    public void Compute_MovesWhenAFilesContentChanges()
    {
        WriteCorpus();
        var before = Compute("src/Loans/**");

        Write("src/Loans/Rate.cs", "rate: 4.5\n");

        Compute("src/Loans/**").ShouldNotBe(before);
    }

    [Fact]
    public void Compute_MovesWhenAFileIsAdded()
    {
        WriteCorpus();
        var before = Compute("src/Loans/**");

        Write("src/Loans/Term.cs", "term\n");

        Compute("src/Loans/**").ShouldNotBe(before);
    }

    [Fact]
    public void Compute_MovesWhenAFileIsDeleted()
    {
        WriteCorpus();
        var before = Compute("src/Loans/**");

        Delete("src/Loans/Fee.cs");

        Compute("src/Loans/**").ShouldNotBe(before);
    }

    [Fact]
    public void Compute_MovesWhenAFileIsRenamedWithinTheSameGlob()
    {
        // The rename the range-based check already catches as two changed paths, and the one a
        // bytes-only fingerprint would call unchanged.
        WriteCorpus();
        var before = Compute("src/Loans/**");

        Move("src/Loans/Fee.cs", "src/Loans/Charge.cs");

        Compute("src/Loans/**").ShouldNotBe(before);
    }

    [Fact]
    public void Compute_HashesBytesVerbatim()
    {
        // No newline normalization: a sources glob can name a fixture or an image, where a 0x0D 0x0A
        // pair is data. Two corpora that differ only in line endings must not seal the same.
        Write("src/Loans/Rate.cs", "a\nb\n");
        var lf = Compute("src/**");

        Write("src/Loans/Rate.cs", "a\r\nb\r\n");

        Compute("src/**").ShouldNotBe(lf);
    }

    /// <summary>
    /// SC12, and the reason <see cref="SourcesFingerprint.Compute"/> takes its candidates rather than
    /// walking: the file a build drops into <c>bin/</c> is under <c>src/**</c> on disk and is not in
    /// git's answer, so it must not be in the seal. A walking implementation seals it, the next rebuild
    /// moves it, and the page never matches its own seal again — the feature failing safe and no-opping
    /// for the commonest glob shape there is (spec §4b defect F).
    /// </summary>
    [Fact]
    public void Compute_TheSameTreeFingerprintsIdenticallyAcrossABuild()
    {
        WriteCorpus();
        var before = Compute("src/**");

        // On disk but not in the index, which is exactly what `bin/` in a .gitignore produces.
        Untracked("src/Loans/bin/Debug/DocuMe.dll", "not really a dll");
        Untracked("src/Loans/obj/project.assets.json", "{ }");

        Compute("src/**").ShouldBe(before);
    }

    /// <summary>
    /// The other half of SC12: matching is against the list, so a file that is not in it is never read
    /// either. A page whose glob names a directory full of untracked scratch files pays nothing for
    /// them, and a fingerprint cannot depend on what a developer happens to have lying around.
    /// </summary>
    [Fact]
    public void Compute_ReadsNoFileTheCandidateListDoesNotName()
    {
        WriteCorpus();

        // Unreadable to anything that tried: a directory where the walk expected a file.
        Directory.CreateDirectory(Path.Combine(_root, "src", "Loans", "scratch.cs"));

        Compute("src/Loans/**").ShouldBe(CorpusFingerprint);
    }

    /// <summary>
    /// A file git tracks that the working tree does not have — deleted and not staged, or a sparse
    /// checkout. It throws rather than sealing the rest, because a fingerprint over the files that
    /// happened to be readable would equal itself on the next run and hold the page out of a drift report
    /// nobody could verify (spec §3.3; <c>PublishExecutor.SealSources</c> warns and seals nothing).
    /// </summary>
    [Fact]
    public void Compute_ATrackedFileTheWorkingTreeDoesNotHave_Throws()
    {
        WriteCorpus();
        File.Delete(Path.Combine(_root, "src", "Loans", "Fee.cs"));

        Should.Throw<IOException>(() => Compute("src/Loans/**"));
    }

    /// <summary>
    /// A matched candidate the working tree holds as a directory — the shape a git submodule's gitlink
    /// takes in <c>git ls-files</c>, and the case
    /// <see cref="Compute_ReadsNoFileTheCandidateListDoesNotName"/> deliberately keeps out of the list
    /// so it can assert the opposite. Pinned because the throw is not
    /// <see cref="IOException"/>: <see cref="UnauthorizedAccessException"/> does not derive from it, both
    /// call sites already catch both, and a later cleanup narrowing a catch to one of them would turn one
    /// page's unsealable sources into a publish run that aborts.
    /// </summary>
    [Fact]
    public void Compute_ADirectoryTypedCandidateItMatches_ThrowsUnauthorizedAccess()
    {
        WriteCorpus();

        Directory.CreateDirectory(Path.Combine(_root, "src", "Loans", "vendored"));
        _tracked.Add("src/Loans/vendored");

        Should.Throw<UnauthorizedAccessException>(() => Compute("src/Loans/**"));
    }

    [Fact]
    public void Compute_GlobsThatMatchNothing_AreTheEmptySetFingerprint()
    {
        // The value, not a verdict: PublishExecutor.SealSources refuses to record this and warns, and
        // SealedVerdicts.Apply refuses to match on it (spec §3.1 as revised 2026-08-19). What this
        // function owes its callers is the honest combination of what it matched, which is nothing.
        WriteCorpus();

        Compute("src/Deposits/**").ShouldBe(EmptySet);
    }

    [Fact]
    public void Compute_NoPatternsAtAll_IsTheEmptySetFingerprint()
    {
        WriteCorpus();

        SourcesFingerprint.Compute(_root, [], _tracked).ShouldBe(EmptySet);
    }

    [Fact]
    public void Compute_NoCandidatesAtAll_IsTheEmptySetFingerprint()
    {
        // The repo git knows nothing about, and the root it would have read is never touched: nothing
        // was matched, and saying so as a value is the same answer as a glob that matched nothing —
        // which is exactly why neither caller may treat that value as evidence.
        WriteCorpus();

        SourcesFingerprint.Compute(Path.Combine(_root, "not-a-directory"), ["src/**"], [])
            .ShouldBe(EmptySet);
    }

    [Fact]
    public void Compute_CountsAFileTwoGlobsBothClaimOnce()
    {
        WriteCorpus();

        Compute("src/Loans/**", "src/**/*.cs").ShouldBe(CorpusFingerprint);
    }

    [Fact]
    public void Compute_IgnoresBlankPatterns()
    {
        WriteCorpus();

        Compute("src/Loans/**", "   ").ShouldBe(CorpusFingerprint);
    }

    [Fact]
    public void Compute_MatchesCaseSensitively()
    {
        // The one shared matcher seam (DriftPlanner.BuildMatcher) is ordinal, so a glob whose case does
        // not match the tree matches nothing here exactly as it matches nothing in a drift run.
        WriteCorpus();

        Compute("SRC/Loans/**").ShouldBe(EmptySet);
    }

    [Theory]
    [InlineData("src/Loans/")]
    [InlineData("/src/Loans/**")]
    public void Compute_NormalizesAGlobTheWayDriftDoes(string pattern)
    {
        // Same two spellings DriftPlanner.NormalizePattern straightens out, asserted here so the seal
        // cannot silently read a page's globs as matching nothing while drift reads them as matching.
        WriteCorpus();

        Compute(pattern).ShouldBe(CorpusFingerprint);
    }

    [Fact]
    public void Compute_NormalizesTheCandidatePathsTheWayDriftDoes()
    {
        // Blanks, duplicates and backslashes, the three corrections DriftPlanner makes to the diff it is
        // handed: a candidate list that would match there has to match here, or the seal and the check
        // disagree about a page whose sources never moved.
        WriteCorpus();

        SourcesFingerprint
            .Compute(
                _root,
                ["src/Loans/**"],
                [@"src\Loans\Rate.cs", "src/Loans/Rate.cs", " src/Loans/Fee.cs ", "  "])
            .ShouldBe(CorpusFingerprint);
    }

    [Fact]
    public void Compute_NeedsARootPatternsAndCandidates()
    {
        Should.Throw<ArgumentException>(() => SourcesFingerprint.Compute(" ", ["src/**"], []));
        Should.Throw<ArgumentNullException>(() => SourcesFingerprint.Compute(_root, null!, []));
        Should.Throw<ArgumentNullException>(() => SourcesFingerprint.Compute(_root, ["src/**"], null!));
    }

    /// <summary>Fingerprints <paramref name="patterns"/> against everything git tracks here.</summary>
    private string Compute(params string[] patterns) =>
        SourcesFingerprint.Compute(_root, patterns, _tracked);

    private void WriteCorpus()
    {
        Write("src/Loans/Rate.cs", "rate\n");
        Write("src/Loans/Fee.cs", "fee\n");
    }

    /// <summary>Writes a tracked file, as <c>git add</c> would leave it.</summary>
    private void Write(string path, string content)
    {
        Untracked(path, content);

        if (!_tracked.Contains(path, StringComparer.Ordinal))
        {
            _tracked.Add(path);
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> byte for byte and tells git nothing about it — a gitignored
    /// build artifact, or a file nobody has added yet. Never through a helper that would translate
    /// <c>\n</c> on Windows, since the line endings are half of what these tests assert.
    /// </summary>
    private void Untracked(string path, string content)
    {
        var file = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(content));
    }

    private void Delete(string path)
    {
        File.Delete(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        _tracked.Remove(path);
    }

    private void Move(string from, string to)
    {
        File.Move(
            Path.Combine(_root, from.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(_root, to.Replace('/', Path.DirectorySeparatorChar)));

        _tracked.Remove(from);
        _tracked.Add(to);
    }
}
