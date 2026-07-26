using System.Text;

namespace DocuMe.Core.Acceptance;

/// <summary>
/// Turns a diagnostic message into a grouping key by replacing the specific token it quotes with
/// an ellipsis, and hands back that token.
/// </summary>
/// <remarks>
/// <para>
/// Both halves of an acceptance run need this, on different quote characters, and for the same
/// reason: the tool that rejected the page interpolated the offending token into prose, so the
/// prose is the construct and the token is the dialect. The converter's fail-loud sites quote with
/// <c>'</c> (<see cref="ConversionFailure"/>); <c>beautiful-mermaid</c> quotes the diagram header
/// with <c>"</c> (<see cref="DiagramFailureGroup"/>). Normalizing is what makes "how many pages
/// hit this" readable separately from "which spellings", without either tool having to grow
/// structured error codes.
/// </para>
/// </remarks>
internal static class QuotedTokens
{
    /// <summary>
    /// Replaces every quoted run in a tool's message with an ellipsis, and reports the first one.
    /// </summary>
    /// <param name="text">The message verbatim.</param>
    /// <param name="quote">The quote character the message uses around its tokens.</param>
    /// <returns>
    /// The message with every quoted run replaced by <c>…</c> between quotes, and the first quoted
    /// run. Both are the message itself and <c>null</c> when it quotes nothing.
    /// </returns>
    internal static (string Normalized, string? First) Normalize(string text, char quote)
    {
        // Odd-indexed segments of the split are the quoted ones. A message ending mid-quote (an
        // odd number of quote characters, e.g. an apostrophe in prose) leaves a trailing unpaired
        // segment that is put back verbatim rather than mistaken for a token.
        var segments = text.Split(quote);
        if (segments.Length < 3)
        {
            return (text, null);
        }

        var normalized = new StringBuilder();
        string? first = null;

        for (var i = 0; i < segments.Length; i++)
        {
            if (i % 2 == 0)
            {
                _ = normalized.Append(segments[i]);
                continue;
            }

            if (i == segments.Length - 1)
            {
                _ = normalized.Append(quote).Append(segments[i]);
                continue;
            }

            first ??= segments[i];
            _ = normalized.Append(quote).Append('…').Append(quote);
        }

        return (normalized.ToString(), first);
    }

    /// <summary>
    /// Builds the human-readable message for a set of messages that already share a
    /// <see cref="Normalize"/> key: a quoted run they all agree on stays verbatim, and only the
    /// runs that actually differ collapse to an ellipsis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Normalize"/> elides every quoted run because it is building a <em>key</em>, and a
    /// key must not carry anything that varies. Reading that key back to a person throws away the
    /// half they can act on: <c>beautiful-mermaid</c> answers a bad header with
    /// <c>Invalid mermaid header: "graph TD;". Expected "graph TD", "flowchart LR",
    /// "stateDiagram-v2", etc.</c> — the first token is the offender and varies per diagram, but the
    /// expected list is constant prose that happens to be quoted, and eliding it leaves
    /// <c>Expected "…", "…", "…", etc.</c>, which names nothing to write instead.
    /// </para>
    /// <para>
    /// Within one group the varying runs are exactly the ones the messages disagree on, so
    /// membership in the group is itself the test. A group of one elides nothing and reads exactly
    /// as <c>publish</c> prints it.
    /// </para>
    /// </remarks>
    /// <param name="messages">The group's messages verbatim; must not be empty.</param>
    /// <param name="quote">The quote character the messages use around their tokens.</param>
    /// <returns>
    /// The shared message with only the differing quoted runs elided. Falls back to
    /// <see cref="Normalize"/> of the first message if the group's messages do not share a shape,
    /// which a shared key should already rule out.
    /// </returns>
    internal static string Common(IReadOnlyList<string> messages, char quote)
    {
        if (messages.Count == 1)
        {
            return messages[0];
        }

        var split = messages.Select(message => message.Split(quote)).ToList();
        var shape = split[0];

        bool AgreeAt(int index) =>
            split.TrueForAll(segments => string.Equals(segments[index], shape[index], StringComparison.Ordinal));

        if (!split.TrueForAll(segments => segments.Length == shape.Length) || shape.Length < 3)
        {
            return Normalize(messages[0], quote).Normalized;
        }

        var common = new StringBuilder();

        for (var i = 0; i < shape.Length; i++)
        {
            if (i % 2 == 0)
            {
                // Same key implies the same prose, so a disagreement here means the key collided on
                // two genuinely different messages. Key behavior wins: report the key.
                if (!AgreeAt(i))
                {
                    return Normalize(messages[0], quote).Normalized;
                }

                _ = common.Append(shape[i]);
                continue;
            }

            if (i == shape.Length - 1)
            {
                // Trailing unpaired segment: prose, not a token (Normalize treats it the same way).
                _ = common.Append(quote).Append(shape[i]);
                continue;
            }

            _ = common.Append(quote).Append(AgreeAt(i) ? shape[i] : "…").Append(quote);
        }

        return common.ToString();
    }
}
