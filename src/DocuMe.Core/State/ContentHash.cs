using System.Security.Cryptography;
using System.Text;

namespace DocuMe.Core.State;

/// <summary>
/// Computes the <c>contentHash</c> values stored in <c>_meta/state.json</c> (PLAN.md §5.3):
/// the page hash that drives change detection and approval invalidation, and the per-attachment
/// hash that decides which attachments a publish re-uploads (§6.2 step 5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The preimage is pinned, deliberately.</strong> A page hash is taken over the UTF-8
/// bytes of the <em>converted storage format</em> — exactly what
/// <see cref="Markdown.ConfluenceStorageConverter.Convert"/> returns — after newline
/// normalization and trimming, and its spelling is <c>sha256:</c> + 64 lowercase hex characters
/// (§5.3's own example). Every clause here is load-bearing: the hash is compared against a value
/// a previous run committed to state, so any change of preimage or spelling would look like
/// "every page changed" and would revoke approval on every approved page in the wiki
/// (§8, rule §9.2). <c>ContentHashTests</c> pins both against constants for that reason.
/// </para>
/// <para>
/// <strong>The banner is excluded by construction.</strong> §8 requires that banner-only and
/// machine edits never invalidate approval, and §5.3 defines the hash as "the converted body
/// EXCLUDING banner". This type therefore hashes what the converter produced, and the publish
/// pipeline must hash <em>before</em> injecting the banner (§6.2 orders it that way: step 5
/// computes the hash, then upserts with the banner). Hashing a published body read back from
/// Confluence would break the invariant twice over — it carries the banner, and spike S6 leaves
/// open whether Confluence re-encodes what it stores.
/// </para>
/// <para>
/// Text is normalized the same way <see cref="Markdown.MermaidAttachmentName"/> normalizes
/// diagram sources: line endings collapse to <c>\n</c> and the ends are trimmed, so a Windows
/// checkout of the same wiki produces the same hash as a Linux CI runner. Both are no-ops in
/// storage-format XHTML, where inter-element whitespace carries no meaning.
/// </para>
/// <para>
/// Attachment bytes are hashed <em>verbatim</em> — no normalization. They are binary (rendered
/// SVG, PNG), and a byte that looks like <c>\r\n</c> inside a PNG is not a line ending.
/// </para>
/// </remarks>
public static class ContentHash
{
    /// <summary>The algorithm prefix every stored hash carries, per §5.3.</summary>
    public const string Prefix = "sha256:";

    /// <summary>
    /// Hashes a converted storage-format body, banner excluded.
    /// </summary>
    /// <param name="storageFormat">
    /// The converter's output for one page. Must not include the §8 banner: passing a
    /// banner-carrying body silently breaks approval invalidation rather than failing loud,
    /// which is why the pipeline computes this at §6.2 step 5, before injection.
    /// </param>
    /// <returns><c>sha256:</c> followed by 64 lowercase hex characters.</returns>
    public static string OfBody(string storageFormat)
    {
        ArgumentNullException.ThrowIfNull(storageFormat);

        return OfBytes(Encoding.UTF8.GetBytes(Normalize(storageFormat)));
    }

    /// <summary>
    /// Hashes attachment content verbatim, for the hash comparison that decides the upload set.
    /// </summary>
    /// <param name="content">The exact bytes the publish would upload.</param>
    /// <returns><c>sha256:</c> followed by 64 lowercase hex characters.</returns>
    public static string OfBytes(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(content, hash);

        return string.Concat(Prefix, Convert.ToHexStringLower(hash));
    }

    private static string Normalize(string text) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Trim();
}
