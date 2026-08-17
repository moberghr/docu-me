using System.Text.RegularExpressions;
using DocuMe.Core.Scaffolding;
using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Acceptance;

/// <summary>
/// Rule §9.5 — "repo-specific knowledge (domain list, tone, audience, structure) lives in the consumer
/// repo; the tool and skills stay generic" — asserted over the shipped tree, which is the only place the
/// rule can be broken.
/// </summary>
/// <remarks>
/// <para>
/// PLAN.md §1 states this as a design principle: <em>one tool, many repos</em>. It is the claim that makes
/// DocuMe distributable at all, and until this class existed nothing checked it. Three test files cite
/// §9.5 in doc comments (<see cref="Cli.CliDriftTests"/> and two neighbours) and all three cite it as
/// <em>context</em> for a different assertion — that a space key or a label name is read from config — so
/// the rule itself had no test at all.
/// </para>
/// <para>
/// <strong>Measured before this class existed.</strong> Two edits, one per half of the rule, each run
/// against the full suite: <c>docume init</c> scaffolding <c>_meta/STYLE.md</c> pre-filled with this
/// repo's own audience, tone and directory taxonomy, and <c>docs-loop</c>'s SKILL.md carrying a hardcoded
/// house taxonomy in place of the paragraph that defers to the consumer. <strong>1384 of 1384 passed under
/// each.</strong> A build that seeded every consumer repo with DocuMe's voice was two green commits away.
/// </para>
/// <para>
/// <strong>What makes the property testable is that this repo is a consumer of its own tool.</strong>
/// <c>docs/wiki/_meta/STYLE.md</c> is a real filled-in style guide sitting in the same tree as the thing
/// that must not contain it, so the needles are derived rather than invented: whatever this repo has
/// answered for itself is, by §9.5, exactly what the shipped tool may not carry. Leakage from the dogfood
/// wiki is also the realistic vector — it is the style guide in front of every editor.
/// </para>
/// <para>
/// What this class does <em>not</em> claim: that the scaffolded guide is free of <em>any</em> conceivable
/// house style, only that it is free of <em>this</em> repo's and names no taxonomy of its own. A
/// maintainer who invents a fresh opinion from nothing and writes it into the scaffolder passes here. The
/// alternative was a length threshold separating "a prompt" from "an answer", and an arbitrary constant
/// that reads as rigour is worse than a stated boundary.
/// </para>
/// </remarks>
public sealed partial class ConsumerKnowledgeCoverageTests
{
    /// <summary>
    /// The kinds of knowledge §9.5 names as the consumer's, matched to the headings this repo's own style
    /// guide carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guide's other sections are deliberately absent, and they are declared in
    /// <see cref="SharedWithTheProduct"/> rather than merely left out — a literal that bounds a sweep and
    /// is pinned to nothing lets the tree grow past it in silence. <c>Verification</c> is the tool's own
    /// marker convention (<c>⚠️ UNVERIFIED</c> and its sibling are pinned by
    /// <see cref="StyleGuidePageTests"/> and converted by the golden corpus), so it is shared between the
    /// guide and the tool on purpose. <c>Scope</c> carries "the repo is the source of truth", which is
    /// PLAN.md §1's product principle and appears in four <c>src/</c> files because stating it is their job.
    /// </para>
    /// <para>
    /// Including either turned the scan against the product for describing itself, which is not what §9.5
    /// forbids: the rule is about one repo's <em>answers</em>, not about the vocabulary the tool and its
    /// consumers necessarily share.
    /// </para>
    /// </remarks>
    private static readonly string[] ConsumerTopics = ["Audience", "Tone", "Structure", "Diagrams"];

    /// <summary>
    /// The guide's remaining sections: the product describing itself, which §9.5 does not forbid the tool
    /// from repeating.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Constraints that are checked</c> names this suite's own <c>[Fact]</c> methods and
    /// <c>Constructs to avoid</c> names the converter's degradation codes; both are derived from the code
    /// by <see cref="StyleGuidePageTests"/>, so a scan that treated them as this repo's taste would indict
    /// the product for documenting its own behaviour.
    /// </para>
    /// <para>
    /// <strong>Measured iter185.</strong> This list exists because the one above bounded the whole scan and
    /// nothing paired it with the file. A <c>## Domains</c> section — §9.5's <em>first</em>-named category
    /// of consumer knowledge — added to the guide and lifted verbatim into the shipped scaffolder's own
    /// prose passed all 1,436 tests. The same lift into the scaffolded bullet list was caught, but by
    /// <c>ReadmeCliContractTests</c> holding README and scaffolder to the same topic list, which says
    /// nothing about §9.5 and does not see a doc comment three lines above it.
    /// </para>
    /// </remarks>
    private static readonly string[] SharedWithTheProduct =
        ["Scope", "Verification", "Constraints that are checked", "Constructs to avoid"];

