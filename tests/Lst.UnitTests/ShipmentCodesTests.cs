using System.Text.RegularExpressions;
using Lst.Modules.Shipments.Domain;

namespace Lst.UnitTests;

public sealed class ShipmentCodesTests
{
    [Fact]
    public void U1_generated_tracking_numbers_match_format_and_luhn()
    {
        var generator = new RandomShipmentCodeGenerator();
        var format = new Regex("^LS[0-9]{10}$");
        for (var i = 0; i < 10_000; i++)
        {
            var trackingNumber = generator.NextTrackingNumber();
            Assert.Matches(format, trackingNumber);
            Assert.True(TrackingNumbers.IsValid(trackingNumber), trackingNumber);
        }
    }

    [Theory]
    [InlineData("LS4829105735", true)]
    [InlineData("LS4829105736", false)]
    [InlineData("LS000000000", false)]
    [InlineData("XS4829105735", false)]
    [InlineData("LS48291057A5", false)]
    public void Luhn_known_vectors(string trackingNumber, bool expected) =>
        Assert.Equal(expected, TrackingNumbers.IsValid(trackingNumber));

    [Fact]
    public void Label_code_uses_two_digit_sequence() =>
        Assert.Equal("LS4829105735-02", TrackingNumbers.LabelCode("LS4829105735", 2));

    [Fact]
    public void U2_public_code_is_26_crockford_characters()
    {
        var generator = new RandomShipmentCodeGenerator();
        for (var i = 0; i < 2_000; i++)
        {
            var code = generator.NextPublicTrackingCode();
            Assert.Equal(26, code.Length);
            Assert.All(code, c => Assert.Contains(c, CrockfordBase32.Alphabet));
            Assert.DoesNotContain('I', code);
            Assert.DoesNotContain('L', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('U', code);
        }
    }

    [Fact]
    public void Crockford_encoding_known_vectors()
    {
        Assert.Equal(new string('0', 26), CrockfordBase32.Encode(new byte[16]));
        Assert.Equal("ZZZZZZZZZZZZZZZZZZZZZZZZZW", CrockfordBase32.Encode(Enumerable.Repeat((byte)0xFF, 16).ToArray()));
        Assert.Equal("CSQPYRK1", CrockfordBase32.Encode("fooba"u8));
    }
}
