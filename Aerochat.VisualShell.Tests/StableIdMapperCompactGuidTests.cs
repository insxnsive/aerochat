using System.Globalization;
using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class StableIdMapperCompactGuidTests
{
    [TestCase("0")]
    [TestCase("1")]
    [TestCase("2001")]
    [TestCase("18446744073709551615")]
    public void All_digit_compact_GUID_maps_like_the_same_hyphenated_GUID(string digits)
    {
        string compact = digits.PadLeft(32, '0');
        Guid id = Guid.ParseExact(compact, "N");
        Assert.That(StableIdMapper.TryMap(compact, out ulong compactId), Is.True);
        Assert.That(StableIdMapper.TryMap(id.ToString("D"), out ulong hyphenatedId), Is.True);
        Assert.That(compactId, Is.EqualTo(StableIdMapper.Map(id)));
        Assert.That(compactId, Is.EqualTo(hyphenatedId));
    }

    [TestCase("0")]
    [TestCase("2001")]
    [TestCase("18446744073709551615")]
    public void Ordinary_decimal_ID_still_passes_through(string digits)
    {
        Assert.That(StableIdMapper.TryMap(digits, out ulong mapped), Is.True);
        Assert.That(mapped, Is.EqualTo(ulong.Parse(digits, CultureInfo.InvariantCulture)));
    }

    [Test]
    public void Compact_hex_GUID_and_casing_remain_stable()
    {
        Guid id = Guid.Parse("abcdefab-1234-5678-90ab-abcdefabcdef");
        foreach (string spelling in new[] { id.ToString("N"), id.ToString("N").ToUpperInvariant(), id.ToString("D") })
        {
            Assert.That(StableIdMapper.TryMap(spelling, out ulong mapped), Is.True);
            Assert.That(mapped, Is.EqualTo(StableIdMapper.Map(id)));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-an-id")]
    public void Invalid_ID_is_rejected(string? value) =>
        Assert.That(StableIdMapper.TryMap(value, out _), Is.False);
}
