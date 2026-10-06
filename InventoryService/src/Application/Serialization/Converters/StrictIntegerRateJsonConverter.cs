using System;
using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Dprental.InventoryService.Application.Serialization.Converters;

/// <summary>
/// Custom JsonConverter cho các trường giá thuê (decimal?):
/// Xác thực tính nguyên chính xác từ raw numeric token trước khi parse sang decimal.
/// Chặn làm tròn của decimal parser đối với các số có phần lẻ dài (ví dụ: 0.999... hoặc 1000.000...01).
/// Hỗ trợ các biểu diễn nguyên hợp lệ như 1000.0 và 1e3, và giữ nguyên semantics null/missing.
/// Giới hạn exponent để ngăn chặn tấn công DoS / memory exhaustion.
/// </summary>
public sealed class StrictIntegerRateJsonConverter : JsonConverter<decimal?>
{
    private const int MaxExponentDigits = 4;
    private const int MinExponent = -30;
    private const int MaxExponent = 30;

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("Giá trị giá thuê phải là số.");
        }

        ReadOnlySpan<byte> span = reader.HasValueSequence
            ? reader.ValueSequence.ToArray()
            : reader.ValueSpan;

        if (!TryParseExactIntegerDecimal(span, out decimal value))
        {
            throw new JsonException("Giá trị giá thuê phải là số nguyên, không được chứa phần lẻ hoặc vượt quá giới hạn.");
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

    public static bool TryParseExactIntegerDecimal(ReadOnlySpan<byte> rawSpan, out decimal value)
    {
        value = 0m;
        var span = rawSpan;
        while (!span.IsEmpty && (span[0] == (byte)' ' || span[0] == (byte)'\t' || span[0] == (byte)'\r' || span[0] == (byte)'\n'))
        {
            span = span[1..];
        }
        while (!span.IsEmpty && (span[^1] == (byte)' ' || span[^1] == (byte)'\t' || span[^1] == (byte)'\r' || span[^1] == (byte)'\n'))
        {
            span = span[..^1];
        }
        if (span.IsEmpty)
        {
            return false;
        }

        if (span[0] == (byte)'+' || span[0] == (byte)'-')
        {
            span = span[1..];
        }

        if (span.IsEmpty)
        {
            return false;
        }

        // 1. Phân tách mantissa và exponent
        ReadOnlySpan<byte> mantissaSpan;
        int exponent = 0;
        int expIndex = -1;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == (byte)'e' || span[i] == (byte)'E')
            {
                expIndex = i;
                break;
            }
        }

        if (expIndex >= 0)
        {
            mantissaSpan = span[..expIndex];
            var expSpan = span[(expIndex + 1)..];
            if (expSpan.IsEmpty)
            {
                return false;
            }

            bool expNegative = false;
            if (expSpan[0] == (byte)'+')
            {
                expSpan = expSpan[1..];
            }
            else if (expSpan[0] == (byte)'-')
            {
                expNegative = true;
                expSpan = expSpan[1..];
            }

            if (expSpan.IsEmpty || expSpan.Length > MaxExponentDigits)
            {
                return false;
            }

            int parsedExp = 0;
            foreach (var b in expSpan)
            {
                if (b < (byte)'0' || b > (byte)'9')
                {
                    return false;
                }
                parsedExp = parsedExp * 10 + (b - (byte)'0');
            }

            exponent = expNegative ? -parsedExp : parsedExp;
            if (exponent < MinExponent || exponent > MaxExponent)
            {
                return false;
            }
        }
        else
        {
            mantissaSpan = span;
        }

        // 2. Phân tách phần nguyên và phần thập phân của mantissa
        int dotIndex = -1;
        for (int i = 0; i < mantissaSpan.Length; i++)
        {
            if (mantissaSpan[i] == (byte)'.')
            {
                dotIndex = i;
                break;
            }
        }

        ReadOnlySpan<byte> intPart;
        ReadOnlySpan<byte> fracPart;
        if (dotIndex >= 0)
        {
            intPart = mantissaSpan[..dotIndex];
            fracPart = mantissaSpan[(dotIndex + 1)..];
        }
        else
        {
            intPart = mantissaSpan;
            fracPart = ReadOnlySpan<byte>.Empty;
        }

        foreach (var b in intPart)
        {
            if (b < (byte)'0' || b > (byte)'9')
            {
                return false;
            }
        }

        foreach (var b in fracPart)
        {
            if (b < (byte)'0' || b > (byte)'9')
            {
                return false;
            }
        }

        if (intPart.IsEmpty && fracPart.IsEmpty)
        {
            return false;
        }

        // 3. Kiểm tra tính nguyên chính xác (Integrality check)
        int lf = fracPart.Length;
        int li = intPart.Length;

        if (exponent == 0)
        {
            foreach (var b in fracPart)
            {
                if (b != (byte)'0')
                {
                    return false;
                }
            }
        }
        else if (exponent > 0)
        {
            if (lf > exponent)
            {
                var remainingFrac = fracPart[exponent..];
                foreach (var b in remainingFrac)
                {
                    if (b != (byte)'0')
                    {
                        return false;
                    }
                }
            }
        }
        else // exponent < 0
        {
            int absExp = -exponent;
            foreach (var b in fracPart)
            {
                if (b != (byte)'0')
                {
                    return false;
                }
            }

            if (absExp < li)
            {
                var shiftedFrac = intPart[(li - absExp)..];
                foreach (var b in shiftedFrac)
                {
                    if (b != (byte)'0')
                    {
                        return false;
                    }
                }
            }
            else
            {
                foreach (var b in intPart)
                {
                    if (b != (byte)'0')
                    {
                        return false;
                    }
                }
            }
        }

        // 4. Parse sang decimal an toàn và chuẩn hóa scale về 0
        if (!decimal.TryParse(rawSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed))
        {
            return false;
        }

        if (decimal.Truncate(parsed) != parsed)
        {
            return false;
        }

        value = decimal.Truncate(parsed);
        return true;
    }
}
