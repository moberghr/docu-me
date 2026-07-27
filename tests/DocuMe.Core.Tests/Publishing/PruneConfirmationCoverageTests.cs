using DocuMe.Core.Tests.Cli;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// The structural half of rule §9.6 — "orphan deletion requires interactive confirmation and never runs
/// in CI" — asserted over the tree rather than over the one wiring that happens to be correct today.
/// </summary>
/// <remarks>
/// <para>
/// The rule's two halves are enforced in different ways, and only one of them had anything holding it.
/// The CI half is a computation: <see cref="PruneGuardTests"/> drives <c>PruneGuard.Refusal</c> through an
/// injected environment reader, so deleting the refusal turns that class red. The confirmation half is not
/// a computation at all — it is <em>one argument at one call site</em>. <c>PruneExecutor</c> deletes
/// whatever the delegate it was handed agrees to, by design, so that the "no" case is testable offline;
/// every test that reaches the executor therefore hands it a stub, and the production delegate is the only
/// thing that makes a human's answer a precondition for a delete.
/// </para>
/// <para>
/// Measured before this class existed: replacing that argument with an auto-yes and deleting the prompt
/// with it left <c>PruneGuardTests</c>, <c>PruneExecutorTests</c>, <c>PrunePlannerTests</c>,
/// <c>CliConfluenceTests</c> and <c>PublishPipelineTests</c> — 73 tests, every class that owns a piece of
/// <c>--prune</c> — entirely green. So did flipping the prompt's <c>defaultValue</c> from no to yes.
/// A prune that deleted pages unattended was one edit away from shipping green.
/// </para>
/// <para>
/// The shape is <see cref="WriteLockCoverageTests"/>'s, for the same reason it gives: the property is a
/// statement about the whole tree, so no run of the path that is wired correctly can show that a second
/// path is. What this class does <em>not</em> claim is that the prompt is well worded or that Spectre asks
/// it properly — <see cref="Cli.CliConfluenceTests"/> owns the refusal that fires when no terminal can be
/// asked at all. It pins that the delete is reached only through a named prompt that defaults to no, that
/// the prompt hands the answer back, and that every file reaching the executor sits behind the guard.
/// </para>
/// <para>
/// That third fact was added after measuring this class against its own residual, the audit
/// <c>AuthHardStopCoverageTests</c> earned for rule §1.2: a grep over a mechanism's body reads a
/// <em>toothless</em> mechanism as compliant. A prompt that awaits the confirmation, discards what the
/// human said and returns <c>true</c> still contains <c>ConfirmAsync(</c> and still contains
/// <c>defaultValue: false</c>, so it satisfied every fact here — and nothing else could object either,
/// because the production prompt is unreachable from any test: it is private, it is driven by the static
/// <c>AnsiConsole</c>, and <c>PublishCommand.PruneRefusal</c> turns <c>--prune</c> away before the prune
/// runs at all when the terminal cannot prompt, which is every test host. Measured, not argued: that
/// mutation failed <em>nothing</em> in the whole suite as it stood when this fact was written, and now
/// fails this fact alone — no count is quoted here on purpose, because a number in prose is a mirror
/// nobody diffs and the suite's floor already lives in one place.
/// </para>
/// </remarks>
public sealed class PruneConfirmationCoverageTests
{
    /// <summary>
    /// The invocation that hands <c>PruneExecutor</c> its confirmation. Matched with the leading dot on
    /// purpose: <c>PublishCommand</c> has a private helper of its own called <c>PruneAsync</c>, and its
    /// call and declaration would both match the bare name.
    /// </summary>
    private const string ExecutorCall = ".PruneAsync(";

    /// <summary>Constructing the executor is the other way to reach a delete, so it is tracked too.</summary>
    private const string ExecutorConstruction = "new PruneExecutor(";

    /// <summary>The refusal that carries §9.6's CI half and the §0.1/§1.4 write lock.</summary>
    private const string GuardCall = "PruneGuard.Refusal(";

