using System.Text.Json;
using System.Text.Json.Nodes;
using Lst.Modules.Shipments.Application;

namespace Lst.UnitTests;

public sealed class RequestHashAndValidationTests
{
    private static string Mutate(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(SampleRequests.Valid)!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    private static JsonObject Reordered(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var (key, value) in source.Reverse())
        {
            result[key] = value switch
            {
                JsonObject o => Reordered(o),
                JsonArray a => new JsonArray(a.Select(i => i is JsonObject io ? (JsonNode)Reordered(io) : i?.DeepClone()).ToArray()),
                _ => value?.DeepClone(),
            };
        }

        return result;
    }

    [Fact]
    public void U4_field_order_and_whitespace_do_not_change_hash()
    {
        var original = SampleRequests.Hash(SampleRequests.Valid);
        var reordered = Reordered(JsonNode.Parse(SampleRequests.Valid)!.AsObject()).ToJsonString();
        var compact = JsonNode.Parse(SampleRequests.Valid)!.ToJsonString();

        Assert.NotEqual(SampleRequests.Valid, reordered);
        Assert.Equal(original, SampleRequests.Hash(reordered));
        Assert.Equal(original, SampleRequests.Hash(compact));
    }

    [Fact]
    public void U4_value_change_changes_hash()
    {
        var original = SampleRequests.Hash(SampleRequests.Valid);
        var changed = Mutate(n => n["recipient"]!["address"]!["postalCode"] = "06421");
        Assert.NotEqual(original, SampleRequests.Hash(changed));
    }

    [Fact]
    public void Package_order_is_significant()
    {
        var original = SampleRequests.Hash(SampleRequests.Valid);
        var swapped = Mutate(n =>
        {
            var packages = n["packages"]!.AsArray();
            var first = packages[0]!.DeepClone();
            var second = packages[1]!.DeepClone();
            n["packages"] = new JsonArray(second, first);
        });
        Assert.NotEqual(original, SampleRequests.Hash(swapped));
    }

    [Theory]
    [InlineData("1850")]
    [InlineData("1850.0")]
    [InlineData("1850.000")]
    public void Equivalent_declared_value_representations_share_hash(string amountLiteral)
    {
        var json = SampleRequests.Valid.Replace("\"amount\": 1850.00", $"\"amount\": {amountLiteral}", StringComparison.Ordinal);
        Assert.NotEqual(SampleRequests.Valid, json);
        Assert.Equal(SampleRequests.Hash(SampleRequests.Valid), SampleRequests.Hash(json));
    }

    [Theory]
    [InlineData("1850.001")]
    [InlineData("-1")]
    [InlineData("10000000000.00")]
    public void Declared_value_out_of_scale_or_range_is_rejected(string amountLiteral)
    {
        var json = SampleRequests.Valid.Replace("\"amount\": 1850.00", $"\"amount\": {amountLiteral}", StringComparison.Ordinal);
        var (valid, errors) = CreateShipmentValidator.Validate(SampleRequests.Parse(json));
        Assert.Null(valid);
        Assert.Contains("declaredValue.amount", errors.Keys);
    }

    [Fact]
    public void High_precision_amount_is_not_rounded_silently()
    {
        // decimal okuması kayıpsızdır; 28 haneli değer double'a düşmez ve ölçek kuralıyla reddedilir.
        var json = SampleRequests.Valid.Replace("\"amount\": 1850.00", "\"amount\": 1850.0000000000000000000001", StringComparison.Ordinal);
        var (valid, errors) = CreateShipmentValidator.Validate(SampleRequests.Parse(json));
        Assert.Null(valid);
        Assert.Contains("declaredValue.amount", errors.Keys);
    }

    [Fact]
    public void Optional_absent_null_and_empty_are_equivalent_and_country_defaults_to_tr()
    {
        var original = SampleRequests.Hash(SampleRequests.Valid);
        var variant = Mutate(n =>
        {
            n["recipient"]!.AsObject().Remove("companyName");
            n["recipient"]!["email"] = "";
            n["recipient"]!["address"]!.AsObject().Remove("countryCode");
            n["sender"]!["address"]!.AsObject().Remove("line2");
        });
        Assert.Equal(original, SampleRequests.Hash(variant));
    }

    [Fact]
    public void Hash_includes_versioned_domain_and_endpoint()
    {
        Assert.Equal(1, CreateShipmentRequestHash.Version);
        Assert.Equal("POST /api/shipments", CreateShipmentRequestHash.Endpoint);
        Assert.Equal(32, SampleRequests.Hash(SampleRequests.Valid).Length);
    }

    [Theory]
    [InlineData("""{"serviceLevel":"Standard","serviceLevel":"Express"}""")]
    [InlineData("""{"serviceLevel":"Standard","unknownField":1}""")]
    [InlineData("""{"packages":[{"weightGrams":"2400"}]}""")]
    [InlineData("""{"packages":[{"weightGrams":2400.5}]}""")]
    [InlineData("""{"ServiceLevel":"Standard"}""")]
    public void Strict_json_rejects_duplicates_unknown_fields_and_loose_numbers(string json) =>
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<CreateShipmentRequest>(json, ShipmentsJson.StrictRequest));

    [Fact]
    public void Twenty_one_packages_fail_on_packages_field()
    {
        var json = Mutate(n =>
        {
            var package = n["packages"]![0]!;
            n["packages"] = new JsonArray(Enumerable.Range(0, 21).Select(_ => package.DeepClone()).ToArray());
        });
        var (valid, errors) = CreateShipmentValidator.Validate(SampleRequests.Parse(json));
        Assert.Null(valid);
        Assert.Contains("packages", errors.Keys);
    }

    [Theory]
    [InlineData("05321234567")]
    [InlineData("+0532123")]
    [InlineData("+90 532 123 45 67")]
    public void Invalid_phone_is_a_field_validation_error(string phone)
    {
        var json = Mutate(n => n["recipient"]!["phone"] = phone);
        var (valid, errors) = CreateShipmentValidator.Validate(SampleRequests.Parse(json));
        Assert.Null(valid);
        Assert.Contains("recipient.phone", errors.Keys);
    }

    [Fact]
    public void Turkish_postal_code_must_have_five_digits()
    {
        var json = Mutate(n => n["recipient"]!["address"]!["postalCode"] = "6420");
        var (_, errors) = CreateShipmentValidator.Validate(SampleRequests.Parse(json));
        Assert.Contains("recipient.address.postalCode", errors.Keys);
    }
}
