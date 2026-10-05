using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Assets;

namespace Dprental.InventoryService.Application.Serialization.Converters;

/// <summary>
/// Custom JsonConverter cho PatchInventoryAssetRequest thực thi presence tracking,
/// từ chối các trường bị cấm/không rõ nguồn gốc và xác thực kiểu dữ liệu nghiêm ngặt.
/// </summary>
public sealed class PatchInventoryAssetRequestJsonConverter : JsonConverter<PatchInventoryAssetRequest>
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "brandId",
        "model",
        "serialNumber",
        "manufactureYear",
        "thumbnailMediaId",
        "schemaVersion",
        "specValues"
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
                throw new JsonException("Cú pháp JSON không hợp lệ.");
            }

            var propName = reader.GetString() ?? string.Empty;
            if (!AllowedProperties.Contains(propName))
            {
                throw new JsonException($"Trường '{propName}' không hợp lệ hoặc bị cấm trong cập nhật thông tin tài sản.");
            }

            if (!seenProperties.Add(propName))
            {
                throw new JsonException($"Trường '{propName}' bị lặp lại trong nội dung JSON.");
            }

            if (!reader.Read())
            {
                throw new JsonException("Nội dung JSON bị kết thúc bất thường.");
            }

            if (string.Equals(propName, "brandId", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    throw new JsonException("Trường 'brandId' không được null.");
                }

                if (reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out var brandId))
                {
                    request.IsBrandIdSpecified = true;
                    request.BrandId = brandId;
                }
                else
                {
                    throw new JsonException("Trường 'brandId' phải là GUID hợp lệ.");
                }
            }
            else if (string.Equals(propName, "model", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    throw new JsonException("Trường 'model' không được null.");
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException("Trường 'model' phải là chuỗi ký tự.");
                }

                request.IsModelSpecified = true;
                request.Model = reader.GetString();
            }
            else if (string.Equals(propName, "serialNumber", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    request.IsSerialNumberSpecified = true;
                    request.SerialNumber = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    request.IsSerialNumberSpecified = true;
                    request.SerialNumber = reader.GetString();
                }
                else
                {
                    throw new JsonException("Trường 'serialNumber' phải là chuỗi ký tự hoặc null.");
                }
            }
            else if (string.Equals(propName, "manufactureYear", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    request.IsManufactureYearSpecified = true;
                    request.ManufactureYear = null;
                }
                else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var year))
                {
                    request.IsManufactureYearSpecified = true;
                    request.ManufactureYear = year;
                }
                else
                {
                    throw new JsonException("Trường 'manufactureYear' phải là số nguyên hoặc null.");
                }
            }
            else if (string.Equals(propName, "thumbnailMediaId", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    request.IsThumbnailMediaIdSpecified = true;
                    request.ThumbnailMediaId = null;
                }
                else if (reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out var thumbId))
                {
                    request.IsThumbnailMediaIdSpecified = true;
                    request.ThumbnailMediaId = thumbId;
                }
                else
                {
                    throw new JsonException("Trường 'thumbnailMediaId' phải là GUID hợp lệ hoặc null.");
                }
            }
            else if (string.Equals(propName, "schemaVersion", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    throw new JsonException("Trường 'schemaVersion' không được null.");
                }

                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var ver))
                {
                    request.IsSchemaVersionSpecified = true;
                    request.SchemaVersion = ver;
                }
                else
                {
                    throw new JsonException("Trường 'schemaVersion' phải là số nguyên.");
                }
            }
            else if (string.Equals(propName, "specValues", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    throw new JsonException("Trường 'specValues' không được null.");
                }

                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new JsonException("Trường 'specValues' phải là JSON object.");
                }

                using var doc = JsonDocument.ParseValue(ref reader);
                request.IsSpecValuesSpecified = true;
                request.SpecValues = doc.RootElement.Clone();
            }
        }

        return request;
    }

    public override void Write(Utf8JsonWriter writer, PatchInventoryAssetRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.IsBrandIdSpecified)
        {
            if (value.BrandId.HasValue)
            {
                writer.WriteString("brandId", value.BrandId.Value);
            }
            else
            {
                writer.WriteNull("brandId");
            }
        }

        if (value.IsModelSpecified)
        {
            writer.WriteString("model", value.Model);
        }

        if (value.IsSerialNumberSpecified)
        {
            if (value.SerialNumber is not null)
            {
                writer.WriteString("serialNumber", value.SerialNumber);
            }
            else
            {
                writer.WriteNull("serialNumber");
            }
        }

        if (value.IsManufactureYearSpecified)
        {
            if (value.ManufactureYear.HasValue)
            {
                writer.WriteNumber("manufactureYear", value.ManufactureYear.Value);
            }
            else
            {
                writer.WriteNull("manufactureYear");
            }
        }

        if (value.IsThumbnailMediaIdSpecified)
        {
            if (value.ThumbnailMediaId.HasValue)
            {
                writer.WriteString("thumbnailMediaId", value.ThumbnailMediaId.Value);
            }
            else
            {
                writer.WriteNull("thumbnailMediaId");
            }
        }

        if (value.IsSchemaVersionSpecified)
        {
            if (value.SchemaVersion.HasValue)
            {
                writer.WriteNumber("schemaVersion", value.SchemaVersion.Value);
            }
            else
            {
                writer.WriteNull("schemaVersion");
            }
        }

        if (value.IsSpecValuesSpecified)
        {
            if (value.SpecValues.HasValue)
            {
                writer.WritePropertyName("specValues");
                value.SpecValues.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteNull("specValues");
            }
        }

        writer.WriteEndObject();
    }
}