    /// <summary>The ask itself. One spelling, read by the two facts that need it.</summary>
    private const string ConfirmCall = "ConfirmAsync(";

    /// <summary>Zero-based position of the <c>PruneConfirmation</c> parameter in <c>PruneAsync</c>.</summary>
    private const int ConfirmationArgument = 2;

    /// <summary>
    /// Every file outside <c>PruneExecutor</c> itself that reaches a prune. One, as traced by hand before
    /// this class existed.
    /// </summary>
    private static readonly string[] PruneCallers = ["PublishCommand.cs"];

    private const string ExecutorFile = "PruneExecutor.cs";

    /// <summary>
    /// The set of files that can reach a delete is the set this class knows about — the assertion a second
    /// prune path fails.
    /// </summary>
    [Fact]
    public void Every_file_that_can_reach_a_prune_is_one_this_class_knows_about()
    {
        var reaching = Sources()
            .Where(file => !string.Equals(Path.GetFileName(file), ExecutorFile, StringComparison.Ordinal))
            .Where(file => Reaches(File.ReadAllText(file)))
            .Select(file => Path.GetFileName(file)!)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Vacuous-pass guard. A renamed executor or a moved src/ would otherwise compare two empty sets
        // and report the confirmation fully covered.
        const string lost = "Nothing in src/ reaches PruneExecutor, which cannot be true while `publish --prune` "
            + "ships. The scan has stopped reading the tree rather than found the prune gone.";

        reaching.ShouldNotBeEmpty(lost);

        const string added = "A file can reach an orphan delete without this class knowing about it. Rule §9.6 says "
            + "the delete is confirmed interactively and never runs in CI, and both are enforced by what "
            + "sits in front of the call rather than by PruneExecutor, which deletes whatever its "
            + "delegate agrees to. Confirm the new path passes a real prompt and checks PruneGuard, then "
            + "add it here. Reaching a prune:";

        reaching.ShouldBe(PruneCallers.Order(StringComparer.Ordinal).ToList(), added);
    }

    /// <summary>
    /// Every prune call site passes a named prompt, not an inline delegate — the assertion an auto-yes
    /// fails.
    /// </summary>
    /// <remarks>
    /// A lambda is refused rather than inspected on purpose. <c>(_, _) =&gt; Task.FromResult(true)</c> and a
    /// lambda that really does prompt are the same shape to any scan short of a compiler, and the second
    /// one has no reason to exist: the prompt belongs in a named method where it can be read and pinned.
    /// </remarks>
    [Fact]
    public void Every_prune_call_site_confirms_through_a_named_prompt()
    {
        var sites = CallSites();

        const string lost = "No `.PruneAsync(` invocation was found in src/. The scan has stopped matching rather "
            + "than the prune having gone away.";

        sites.ShouldNotBeEmpty(lost);

        foreach (var (file, arguments) in sites)
        {
            var enough = $"{file} calls PruneAsync with {arguments.Count} argument(s); the confirmation is "
                + $"argument {ConfirmationArgument + 1}. The signature has changed — re-read "
                + "PruneExecutor.PruneAsync against rule §9.6 before repinning.";

            arguments.Count.ShouldBeGreaterThan(ConfirmationArgument, enough);

            var confirmation = arguments[ConfirmationArgument];

            var inline = $"{file} passes `{confirmation}` as the confirmation rule §9.6 requires. An inline "
                + "delegate is indistinguishable from an auto-yes to anything but a compiler, and this is "
                + "the one call in DocuMe that cannot be undone outside the space trash. Move the prompt "
                + "into a named method in this file and pass it by name.";

            IsIdentifier(confirmation).ShouldBeTrue(inline);

            var declared = PromptBody(file, confirmation);

            var missing = $"{file} passes `{confirmation}` as its prune confirmation, and no method of that "
                + "name is declared in the same file. This class reads the prompt's body to check it "
                + "actually asks, so a prompt it cannot find is a prompt nothing pins.";

            declared.ShouldNotBeNull(missing);
        }
    }

