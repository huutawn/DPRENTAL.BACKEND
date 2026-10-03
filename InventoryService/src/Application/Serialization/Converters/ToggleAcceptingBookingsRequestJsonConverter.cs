using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Assets;

namespace Dprental.InventoryService.Application.Serialization.Converters;

/// <summary>
/// Custom JsonConverter cho ToggleAcceptingBookingsRequest đảm bảo acceptingBookings bắt buộc,
/// chỉ nhận boolean, từ chối unknown/duplicate fields và chấp nhận reason dạng chuỗi hoặc null.
/// </summary>
public sealed class ToggleAcceptingBookingsRequestJsonConverter : JsonConverter<ToggleAcceptingBookingsRequest>
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "acceptingBookings",
        "reason"
    };

    public override ToggleAcceptingBookingsRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Nội dung yêu cầu phải là một JSON object.");
        }

        bool hasFlag = false;
        bool acceptingBookings = false;
        string? reason = null;
        var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Cú pháp JSON không hợp lệ.");
            }

            var propName = reader.GetString() ?? string.Empty;
            if (!AllowedProperties.Contains(propName))
            {
                throw new JsonException($"Trường '{propName}' không hợp lệ hoặc không được hỗ trợ trong yêu cầu bật/tắt nhận lịch.");
            }

            if (!seenProperties.Add(propName))
            {
                throw new JsonException($"Trường '{propName}' bị lặp lại trong nội dung JSON.");
            }

            if (!reader.Read())
            {
                throw new JsonException("Nội dung JSON bị kết thúc bất thường.");
            }

            if (string.Equals(propName, "acceptingBookings", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                {
                    throw new JsonException("Trường 'acceptingBookings' phải là boolean (true hoặc false) và không được null.");
                }

                acceptingBookings = reader.GetBoolean();
                hasFlag = true;
            }
            else if (string.Equals(propName, "reason", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    reason = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    reason = reader.GetString();
                }
                else
                {
                    throw new JsonException("Trường 'reason' phải là chuỗi ký tự hoặc null.");
                }
            }
        }

        if (!hasFlag)
        {
            throw new JsonException("Trường 'acceptingBookings' là bắt buộc.");
        }

        return new ToggleAcceptingBookingsRequest(acceptingBookings, reason);
    }

    public override void Write(Utf8JsonWriter writer, ToggleAcceptingBookingsRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("acceptingBookings", value.AcceptingBookings);
        if (value.Reason is not null)
        {
            writer.WriteString("reason", value.Reason);
        }
        else
        {
            writer.WriteNull("reason");
        }
        writer.WriteEndObject();
    }
}
