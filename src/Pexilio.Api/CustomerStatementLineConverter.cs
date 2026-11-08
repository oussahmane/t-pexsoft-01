using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pexilio.Application;

namespace Pexilio.Api;

/// <summary>
/// Sale statement lines carry the credit-override snapshot. Payment and return lines omit those fields.
/// </summary>
public sealed class CustomerStatementLineConverter : JsonConverter<CustomerStatementLineResponse>
{
    private static readonly string[] CreditProperties =
    [
        nameof(CustomerStatementLineResponse.CreditLimitOverridden),
        nameof(CustomerStatementLineResponse.CreditOverrideReason),
        nameof(CustomerStatementLineResponse.CreditLimitSnapshot),
        nameof(CustomerStatementLineResponse.CreditOutstandingBefore),
        nameof(CustomerStatementLineResponse.CreditProjectedOutstanding),
    ];

    public override CustomerStatementLineResponse Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var bare = WithoutThisConverter(options);
        var line = JsonSerializer.Deserialize<CustomerStatementLineResponse>(ref reader, bare);
        return line ?? throw new JsonException("A statement line is required.");
    }

    public override void Write(Utf8JsonWriter writer, CustomerStatementLineResponse value, JsonSerializerOptions options)
    {
        var bare = WithoutThisConverter(options);
        var node = JsonSerializer.SerializeToNode(value, bare)!.AsObject();
        if (!string.Equals(value.Kind, "sale", StringComparison.Ordinal))
        {
            foreach (var property in CreditProperties)
            {
                node.Remove(bare.PropertyNamingPolicy?.ConvertName(property) ?? property);
            }
        }

        node.WriteTo(writer);
    }

    private static JsonSerializerOptions WithoutThisConverter(JsonSerializerOptions options)
    {
        var bare = new JsonSerializerOptions(options);
        for (var index = bare.Converters.Count - 1; index >= 0; index--)
        {
            if (bare.Converters[index] is CustomerStatementLineConverter)
            {
                bare.Converters.RemoveAt(index);
            }
        }

        return bare;
    }
}