    /// <summary>
    /// The prompt each call site names actually asks a human, and defaults to no — the assertion a
    /// <c>defaultValue: true</c> fails.
    /// </summary>
    [Fact]
    public void Every_named_prompt_asks_and_defaults_to_no()
    {
        foreach (var (file, arguments) in CallSites())
        {
            var confirmation = arguments[ConfirmationArgument];
            var body = PromptBody(file, confirmation);

            body.ShouldNotBeNull($"{confirmation} is not declared in {file}.");

            var silent = $"{file}'s `{confirmation}` never calls ConfirmAsync, so the prune's confirmation "
                + "does not ask anybody anything. Rule §9.6 wants a human's answer in front of the "
                + "delete, not a method named as though there were one.";

            body.ShouldContain(ConfirmCall, customMessage: silent);

            var defaulted = $"{file}'s `{confirmation}` does not pass `defaultValue: false`. A destructive "
                + "prompt that defaults to yes turns a stray newline into a delete, which is the one "
                + "mistake rule §9.6 exists to make impossible.";

            body.ShouldContain("defaultValue: false", customMessage: defaulted);
        }
    }

    /// <summary>
    /// The answer each prompt is given is the answer it hands back — the assertion a prompt that asks and
    /// discards fails.
    /// </summary>
    /// <remarks>
    /// Two shapes are accepted, and anything else is refused rather than reasoned about: the ask is the
    /// returned expression, or it is assigned to a local that the body returns. A prompt written some third
    /// way is not wrong, but it is unreadable to a scan, and this is the call that cannot be undone outside
    /// the space trash — so the refusal asks for the prompt to be rewritten or this fact to be repinned
    /// deliberately, rather than guessing which locals reach a <c>return</c>.
    /// </remarks>
    [Fact]
    public void Every_named_prompt_returns_the_answer_the_human_gave()
    {
        foreach (var (file, arguments) in CallSites())
        {
            var confirmation = arguments[ConfirmationArgument];
            var body = PromptBody(file, confirmation);

            body.ShouldNotBeNull($"{confirmation} is not declared in {file}.");

            var statement = AskingStatement(body);

            // Vacuous-pass guard. Two causes, and the message names both because only one of them is this
            // fact's business: a prompt that stopped asking is fact 3's finding and fails there in the same
            // run, while a body no statement can be read out of is this scan having gone blind. Either way,
            // returning early here would report the answer honoured by a prompt that never took one.
            var unreadable = $"{file}'s `{confirmation}` contains no statement this scan can attribute a "
                + $"{ConfirmCall} call to. Either the prompt no longer asks — check whether "
                + $"{nameof(Every_named_prompt_asks_and_defaults_to_no)} failed alongside this, which is "
                + "where that belongs — or this scan has stopped reading the body, in which case check "
                + "AskingStatement against the declaration's shape.";

            statement.ShouldNotBeNull(unreadable);

            var opening = statement[0].Trim();

            if (opening.StartsWith("return", StringComparison.Ordinal))
            {
                continue;
            }

            var held = Assigned(opening);

            var discarded = $"{file}'s `{confirmation}` asks and then drops the answer: the statement "
                + $"holding the ask reads `{opening}`, which neither returns it nor assigns it to a local. "
                + "A prompt that awaits a human's \"no\" and deletes anyway is rule §9.6 satisfied on paper "
                + "and broken in the one way that matters, and it reads as compliant to every other fact "
                + "in this class.";

            held.ShouldNotBeNull(discarded);

            var stranded = $"{file}'s `{confirmation}` puts the answer in `{held}` and never returns it, so "
                + "whatever it does hand back is not what the human said.";

            body.ShouldContain($"return {held};", customMessage: stranded);
        }
    }

