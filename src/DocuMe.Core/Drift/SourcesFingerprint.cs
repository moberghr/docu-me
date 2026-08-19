using System.Text;
using DocuMe.Core.State;
using Microsoft.Extensions.FileSystemGlobbing;

namespace DocuMe.Core.Drift;

/// <summary>
/// One hash over every file a page's <c>sources</c> globs match: what a publish seals into the page's
/// state entry so a later run can ask whether the bytes the live body was generated from are still the
/// bytes on disk (docs/specs/2026-08-19-sealed-source-verdicts.md §3.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The preimage is pinned, for the reason <see cref="ContentHash"/> pins its own.</strong>
/// Ordinal by path, <c>"&lt;path&gt;\n&lt;hash-of-bytes&gt;\n"</c> per file, concatenated and hashed as
/// UTF-8. The value is compared against one an earlier run committed to <c>_meta/state.json</c>, so
/// changing the preimage or the spelling would not read as "this file changed" — it would unseal every
/// page in the wiki at once, silently, and each one would go back to answering drift from the commit
/// range. <c>SourcesFingerprintTests</c> pins both against constants computed outside this code.
/// </para>
/// <para>
/// <strong>The path is in the preimage, not only the bytes.</strong> Renaming a file to a name the same
/// glob still matches leaves every byte in the corpus untouched; without the path, the fingerprint would
/// not move, and a page documenting a type by its file name would stay sealed through the rename that
/// made its prose wrong.
/// </para>
/// <para>
/// <strong>Bytes are hashed verbatim, with no newline normalization</strong>
/// (<see cref="ContentHash.OfBytes"/>). <c>sources</c> globs name source files, and a page may point at
/// a fixture, an image or any other binary: a <c>0x0D 0x0A</c> pair inside one is data, and collapsing it
/// would make two different corpora seal identically. The opposite risk — a Windows checkout sealing
/// differently from a Linux CI runner — is real but cannot be fixed here: the seal is written and read
/// against a working tree, and git's own <c>core.autocrlf</c> is where that belongs.
/// </para>
/// <para>
/// <strong>Split in two, following <see cref="DriftPlanner"/>'s own split.</strong> <see cref="Of"/> is
/// the pure combiner tests pin; <see cref="Compute"/> is the half that reads a working tree. The glob
/// seam is <see cref="DriftPlanner.BuildMatcher"/> and only that — a second matcher would eventually
/// disagree with drift's about what <c>**</c> means, and the two disagreeing is precisely a page that
/// drift flags and the seal then holds out.
/// </para>
/// <para>
/// <strong>The files it may match are supplied, never walked</strong> (spec §3.1 as amended, §4b defect
/// F). <see cref="Compute"/> takes its candidate list exactly as <see cref="DriftPlanner.Plan"/> takes
/// its changed-file list, and the caller hands over git's tracked files
/// (<see cref="Git.GitRepository.TrackedFilesAsync"/>). A tree walk was the first implementation and it
/// is wrong in one silent way: drift's list comes from <c>git diff</c>, which never reports a gitignored
/// path, while a walk sees <c>bin/</c> and <c>obj/</c>. A page with <c>sources: ["src/**"]</c> in a repo
/// that builds in-tree would seal a fingerprint containing build output and never match it again after a
/// rebuild — the feature failing safe and no-opping, for the commonest glob shape there is. Taking the
/// list makes the two halves see one universe by construction rather than by two implementations
/// agreeing.
/// </para>
/// </remarks>
public static class SourcesFingerprint
{
    /// <summary>
    /// The fingerprint of no files at all, and the one value that is never a verdict
    /// (spec §3.1 as revised 2026-08-19).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a perfectly real hash — <c>sha256</c> of the empty preimage — and <see cref="Of"/> keeps
    /// answering it, because the pure function's job is to combine what it is handed. What changed is
    /// that nothing ever <em>seals</em> it and nothing ever matches it. The spec originally argued the
    /// value distinguished "documented nothing, deliberately" from "never sealed"; the distinction is
    /// not worth what it costs, because every structural way of matching zero files produces this one
    /// constant — a page whose glob has a typo in it, a sparse checkout scoped to <c>docs/</c>, a
    /// candidate list git answered empty — and a later run under the same structural condition
    /// recomputes the same constant and calls the page verified. A page whose sources were never read
    /// would vanish from the report, which is the one direction that cannot be allowed
    /// (<see cref="SealedVerdicts"/>).
    /// </para>
    /// <para>
    /// It is the same reading <see cref="DriftPlanner.NormalizePattern"/> already takes of a glob that
    /// can never fire: "the one failure mode of an advisory check that gets believed: nobody
    /// investigates a green run."
    /// </para>
    /// <para>
    /// Derived from <see cref="Of"/> rather than pasted as a literal, so it cannot drift from the
    /// preimage it names; <c>SourcesFingerprintTests</c> pins the literal against
    /// <see cref="Of"/> itself.
    /// </para>
    /// </remarks>
    public static readonly string EmptySet = Of([]);

    /// <summary>
    /// Whether <paramref name="fingerprint"/> is the one value a seal may never carry
    /// (<see cref="EmptySet"/>) — including the two spellings of "no value at all", so a caller that
    /// asks this question gets one answer for every way a page can have matched nothing.
    /// </summary>
    public static bool IsEmptySet(string? fingerprint) =>
        fingerprint is not { Length: > 0 } || string.Equals(fingerprint, EmptySet, StringComparison.Ordinal);

