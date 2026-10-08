using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Assets;

namespace Dprental.InventoryService.Application.Serialization.Converters;

/// <summary>
/// Custom JsonConverter cho PatchInventoryAssetRequest thực thi presence tracking cho physical assets,
/// chỉ cho phép serialNumber và manufactureYear, từ chối các trường khác và xác thực kiểu nghiêm ngặt.
/// </summary>
public sealed class PatchInventoryAssetRequestJsonConverter : JsonConverter<PatchInventoryAssetRequest>
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "serialNumber",
        "manufactureYear"
    };

    public override PatchInventoryAssetRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Nội dung yêu cầu PATCH phải là một JSON object.");
        }

        var request = new PatchInventoryAssetRequest();
        var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return request;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Mong đợi tên thuộc tính JSON.");
            }

            string propertyName = reader.GetString()!;

            if (!seenProperties.Add(propertyName))
            {
                throw new JsonException($"Thuộc tính '{propertyName}' bị lặp lại trong nội dung yêu cầu.");
            }

            if (!AllowedProperties.Contains(propertyName))
            {
                throw new JsonException($"Thuộc tính '{propertyName}' không được phép cập nhật trên đơn vị thiết bị vật lý.");
            }

            reader.Read();

            switch (propertyName.ToLowerInvariant())
            {
                case "serialnumber":
                    request.IsSerialNumberSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.SerialNumber = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        request.SerialNumber = reader.GetString();
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'serialNumber' phải là kiểu chuỗi hoặc null.");
                    }
                    break;

                case "manufactureyear":
                    request.IsManufactureYearSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.ManufactureYear = null;
                    }
                    else if (reader.TokenType == JsonTokenType.Number)
                    {
                        if (reader.TryGetInt32(out int year))
                        {
                            request.ManufactureYear = year;
                        }
                        else
                        {
                            throw new JsonException("Thuộc tính 'manufactureYear' phải là số nguyên hợp lệ.");
                        }
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'manufactureYear' phải là số nguyên hoặc null.");
                    }
                    break;
            }
        }

        return request;
    }

    public override void Write(Utf8JsonWriter writer, PatchInventoryAssetRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.IsSerialNumberSpecified)
        {
            if (value.SerialNumber == null)
            {
                writer.WriteNull("serialNumber");
            }
            else
            {
                writer.WriteString("serialNumber", value.SerialNumber);
            }
        }

        if (value.IsManufactureYearSpecified)
        {
            if (value.ManufactureYear == null)
            {
                writer.WriteNull("manufactureYear");
            }
            else
            {
                writer.WriteNumber("manufactureYear", value.ManufactureYear.Value);
            }
        }

        writer.WriteEndObject();
    }
}