    /// <summary>
    /// Every file that can reach a prune also holds the guard carrying §9.6's CI half.
    /// </summary>
    /// <remarks>
    /// The confirmation and the CI refusal fail differently and so are pinned separately: a terminal that
    /// cannot prompt is a broken precondition the operator sees, while CI is a shell where the prompt
    /// would never be read at all. A prune path that asks but does not check the guard would pass every
    /// other test here.
    /// </remarks>
    [Fact]
    public void Every_prune_caller_checks_the_guard_before_it_deletes()
    {
        foreach (var name in PruneCallers)
        {
            var text = File.ReadAllText(SourceNamed(name));

            var unguarded = $"{name} can reach an orphan delete without calling PruneGuard.Refusal. That is "
                + "where §9.6's \"never runs in CI\" lives, and where the §0.1/§1.4 write lock is checked "
                + "for a delete — both are enforced by the call sitting in front of the executor.";

            text.ShouldContain(GuardCall, customMessage: unguarded);

            var order = text.IndexOf(GuardCall, StringComparison.Ordinal);
            var deletes = text.IndexOf(ExecutorCall, StringComparison.Ordinal);

            var backwards = $"{name} calls PruneGuard.Refusal after it reaches PruneExecutor. A run that "
                + "deleted pages and then reported it should not have been allowed to has the order "
                + "exactly backwards.";

            order.ShouldBeLessThan(deletes, backwards);
        }
    }

    /// <summary>Whether <paramref name="text"/> reaches a prune, by call or by construction.</summary>
    private static bool Reaches(string text) =>
        text.Contains(ExecutorCall, StringComparison.Ordinal)
        || text.Contains(ExecutorConstruction, StringComparison.Ordinal);

    /// <summary>Every <c>.PruneAsync(</c> invocation in <c>src/</c>, with its arguments split out.</summary>
    private static List<(string File, List<string> Arguments)> CallSites()
    {
        var sites = new List<(string File, List<string> Arguments)>();

        foreach (var file in Sources())
        {
            var name = Path.GetFileName(file)!;

            if (string.Equals(name, ExecutorFile, StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var at = text.IndexOf(ExecutorCall, StringComparison.Ordinal);

            while (at >= 0)
            {
                sites.Add((name, Arguments(text, at + ExecutorCall.Length)));
                at = text.IndexOf(ExecutorCall, at + ExecutorCall.Length, StringComparison.Ordinal);
            }
        }

        return sites;
    }

    /// <summary>
    /// The top-level arguments of an invocation whose open paren has just been consumed, each trimmed.
    /// </summary>
    /// <remarks>
    /// Depth-counted rather than matched with a regular expression: a character class stops at the first
    /// closing bracket it meets, which is how a scan over C# comes to misread a nested generic and report
    /// a confident wrong answer. String literals are skipped so a comma inside the prompt's text is not
    /// read as an argument separator.
    /// </remarks>
    private static List<string> Arguments(string text, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var inString = false;
        var argumentStart = start;

        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];

            if (inString)
            {
                inString = character switch
                {
                    '\\' => Skip(ref index),
                    '"' => false,
                    _ => true,
                };

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character is '(' or '[')
            {
                depth++;
                continue;
            }

            if (character is ']')
            {
                depth--;
                continue;
            }

            if (character == ')')
            {
                if (depth == 0)
                {
                    arguments.Add(text[argumentStart..index].Trim());

                    return arguments;
                }

                depth--;
                continue;
            }

            if (character == ',' && depth == 0)
            {
                arguments.Add(text[argumentStart..index].Trim());
                argumentStart = index + 1;
            }
        }

        return arguments;
    }

    /// <summary>Consumes the character escaped by a backslash, staying inside the literal.</summary>
    private static bool Skip(ref int index)
    {
        index++;

        return true;
    }

