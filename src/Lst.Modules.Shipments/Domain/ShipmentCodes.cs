using System.Globalization;
using System.Security.Cryptography;

namespace Lst.Modules.Shipments.Domain;

/// <summary>Tracking numarası: "LS" + 9 rastgele rakam + Luhn kontrol hanesi (docs/02 §8).</summary>
public static class TrackingNumbers
{
    public const string Prefix = "LS";
    public const int Length = 12;

    public static string FromPayload(int nineDigitPayload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nineDigitPayload);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nineDigitPayload, 999_999_999);
        var payload = nineDigitPayload.ToString("D9", CultureInfo.InvariantCulture);
        return Prefix + payload + ComputeLuhnCheckDigit(payload).ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsValid(string? trackingNumber)
    {
        if (trackingNumber is null || trackingNumber.Length != Length || !trackingNumber.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = trackingNumber.AsSpan(Prefix.Length);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return ComputeLuhnCheckDigit(digits[..^1].ToString()) == digits[^1] - '0';
    }

    public static int ComputeLuhnCheckDigit(string payloadDigits)
    {
        ArgumentNullException.ThrowIfNull(payloadDigits);
        var sum = 0;
        var doubleIt = true; // kontrol hanesi eklenecek; payload'ın en sağ hanesi ikiye katlanır
        for (var i = payloadDigits.Length - 1; i >= 0; i--)
        {
            var d = payloadDigits[i] - '0';
            if (d is < 0 or > 9)
            {
                throw new ArgumentException("Yalnız rakam beklenir.", nameof(payloadDigits));
            }

            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return (10 - (sum % 10)) % 10;
    }

    public static string LabelCode(string trackingNumber, int sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"{trackingNumber}-{sequence:00}");
}

/// <summary>Crockford Base32 (I, L, O, U yok). 16 bayt → 26 karakter.</summary>
public static class CrockfordBase32
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var outputLength = ((data.Length * 8) + 4) / 5;
        return string.Create(outputLength, data.ToArray(), static (chars, bytes) =>
        {
            var buffer = 0;
            var bits = 0;
            var index = 0;
            foreach (var b in bytes)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    chars[index++] = Alphabet[(buffer >> (bits - 5)) & 31];
                    bits -= 5;
                }
            }

            if (bits > 0)
            {
                chars[index] = Alphabet[(buffer << (5 - bits)) & 31];
            }
        });
    }
}

/// <summary>Kod üretimi için test sınırı: çakışma testleri deterministik sahte üretici kullanır.</summary>
public interface IShipmentCodeGenerator
{
    string NextTrackingNumber();

    string NextPublicTrackingCode();
}

public sealed class RandomShipmentCodeGenerator : IShipmentCodeGenerator
{
    public const int PublicCodeBytes = 16;

    public string NextTrackingNumber() => TrackingNumbers.FromPayload(RandomNumberGenerator.GetInt32(0, 1_000_000_000));

    public string NextPublicTrackingCode()
    {
        Span<byte> bytes = stackalloc byte[PublicCodeBytes];
        RandomNumberGenerator.Fill(bytes);
        return CrockfordBase32.Encode(bytes);
    }
}
