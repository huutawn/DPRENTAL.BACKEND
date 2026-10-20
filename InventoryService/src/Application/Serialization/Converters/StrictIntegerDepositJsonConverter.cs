using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.Serialization.Converters;

/// <summary>
/// Custom JsonConverter cho trường depositAmount (decimal?):
/// Xác thực tính nguyên chính xác từ raw numeric token trước khi parse sang decimal.
/// Chặn làm tròn của decimal parser đối với các số có phần lẻ dài.
/// Từ chối null, số có phần lẻ, không phải số hoặc vượt giới hạn.
/// </summary>
public sealed class StrictIntegerDepositJsonConverter : JsonConverter<decimal?>
{
    public override bool HandleNull => true;

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            throw new JsonException("INVALID_DEPOSIT_AMOUNT: Tiền cọc không được là null.");
        }

        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("INVALID_DEPOSIT_AMOUNT: Giá trị tiền cọc phải là số.");
        }

        ReadOnlySpan<byte> span = reader.HasValueSequence
            ? reader.ValueSequence.ToArray()
            : reader.ValueSpan;

        if (!StrictIntegerRateJsonConverter.TryParseExactIntegerDecimal(span, out decimal value))
        {
            throw new JsonException("INVALID_DEPOSIT_AMOUNT: Giá trị tiền cọc phải là số nguyên, không được chứa phần lẻ hoặc vượt quá giới hạn.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