    /// <summary>
    /// The statement in <paramref name="body"/> that holds the <see cref="ConfirmCall"/>, as its lines, or
    /// <c>null</c> when no line makes that call.
    /// </summary>
    /// <remarks>
    /// Walked back line by line to the previous statement boundary rather than parsed: the ask is a fluent
    /// chain broken over three lines, so the line the call sits on says nothing about what happens to its
    /// result. The declaration's own opening brace is a boundary, which is what stops the walk at the top of
    /// a body whose first statement is the ask.
    /// </remarks>
    private static string[]? AskingStatement(string body)
    {
        var lines = body.Split(Environment.NewLine);
        var at = Array.FindIndex(lines, line => line.Contains(ConfirmCall, StringComparison.Ordinal));

        if (at < 0)
        {
            return null;
        }

        var start = at;

        while (start > 0 && !Bounds(lines[start - 1]))
        {
            start--;
        }

        return lines[start..(at + 1)];
    }

    /// <summary>
    /// Whether <paramref name="line"/> closes a statement, opens a block, or is blank.
    /// </summary>
    /// <remarks>
    /// A blank line counts, and leaving it out is what the first run of this fact caught: the ask sits one
    /// blank line below <c>RenderPaths</c>, so a walk that stepped over it reported the statement as the
    /// empty string and failed the compliant tree.
    /// </remarks>
    private static bool Bounds(string line)
    {
        var trimmed = line.TrimEnd();

        return trimmed.Length == 0
            || trimmed.EndsWith(';')
            || trimmed.EndsWith('{')
            || trimmed.EndsWith('}');
    }

    /// <summary>
    /// The local <paramref name="statement"/> declares or assigns, or <c>null</c> when it is not an
    /// assignment. Comparisons are excluded so <c>if (x == y)</c> is not read as one.
    /// </summary>
    /// <remarks>
    /// Split rather than indexed: <c>MA0001</c> refuses <c>IndexOf(string)</c> without a
    /// <c>StringComparison</c> and <c>CA1865</c> refuses the string overload for a single character, so the
    /// two rules leave no spelling of that call standing. Splitting once needs neither.
    /// </remarks>
    private static string? Assigned(string statement)
    {
        var parts = statement.Split('=', 2);

        if (parts.Length < 2 || parts[1].StartsWith('='))
        {
            return null;
        }

        var head = parts[0].TrimEnd();

        if (head.Length == 0 || head[^1] is '!' or '<' or '>')
        {
            return null;
        }

        var target = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return target.Length >= 2 ? target[^1] : null;
    }

    /// <summary>Whether <paramref name="argument"/> is a bare identifier — a method group, not a lambda.</summary>
    private static bool IsIdentifier(string argument) =>
        argument.Length > 0
        && (char.IsLetter(argument[0]) || argument[0] == '_')
        && argument.All(character => char.IsLetterOrDigit(character) || character == '_');

    /// <summary>
    /// The source of the method named <paramref name="method"/> in <paramref name="file"/>, from its
    /// declaration to the closing brace at member indentation, or <c>null</c> when it declares none.
    /// </summary>
    private static string? PromptBody(string file, string method)
    {
        var lines = File.ReadAllLines(SourceNamed(file));
        var opening = Array.FindIndex(
            lines,
            line => line.TrimStart().StartsWith("private ", StringComparison.Ordinal)
                && line.Contains($" {method}(", StringComparison.Ordinal));

        if (opening < 0)
        {
            return null;
        }

        var closing = Array.FindIndex(lines, opening, line => string.Equals(line, "    }", StringComparison.Ordinal));

        return closing < 0 ? null : string.Join(Environment.NewLine, lines[opening..closing]);
    }

    private static string SourceNamed(string name) => Sources()
        .Single(file => string.Equals(Path.GetFileName(file), name, StringComparison.Ordinal));

    /// <summary>Every committed C# source under <c>src/</c>, build output excluded.</summary>
    private static IEnumerable<string> Sources() => Directory
        .EnumerateFiles(Path.Combine(DocumeCli.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
