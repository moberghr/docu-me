using System.Security.Cryptography;
using System.Text;

namespace DocuMe.Core.Markdown;

/// <summary>
/// Names the Confluence attachment a <c>```mermaid</c> fence's rendered diagram is uploaded
/// under. The name is a <em>pure function of the diagram source</em> — this type is the one
/// place that decides it, shared by the publish pipeline's <see cref="MermaidDiagramResolver"/>
/// wiring and by <see cref="MermaidRenderer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Purity is a constraint, not a convenience (PLAN.md §8, §9.2 / rule §9.2). The filename
/// lands in the published body and therefore in the page's <c>contentHash</c>, so a name that
/// varied per render — a counter, a timestamp, a GUID — would churn the hash on every publish
/// and revoke approvals on pages where nothing changed.
/// </para>
/// <para>
/// The source is normalized before hashing so the same diagram yields the same name on every
/// machine: line endings collapse to <c>\n</c> (a Windows checkout of the same wiki must not
/// produce a second attachment) and surrounding whitespace is trimmed. Normalization affects
/// the name only — <see cref="MermaidRenderer"/> renders the source it was handed.
/// </para>
/// </remarks>
public static class MermaidAttachmentName
{
    /// <summary>
    /// The extension every rendered diagram is stored under, in <em>one</em> place on purpose.
    /// </summary>
    /// <remarks>
    /// Open sandbox question (loop state item 8): whether Confluence renders an attached SVG
    /// inline via <c>&lt;ri:attachment&gt;</c> or degrades it to a download link. If it forces
    /// a download, the render script must emit PNG instead and this constant is the only edit
    /// on the naming side.
    /// </remarks>
    public const string Extension = ".svg";

    private const string Prefix = "mermaid-";

    /// <summary>
    /// 16 hex characters — 64 bits of SHA-256. Collision risk across the few dozen diagrams
    /// in a wiki is negligible, and a short name keeps the published body readable.
    /// </summary>
    private const int HashLength = 16;

    /// <summary>
    /// Returns the attachment filename for <paramref name="mermaidSource"/>, e.g.
    /// <c>mermaid-3f2a1c9d0b8e7f65.svg</c>.
    /// </summary>
    /// <param name="mermaidSource">The fence body verbatim, as the author wrote it.</param>
    /// <exception cref="ArgumentException">
    /// The source is empty or whitespace: there is no diagram to name. The converter already
    /// fails loud on an empty mermaid fence; this guards the pipeline against naming nothing.
    /// </exception>
    public static string ForSource(string mermaidSource)
    {
        ArgumentNullException.ThrowIfNull(mermaidSource);

        var normalized = Normalize(mermaidSource);
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "Cannot name a diagram attachment for an empty mermaid source.",
                nameof(mermaidSource));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return string.Concat(Prefix, Convert.ToHexStringLower(hash).AsSpan(0, HashLength), Extension);
    }

    private static string Normalize(string mermaidSource) => mermaidSource
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Trim();
}
