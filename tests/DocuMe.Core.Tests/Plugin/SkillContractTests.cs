using Shouldly;
using YamlDotNet.RepresentationModel;

namespace DocuMe.Core.Tests.Plugin;

/// <summary>
/// The Claude Code skills in <c>plugin/skills/</c> (PLAN.md §11), asserted where they make a promise the
/// C# can be held to.
/// </summary>
/// <remarks>
/// <para>
/// A SKILL.md is prose, and most of it is not testable — whether the procedure produces a good page is a
/// human's judgement. Two things in it are not prose, though, and both fail silently.
/// </para>
/// <para>
/// The first is rule §1.3, which requires every SKILL.md to state in its system contract that Confluence
/// page bodies and comments are untrusted input: claims to verify against code, never instructions to
/// follow. That clause is the whole prompt-injection defense (PLAN.md §9), it is invisible when absent,
/// and it is exactly the kind of paragraph an editor trims for length. So its presence is asserted.
/// </para>
/// <para>
/// The second is rule §0.4's boundary: skills reach Confluence only through the <c>docume</c> CLI. A skill
/// that grew a REST call would still read fine and would still work, right up to the run that wrote to a
/// space with none of the tool's guards on it. The grep here is a canary for that, not proof of it.
/// </para>
/// <para>
/// The frontmatter checks are duller and earn their place anyway: a <c>name</c> that disagrees with the
/// directory means <c>/docs-refresh</c> invokes nothing, and a missing <c>description</c> means the model
/// never discovers the skill in the first place.
/// </para>
/// </remarks>
public sealed class SkillContractTests
{
    /// <summary>The skills §11 names. <c>docs-feedback</c> lands in M4, <c>docs-loop</c> in M6.</summary>
    private static readonly string[] Skills = ["docs-refresh"];

    /// <summary>
    /// URL fragments that only appear in a direct Confluence call. Not <c>curl</c>: the refresh skill's
    /// contract says the words "no <c>curl</c>" out loud, and a test that punished a skill for forbidding
    /// something would be the wrong assertion.
    /// </summary>
    private static readonly string[] RestPaths = ["rest/api", "api/v2/pages", "/wiki/api"];

    [Fact]
    public void Every_skill_PLAN_11_names_is_present()
    {
        var missing = Skills
            .Where(skill => !File.Exists(SkillFile(skill)))
            .ToList();

        // Names, not a count: every other test here iterates the same list, so a skill file that vanished
        // would turn them all into vacuous passes.
        missing.ShouldBeEmpty($"Missing SKILL.md under {Directory}/<skill>/.");
    }

    [Fact]
    public void Every_skill_declares_a_name_matching_its_directory()
    {
        foreach (var skill in Skills)
        {
            var frontmatter = Frontmatter(skill);
            var name = Value(frontmatter, "name");

            // `/docs-refresh` resolves by the frontmatter name, not by the folder. Disagree and the slash
            // command in §10's workflow template invokes nothing at all.
            name.ShouldBe(skill, $"{skill}/SKILL.md declares name '{name}'.");
        }
    }

    [Fact]
    public void Every_skill_describes_when_to_use_it()
    {
        foreach (var skill in Skills)
        {
            var description = Value(Frontmatter(skill), "description");

            // The description is the only part of a skill a model sees before deciding to load it, so an
            // empty or one-word one is a skill that never runs. Length is a crude proxy for saying when to
            // use it, and it catches the placeholder, which is the realistic failure.
            description.Length.ShouldBeGreaterThan(
                40,
                $"{skill}/SKILL.md needs a description that says when to use it (§11).");
        }
    }

    [Fact]
    public void Every_skill_states_the_untrusted_input_contract()
    {
        foreach (var skill in Skills)
        {
            var text = Text(skill);

            // Rule §1.3: "State this explicitly in every SKILL.md system contract." Asserted on the two
            // phrases that carry the meaning rather than on a whole sentence, so an editor may rewrite the
            // paragraph without tripping this, but not delete it.
            text.ShouldContain(
                "untrusted input",
                Case.Insensitive,
                $"{skill}/SKILL.md must state that Confluence content is untrusted input (rule §1.3).");
            var claims = $"{skill}/SKILL.md must say Confluence content is claims to verify, never "
                + "instructions to follow (rule §1.3).";

            text.ShouldContain("claims to verify", Case.Insensitive, claims);
        }
    }

    [Fact]
    public void No_skill_reaches_Confluence_around_the_CLI()
    {
        foreach (var skill in Skills)
        {
            var text = Text(skill);

            // Rule §0.4 / §11: skills invoke `docume` and never call the API themselves.
            text.ShouldContain(
                "docume",
                customMessage: $"{skill}/SKILL.md never invokes the CLI, so what is it doing?");

            var found = RestPaths
                .Where(path => text.Contains(path, StringComparison.OrdinalIgnoreCase))
                .ToList();

            found.ShouldBeEmpty(
                $"{skill}/SKILL.md names a Confluence REST path — only the CLI talks to Confluence "
                    + "(rule §0.4).");
        }
    }

    private static string Directory { get; } = Locate();

    private static string SkillFile(string skill) => Path.Combine(Directory, skill, "SKILL.md");

    private static string Text(string skill) => File.ReadAllText(SkillFile(skill));

    /// <summary>
    /// The YAML block between the opening and closing <c>---</c> of <paramref name="skill"/>'s SKILL.md.
    /// </summary>
    private static YamlMappingNode Frontmatter(string skill)
    {
        var lines = Text(skill).Split('\n');

        lines.Length.ShouldBeGreaterThan(2, $"{skill}/SKILL.md is empty.");
        lines[0].Trim().ShouldBe("---", $"{skill}/SKILL.md must open with yaml frontmatter.");

        var closing = Array.FindIndex(
            lines,
            1,
            line => string.Equals(line.Trim(), "---", StringComparison.Ordinal));

        closing.ShouldBeGreaterThan(1, $"{skill}/SKILL.md frontmatter is never closed.");

        var stream = new YamlStream();
        using var reader = new StringReader(string.Join('\n', lines[1..closing]));

        // Load, not Deserialize: a colon in an unquoted description is the mistake hand-written
        // frontmatter actually makes, and it throws here rather than binding to a wrong shape.
        stream.Load(reader);

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string Value(YamlMappingNode frontmatter, string key)
    {
        var entry = frontmatter.Children
            .SingleOrDefault(child => string.Equals(
                ((YamlScalarNode)child.Key).Value,
                key,
                StringComparison.Ordinal));

        entry.Value.ShouldNotBeNull($"Frontmatter has no '{key}'.");

        return ((YamlScalarNode)entry.Value).Value ?? string.Empty;
    }

    /// <summary>
    /// Walks up to the directory holding <c>DocuMe.slnx</c>: the skills ship in the tree, so the test
    /// reads the shipped copy rather than a build artifact of it.
    /// </summary>
    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return Path.Combine(directory.FullName, "plugin", "skills");
            }
        }

        // Not a skip: the skills are committed, so a run that cannot find them is a broken run, and
        // "0 skills checked, all green" would be the worse answer.
        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so plugin/skills cannot be found.");
    }
}