    /// <summary>
    /// Combines already-hashed files into the fingerprint state stores.
    /// </summary>
    /// <param name="files">
    /// Every file the page's globs matched, as its repo-relative forward-slash path and the
    /// <see cref="ContentHash.OfBytes"/> of its bytes. Order is irrelevant — the preimage is ordinal by
    /// path, so a caller that enumerates differently still seals the same value. An empty sequence
    /// combines to <see cref="EmptySet"/>, which is a real value this function is happy to answer and
    /// which no caller may ever seal or match on.
    /// </param>
    /// <returns><c>sha256:</c> followed by 64 lowercase hex characters, §5.3's one spelling.</returns>
    public static string Of(IEnumerable<(string Path, string Hash)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var preimage = new StringBuilder();

        // Hash as the tie-break, so even a caller that hands the same path over twice gets one answer
        // rather than an answer that depends on which of the two arrived first.
        foreach (var (path, hash) in files
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.Hash, StringComparer.Ordinal))
        {
            preimage.Append(path).Append('\n').Append(hash).Append('\n');
        }

        return ContentHash.OfBytes(Encoding.UTF8.GetBytes(preimage.ToString()));
    }

    /// <summary>
    /// Fingerprints the files of <paramref name="candidateFiles"/> that <paramref name="patterns"/> match,
    /// reading their bytes from <paramref name="root"/>.
    /// </summary>
    /// <param name="root">
    /// The directory <c>sources</c> globs are written against — the one holding <c>docume.json</c>
    /// (§5.1), which is also the repo root git reports its paths relative to. Only matched files are read
    /// from it, so a root with nothing matched under it is never touched at all.
    /// </param>
    /// <param name="patterns">
    /// The page's <c>sources</c> globs. Blanks are skipped and a file two globs both claim is counted
    /// once, the way <see cref="DriftPlanner.Plan"/> counts it once.
    /// </param>
    /// <param name="candidateFiles">
    /// Every file the page may match, as repo-relative forward-slash paths — git's tracked files in a real
    /// run (<see cref="Git.GitRepository.TrackedFilesAsync"/>). Supplied rather than discovered for the
    /// reason the type's remarks give: the same universe drift matches against, or the seal answers a
    /// question drift never asked. An empty list combines to <see cref="EmptySet"/>, which callers must
    /// refuse rather than record — it is an unusable answer dressed as a value.
    /// </param>
    /// <exception cref="IOException">
    /// A matched file could not be read — a tracked file deleted from the working tree, a source tree this
    /// checkout does not have. Deliberately not swallowed: the caller decides what an unreadable corpus
    /// means, and the answer is never "seal something" — a fingerprint over the files that happened to be
    /// readable would suppress exactly the drift report nobody could verify.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The same failure, thrown under a different type: a matched candidate the working tree holds as a
    /// directory (the shape a git submodule's gitlink takes in <c>git ls-files</c>) or a file the process
    /// may not open answers this rather than <see cref="IOException"/>, which does not derive from it.
    /// Named here because both call sites already catch both, and a later "cleanup" narrowing a catch to
    /// match a doc that listed one of them would turn one page's unsealable sources into an aborted
    /// publish run (<c>SourcesFingerprintTests</c> pins the type).
    /// </exception>
    public static string Compute(
        string root,
        IReadOnlyCollection<string> patterns,
        IReadOnlyCollection<string> candidateFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(candidateFiles);

        var files = Matched(patterns, Candidates(candidateFiles))
            .Select(path => (Path: path, Hash: ContentHash.OfBytes(File.ReadAllBytes(Absolute(root, path)))))
            .ToList();

        return Of(files);
    }

    /// <summary>
    /// Every file of <paramref name="candidateFiles"/> that <paramref name="patterns"/> match, once each.
    /// </summary>
    /// <remarks>
    /// One matcher per pattern and
    /// <see cref="MatcherExtensions.Match(Matcher, IEnumerable{string})"/> to run it, which is the overload
    /// <see cref="DriftPlanner.Plan"/> runs its own patterns through — the seal and the drift check then
    /// agree about what a glob matches because it is one call site's worth of behaviour, not two.
    /// Two globs claiming one file leave one entry, so the file is read once and counted once, the way
    /// <see cref="DriftPlanner.Plan"/> counts it once.
    /// </remarks>
    private static IEnumerable<string> Matched(
        IReadOnlyCollection<string> patterns,
        IReadOnlyCollection<string> candidateFiles) =>
        patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(pattern => DriftPlanner.BuildMatcher(pattern).Match(candidateFiles).Files)
            .Select(match => match.Path)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The candidate paths as the matcher wants them: forward slashes, no blanks, no duplicates — the
    /// same three corrections <see cref="DriftPlanner.Plan"/> makes to the diff it is handed, so a list
    /// that would match there matches here.
    /// </summary>
    private static List<string> Candidates(IReadOnlyCollection<string> candidateFiles) =>
    [
        .. candidateFiles
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => file.Trim().Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal),
    ];

    private static string Absolute(string root, string path) =>
        Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
}