    /// <summary>The file types the scan reads, asserted against what the shipped roots actually hold.</summary>
    private static readonly string[] Extensions = [".cs", ".md", ".json", ".yml", ".yaml", ".mjs", ".csproj"];

    /// <summary>
    /// Words per phrase compared between this repo's answers and the shipped tree.
    /// </summary>
    /// <remarks>
    /// Measured, not chosen: at five and six words the shipped tree is clean and both defects above are
    /// caught; at seven the skill defect walks through, because its longest lift is the six words "a
    /// developer or tech lead evaluating"; at four the scan starts indicting ordinary prose
    /// ("pages in confluence a", "is dropped from the"). Six sits at the wide end of the band that works.
    /// </remarks>
    private const int PhraseWords = 6;

    /// <summary>The fewest phrases a topic must contribute before the scan is trusted.</summary>
    /// <remarks>
    /// Per topic rather than over the union, because the union hides the failure: the three contribute 28,
    /// 45 and 91 phrases, so a floor loose enough to survive an edit to the largest would wave through the
    /// silent loss of the smallest. A section that stops parsing yields zero and fails here.
    /// </remarks>
    private const int LeastPhrasesPerTopic = 10;

    /// <summary>
    /// No shipped file reproduces a distinctive phrase from this repo's own answers about its audience,
    /// tone or structure.
    /// </summary>
    [Fact]
    public void No_shipped_file_reproduces_this_repos_own_answers()
    {
        var needles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var topic in ConsumerTopics)
        {
            var section = StyleSection(topic);

            var absent = $"docs/wiki/_meta/STYLE.md has no '## {topic}' section. This scan derives what the "
                + "shipped tool may not say from what this repo has said about itself, so a heading that "
                + "moved leaves it comparing against nothing and reporting §9.5 upheld.";

            section.ShouldNotBeNullOrWhiteSpace(absent);

            var phrases = Phrases(Quoted().Replace(section, " "));

            var thin = $"The '{topic}' section yields {phrases.Count} phrase(s) of {PhraseWords} words, "
                + $"below {LeastPhrasesPerTopic}. Either it has been trimmed to almost nothing or the "
                + "quote-stripping is eating it, and both make this scan pass by comparing against an "
                + "empty set.";

            phrases.Count.ShouldBeGreaterThan(LeastPhrasesPerTopic, thin);

            needles.UnionWith(phrases);
        }

        var offenders = new List<string>();

        foreach (var file in ShippedFiles())
        {
            var shared = Phrases(File.ReadAllText(file));
            shared.IntersectWith(needles);

            if (shared.Count == 0)
            {
                continue;
            }

            var quote = shared.Order(StringComparer.Ordinal).First();

            offenders.Add($"{Relative(file)} — \"{quote}\"");
        }

        const string message = "A shipped file repeats this repo's own answer about its audience, tone or structure. "
            + "Rule §9.5 and PLAN.md §1 put that knowledge in the consumer repo (`docume.json`, "
            + "`_meta/STYLE.md`, frontmatter) and keep the tool and skills generic, and this repo's "
            + "`docs/wiki/_meta/STYLE.md` is a consumer's file like any other. If the phrase is the "
            + "product describing itself rather than this repo's taste, it belongs in PLAN.md's "
            + "vocabulary and the guide should quote it — a quoted span is not scanned. Otherwise the "
            + "sentence goes back in the consumer's file. Repeating this repo's answers:";

