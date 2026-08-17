using System.Text;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.State;

/// <summary>
/// The hash's preimage and spelling are a published contract: state.json is committed, so a hash
/// computed by this machine is compared against one an earlier run (or another machine, or CI)
/// wrote. Changing either silently republishes every page and revokes every approval, so the
/// constants below are pinned against values computed outside this code (node's crypto), not
/// against <see cref="ContentHash"/> itself.
/// </summary>
public sealed class ContentHashTests
{
    private const string HelloBody = "<p>Hello</p>";
    private const string HelloHash =
        "sha256:d0a26d23e9d8e0538fd47e7bc502d26cf6c320e8daaec7c8521d4769530f5900";

    [Fact]
    public void OfBody_PinsPreimageAndSpelling()
    {
        ContentHash.OfBody(HelloBody).ShouldBe(HelloHash);
    }

    [Fact]
    public void OfBody_NonAscii_HashesUtf8Bytes()
    {
        // Pinned separately from the ASCII case: the encoding is only observable here, and spike
        // S6 flagged non-ASCII round-tripping as the risk to the hash. It cannot destabilize this
        // one — we hash the source-derived body, never a body read back from Confluence.
        ContentHash.OfBody("Ísland — 79 síður")
            .ShouldBe("sha256:beac34015328dc149321fc296047ae300ecf46590da0f2a28c4e8bf27738c247");
    }

    [Fact]
    public void OfBody_Prefixed64LowercaseHex()
    {
        var hash = ContentHash.OfBody(HelloBody);

        hash.ShouldStartWith("sha256:");
        hash.Length.ShouldBe("sha256:".Length + 64);
        hash.ShouldBe(hash.ToLowerInvariant());
    }

    [Theory]
    [InlineData("<p>a</p>\r\n<p>b</p>")]
    [InlineData("<p>a</p>\r<p>b</p>")]
    public void OfBody_LineEndings_HashAsLf(string body)
    {
        // A Windows checkout of the same wiki must not look like a wiki-wide content change.
        ContentHash.OfBody(body).ShouldBe(ContentHash.OfBody("<p>a</p>\n<p>b</p>"));
    }

    [Fact]
    public void OfBody_SurroundingWhitespace_DoesNotChangeHash()
    {
        ContentHash.OfBody($"\n  {HelloBody}\n\n").ShouldBe(HelloHash);
    }

    [Fact]
    public void OfBody_DifferentContent_DifferentHash()
    {
        ContentHash.OfBody("<p>Hello</p>").ShouldNotBe(ContentHash.OfBody("<p>Hallo</p>"));
    }

    [Fact]
    public void OfBody_BannerPrefixedBody_HashesDifferently()
    {
        // Why the pipeline must hash at §6.2 step 5, before injection: the function cannot tell a
        // banner from content, so hashing after injection would make every banner refresh look
        // like a content change and revoke approval (§8, rule §9.2). The exclusion is the caller's
        // to honor; PublishCommand's own test asserts the round trip once the banner exists.
        const string Banner = """<ac:structured-macro ac:name="info"><ac:rich-text-body><p>Generated</p></ac:rich-text-body></ac:structured-macro>""";

        ContentHash.OfBody(Banner + HelloBody).ShouldNotBe(HelloHash);
    }

    [Fact]
    public void OfBody_ConverterOutput_IsStableAcrossRuns()
    {
        // The real preimage: whatever ConfluenceStorageConverter returns. Guards against a
        // renderer that emits anything run-varying (a counter, a timestamp) into the body.
        const string Markdown = "# Loans\n\nDisbursed within 24 hours.\n";

        var first = ContentHash.OfBody(ConfluenceStorageConverter.Convert(Markdown));
        var second = ContentHash.OfBody(ConfluenceStorageConverter.Convert(Markdown));

        first.ShouldBe(second);
    }

    [Fact]
    public void OfBody_CitationCommentLineNumberMoved_DoesNotChangeHash()
    {
        // The business tier's approval-survives-refactor guarantee (business-tier.md mechanism 2,
        // third property): a citation-only edit — a line number moved by a refactor — must never
        // invalidate an approval, because the comment never reaches the body PublishPipeline hashes.
        const string Before = "Leave earned today is available to book tomorrow.\n"
            + "<!-- cites: src/Jobs/AccrualJob.cs:22 -->\n";
        const string After = "Leave earned today is available to book tomorrow.\n"
            + "<!-- cites: src/Jobs/AccrualJob.cs:41 -->\n";

        var before = ContentHash.OfBody(ConfluenceStorageConverter.Convert(Before));
        var after = ContentHash.OfBody(ConfluenceStorageConverter.Convert(After));

        after.ShouldBe(before);
    }

    [Fact]
    public void OfBody_Null_Throws()
    {
        Should.Throw<ArgumentNullException>(() => ContentHash.OfBody(null!));
    }

    [Fact]
    public void OfBytes_PinsSpelling()
    {
        ContentHash.OfBytes([1, 2, 3])
            .ShouldBe("sha256:039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81");
    }

    [Fact]
    public void OfBytes_IsVerbatim_NoNewlineNormalization()
    {
        // Attachments are binary (SVG, PNG). A 0x0D 0x0A pair inside a PNG is data, not a line
        // ending, so normalizing here would make two different files hash the same and skip an
        // upload the page needs.
        var crlf = ContentHash.OfBytes(Encoding.UTF8.GetBytes("a\r\nb"));
        var lf = ContentHash.OfBytes(Encoding.UTF8.GetBytes("a\nb"));

        crlf.ShouldBe("sha256:18745f36a05e29072709042d6062ce54f1b08ff36c27ba80c39f81fb010c8ce2");
        lf.ShouldBe("sha256:7e18f737311b2dc3b2f269dd78396b0351f14fb66efa879f768cb23181883c78");
        crlf.ShouldNotBe(lf);
    }

    [Fact]
    public void OfBytes_Empty_IsTheEmptyDigest()
    {
        ContentHash.OfBytes([])
            .ShouldBe("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }
}
