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
}
