using Lst.Modules.Shipments.Domain;

namespace Lst.UnitTests;

public sealed class PhoneMaskingTests
{
    [Fact]
    public void U5_turkish_mobile_is_masked_keeping_operator_prefix_and_last_two() =>
        Assert.Equal("+90532*****67", PhoneMasking.Mask("+905321234567"));

    [Fact]
    public void Short_international_number_keeps_less_prefix() =>
        Assert.Equal("+12****78", PhoneMasking.Mask("+12345678"));

    [Fact]
    public void Last4_is_used_for_search() =>
        Assert.Equal("4567", PhoneMasking.Last4("+905321234567"));
}
