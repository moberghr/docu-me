using DocuMe.Core.Publishing;
using Shouldly;

namespace DocuMe.Core.Tests.Publishing;

/// <summary>
/// The managed-marker value contract (docs/specs/2026-08-18-managed-marker.md,
/// <see cref="ManagedMarker"/>): what a stamp writes, and what the prune's live check accepts back.
/// Both halves are pure, so neither needs a server.
/// </summary>
public sealed class ManagedMarkerTests
{
    /// <summary>
    /// The exact compact spelling the spec pins, byte for byte. The value is a wire payload other runs
    /// read back and a future <c>state rebuild</c> queries, so "roughly this JSON" is not a contract:
    /// no indentation, <c>managed</c> before <c>path</c>, camel-cased names.
    /// </summary>
    [Fact]
    public void Stamps_the_exact_compact_value_the_spec_pins()
        => ManagedMarker.ValueFor("a.md").ShouldBe("""{"managed":true,"path":"a.md"}""");

    /// <summary>
    /// The round trip the lifecycle depends on: a page stamped by <see cref="ManagedMarker.ValueFor"/>
    /// must read back as managed, or <c>--prune</c> would refuse every page DocuMe itself wrote.
    /// </summary>
    [Fact]
    public void Accepts_its_own_stamp_back()
        => ManagedMarker.IsManaged(ManagedMarker.ValueFor("a.md")).ShouldBeTrue();

    /// <summary>
    /// Everything that is not this tool's marker reads as unmanaged: a flag saying false, JSON null, an
    /// array, a bare number, an object that never says <c>managed</c>, text that does not parse, and
    /// nothing at all. The one caller is about to delete a page, so "cannot tell" must land on the side
    /// that deletes nothing — somebody else's property under the same key is a page somebody else owns.
    /// </summary>
    [Theory]
    [InlineData("""{"managed":false,"path":"a.md"}""")]
    [InlineData("null")]
    [InlineData("""[{"managed":true,"path":"a.md"}]""")]
    [InlineData("42")]
    [InlineData("""{"path":"a.md"}""")]
    [InlineData("""{"managed":true,""")]
    [InlineData("")]
    [InlineData("   ")]
    public void Reads_everything_that_is_not_its_own_stamp_as_unmanaged(string rawValue)
        => ManagedMarker.IsManaged(rawValue).ShouldBeFalse();
}