        offenders.ShouldBeEmpty(message);
    }

    /// <summary>
    /// No shipped file names the section taxonomy this repo chose for its own wiki.
    /// </summary>
    /// <remarks>
    /// A second, independent net over the same rule. The phrase scan reads prose, and a taxonomy leaks as a
    /// bare list of directory names with no prose around it to match — which is exactly the shape of the
    /// scaffolded style guide's <strong>Structure</strong> bullet, the single most likely place for one
    /// repo's shape to be handed to every other.
    /// </remarks>
    [Fact]
    public void No_shipped_file_names_this_repos_own_section_taxonomy()
    {
        var sections = System.IO.Directory
            .EnumerateDirectories(Path.Combine(DocumeCli.RepoRoot, "docs", "wiki"))
            .Select(directory => new DirectoryInfo(directory).Name)
            .Where(name => !name.StartsWith('_'))
            .Order(StringComparer.Ordinal)
            .ToList();

        const string empty = "docs/wiki has no section directories, so this scan has nothing to look for and "
            + "would report §9.5 upheld against any tree at all. The dogfood wiki has moved.";

        sections.ShouldNotBeEmpty(empty);

        var offenders = new List<string>();

        foreach (var file in ShippedFiles())
        {
            var text = File.ReadAllText(file);

            var named = sections
                .Where(section => text.Contains(section, StringComparison.Ordinal))
                .ToList();

            if (named.Count == 0)
            {
                continue;
            }

            offenders.Add($"{Relative(file)} — {string.Join(", ", named)}");
        }

        var message = $"A shipped file names this repo's own wiki taxonomy ({string.Join(", ", sections)}). "
            + "Rule §9.5: the section taxonomy is the consumer's, declared in their `_meta/STYLE.md` and "
            + "read from there at run time. A tool that ships one repo's directory names hands every other "
            + "repo the same shape, and `docs-loop` will follow it believing the consumer chose it. Naming "
            + "this repo's taxonomy:";

        offenders.ShouldBeEmpty(message);
    }

    /// <summary>
    /// What <c>docume init</c> scaffolds as <c>_meta/STYLE.md</c> asks the consumer its questions and
    /// answers none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other two facts say the tool carries no answers of <em>this</em> repo's. This one says it
    /// carries the <em>questions</em>, and it exists because a specific coupling depends on it.
    /// <c>docs-loop</c>'s SKILL.md tells a run that the scaffolded file is the four topics below
    /// "Fill these in for your project", and instructs it that if the guide is still that, it should say so
    /// in the PR body and infer the taxonomy from the code for that one run.
    /// </para>
    /// <para>
    /// So the invitation is not decoration: it is how a generation run tells an unfilled guide from a
    /// filled one. A scaffolder that shipped opinions instead would leave the run unable to make that
    /// distinction, and it would write forty pages to a style nobody chose — which is the same mistake the
    /// skill is explicitly forbidden to make ("do not fill `STYLE.md` in yourself … a guessed style guide
    /// is worse than an empty one because the next run will follow it").
    /// </para>
    /// </remarks>
    [Fact]
    public void The_scaffolded_style_guide_asks_the_consumer_and_answers_nothing()
    {
        var scaffolded = ScaffoldedStyleGuide();

        const string invitation = "Fill these in for your project.";

        const string missing = "The scaffolded `_meta/STYLE.md` no longer invites the consumer to fill it "
            + "in. docs-loop reads that sentence to tell an unfilled guide from a filled one, and without "
            + "it a run cannot know whether the audience and taxonomy it is following were chosen by "
            + "anybody (rule §9.5).";

        scaffolded.ShouldContain(invitation, customMessage: missing);

        var topics = StyleTopic()
            .Matches(scaffolded)
            .Select(match => match.Groups["topic"].Value)
            .ToList();

        var uncovered = ConsumerTopics
            .Where(topic => !topics.Contains(topic, StringComparer.Ordinal))
            .ToList();

        var dropped = "The scaffolded `_meta/STYLE.md` no longer asks the consumer about "
            + $"[{string.Join(", ", uncovered)}]. §9.5 names those as the consumer's to decide, and a "
            + "topic the tool stops asking about is one the generation run has to invent.";

        uncovered.ShouldBeEmpty(dropped);
    }

    /// <summary>
    /// Every <c>##</c> section of this repo's style guide is one the scan reads or one declared shared
    /// with the product.
    /// </summary>
    /// <remarks>
    /// The needle side of the sweep's population. Non-vacuous by construction rather than by a floor:
    /// <see cref="ConsumerTopics"/> is a non-empty literal, so a guide that parsed to nothing fails the
    /// comparison instead of passing it. The partition is what carries the meaning — a section that is
    /// neither this repo's answer nor the product's own vocabulary is a third thing nobody has decided
    /// about, and it is the one shape §9.5 has no reading for.
    /// </remarks>
    [Fact]
    public void Every_section_of_this_repos_style_guide_is_scanned_or_declared_shared()
    {
        var declared = ConsumerTopics.Concat(SharedWithTheProduct).ToList();

        const string drifted = "docs/wiki/_meta/STYLE.md and the sections this scan classifies have "
            + "diverged. Rule §9.5 names four kinds of consumer knowledge — domain list, tone, audience, "
            + "structure — and this class derives its needles from the ones in ConsumerTopics, so a "
            + "section in neither list is one the tool may reproduce verbatim with nothing objecting. Add "
            + "a new heading to ConsumerTopics if it holds this repo's own answer, or to "
            + "SharedWithTheProduct if it describes the product; a heading here that the guide no longer "
            + "carries leaves StyleSection comparing against an empty string.";

        Headings().ShouldBe(declared, ignoreOrder: true, drifted);
    }

    /// <summary>
    /// The sweep reaches every root it declares shipped, and reads every kind of file it finds there.
    /// </summary>
    /// <remarks>
    /// The haystack side, and per root rather than over the union for the reason
    /// <see cref="LeastPhrasesPerTopic"/> gives: <c>src/</c> holds most of the tree, so a total large
    /// enough to look healthy survives the loss of <c>plugin/</c> — the three <c>SKILL.md</c> files, which
    /// are the likeliest place for one repo's knowledge to reach every other. <c>ShippedFiles</c> skips a
    /// root that is not on disk silently, which is the same failure arriving by a rename.
    /// </remarks>
    [Fact]
    public void The_shipped_sweep_reads_every_root_and_every_file_type_it_ships()
    {
        var unread = new List<string>();

        foreach (var root in DogfoodWikiTests.ShippedRoots)
        {
            var files = FilesUnder(root);

            var absent = $"'{root}' is declared shipped and holds no file the scan can walk. A root that "
                + "moved leaves this sweep reporting §9.5 upheld over a tree it never opened, because "
                + "ShippedFiles passes over a directory that is not there without a word.";

            files.ShouldNotBeEmpty(absent);

            unread.AddRange(files
                .Where(file => !Extensions.Contains(Path.GetExtension(file), StringComparer.Ordinal))
                .Select(Relative));
        }

        const string invisible = "A shipped file's type is outside the set this scan reads, so nothing "
            + "DocuMe hands over in that file is checked against rule §9.5. Add the extension to "
            + "Extensions, or state here why that kind of file carries no prose. Unread:";

        unread.ShouldBeEmpty(invisible);
    }

    /// <summary>The <c>_meta/STYLE.md</c> body <c>docume init</c> writes into a fresh consumer repo.</summary>
    private static string ScaffoldedStyleGuide()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("docume-consumer-knowledge").FullName;

        var style = ProjectScaffolder
            .Scaffold(directory, "DOCS", "https://example.atlassian.net/wiki")
            .Select(result => result.RelativePath)
            .Single(path => path.EndsWith("STYLE.md", StringComparison.Ordinal));

        return File.ReadAllText(Path.Combine(directory, style));
    }

    /// <summary>
    /// The body of one <c>##</c> section of this repo's own style guide, or empty when it has none.
    /// </summary>
    private static string StyleSection(string heading)
    {
        var lines = StyleGuideLines();

        var start = Array.FindIndex(
            lines,
            line => string.Equals(line.Trim(), $"## {heading}", StringComparison.Ordinal));

        if (start < 0)
        {
            return string.Empty;
        }

        var end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));

        return string.Join('\n', lines[(start + 1)..(end < 0 ? lines.Length : end)]);
    }

    /// <summary>This repo's own style guide, line by line.</summary>
    private static string[] StyleGuideLines() =>
        File.ReadAllLines(Path.Combine(DocumeCli.RepoRoot, "docs", "wiki", "_meta", "STYLE.md"));

    /// <summary>Its <c>##</c> headings, in the order they appear.</summary>
    private static List<string> Headings() =>
        StyleGuideLines()
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())
            .ToList();

    /// <summary>Every file under one shipped root, whatever its type, minus build output.</summary>
    private static List<string> FilesUnder(string root)
    {
        var directory = Path.Combine(DocumeCli.RepoRoot, root.TrimEnd('/'));

        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }

        return System.IO.Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Every distinct <see cref="PhraseWords"/>-word phrase in <paramref name="text"/>.</summary>
    /// <remarks>
    /// Reduced to lowercase alphanumeric words first, so markdown emphasis, backticks and line wrapping
    /// cannot hide a lift. Comparing phrases rather than whole sentences is what catches the realistic
    /// copy, which is a paraphrase with the distinctive middle left intact.
    /// </remarks>
    private static HashSet<string> Phrases(string text)
    {
        var words = Word()
            .Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .ToList();

        var phrases = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index + PhraseWords <= words.Count; index++)
        {
            phrases.Add(string.Join(' ', words.GetRange(index, PhraseWords)));
        }

        return phrases;
    }

    /// <summary>Every text file under the roots this repo counts as shipped.</summary>
    /// <remarks>
    /// <c>ShippedRoots</c> is <see cref="DogfoodWikiTests"/>'s, deliberately not a second copy: that list
    /// is already this repo's definition of what it hands over, and two lists of the same thing is how one
    /// of them goes stale.
    /// </remarks>
    private static IEnumerable<string> ShippedFiles() =>
        DogfoodWikiTests.ShippedRoots
            .SelectMany(FilesUnder)
            .Where(file => Extensions.Contains(Path.GetExtension(file), StringComparer.Ordinal));

    private static string Relative(string file) =>
        Path.GetRelativePath(DocumeCli.RepoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    [GeneratedRegex("\"[^\"]*\"", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Quoted();

    [GeneratedRegex("[a-z0-9]+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Word();

    [GeneratedRegex(@"^- \*\*(?<topic>[A-Za-z]+):\*\*", RegexOptions.ExplicitCapture | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StyleTopic();
}
