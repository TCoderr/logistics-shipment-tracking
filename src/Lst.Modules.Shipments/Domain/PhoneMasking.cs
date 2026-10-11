namespace Lst.Modules.Shipments.Domain;

public static class PhoneMasking
{
    /// <summary>E.164 numarada ülke + operatör önekini ve son 2 haneyi bırakır: +905321234567 → +90532*****67.</summary>
    public static string Mask(string phoneE164)
    {
        ArgumentNullException.ThrowIfNull(phoneE164);
        const int suffix = 2;
        var prefix = phoneE164.Length >= 12 ? 6 : 3;
        if (phoneE164.Length <= prefix + suffix)
        {
            return new string('*', phoneE164.Length);
        }

        return string.Concat(
            phoneE164.AsSpan(0, prefix),
            new string('*', phoneE164.Length - prefix - suffix),
            phoneE164.AsSpan(phoneE164.Length - suffix));
    }

    public static string Last4(string phoneE164)
    {
        ArgumentNullException.ThrowIfNull(phoneE164);
        return phoneE164.Length >= 4 ? phoneE164[^4..] : phoneE164;
    }
}
