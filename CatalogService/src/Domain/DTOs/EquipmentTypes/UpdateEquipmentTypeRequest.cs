using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

[JsonConverter(typeof(UpdateEquipmentTypeRequestJsonConverter))]
public sealed class UpdateEquipmentTypeRequest
{
    [JsonIgnore]
    public bool IsCodeSpecified { get; init; }
    public string? Code { get; init; }

    [JsonIgnore]
    public bool IsNameSpecified { get; init; }
    public string? Name { get; init; }

    [JsonIgnore]
    public bool IsDescriptionSpecified { get; init; }
    public string? Description { get; init; }

    [JsonIgnore]
    public bool IsCategoryIdSpecified { get; init; }
    public Guid? CategoryId { get; init; }

    [JsonIgnore]
    public bool IsMediaSpecified { get; init; }
    public IReadOnlyList<EquipmentTypeMediaItemDto?>? Media { get; init; }

    [JsonIgnore]
    public bool HasAnyFieldSpecified =>
        IsCodeSpecified || IsNameSpecified || IsDescriptionSpecified || IsCategoryIdSpecified || IsMediaSpecified;
}

public sealed class UpdateEquipmentTypeRequestJsonConverter : JsonConverter<UpdateEquipmentTypeRequest>
{
    public override UpdateEquipmentTypeRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object.");
        }

        bool isCodeSpecified = false;
        string? code = null;
        bool isNameSpecified = false;
        string? name = null;
        bool isDescriptionSpecified = false;
        string? description = null;
        bool isCategoryIdSpecified = false;
        Guid? categoryId = null;
        bool isMediaSpecified = false;
        IReadOnlyList<EquipmentTypeMediaItemDto?>? media = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new UpdateEquipmentTypeRequest
                {
                    IsCodeSpecified = isCodeSpecified,
                    Code = code,
                    IsNameSpecified = isNameSpecified,
                    Name = name,
                    IsDescriptionSpecified = isDescriptionSpecified,
                    Description = description,
                    IsCategoryIdSpecified = isCategoryIdSpecified,
                    CategoryId = categoryId,
                    IsMediaSpecified = isMediaSpecified,
                    Media = media
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name.");
            }

            string propName = reader.GetString() ?? string.Empty;
            reader.Read();

            if (string.Equals(propName, "code", StringComparison.OrdinalIgnoreCase))
            {
                isCodeSpecified = true;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    code = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    code = reader.GetString();
                }
                else
                {
                    throw new JsonException("Code must be a string.");
                }
            }
            else if (string.Equals(propName, "name", StringComparison.OrdinalIgnoreCase))
            {
                isNameSpecified = true;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    name = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    name = reader.GetString();
                }
                else
                {
                    throw new JsonException("Name must be a string.");
                }
            }
            else if (string.Equals(propName, "description", StringComparison.OrdinalIgnoreCase))
            {
                isDescriptionSpecified = true;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    description = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    description = reader.GetString();
                }
                else
                {
                    throw new JsonException("Description must be a string or null.");
                }
            }
            else if (string.Equals(propName, "categoryId", StringComparison.OrdinalIgnoreCase))
            {
                isCategoryIdSpecified = true;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    categoryId = null;
                }
                else if (reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var g))
                {
                    categoryId = g;
                }
                else
                {
                    throw new JsonException("CategoryId must be a valid GUID.");
                }
            }
            else if (string.Equals(propName, "media", StringComparison.OrdinalIgnoreCase))
            {
                isMediaSpecified = true;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    media = null;
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    media = JsonSerializer.Deserialize<IReadOnlyList<EquipmentTypeMediaItemDto?>>(ref reader, options);
                }
                else
                {
                    throw new JsonException("Media must be an array or null.");
                }
            }
            else
            {
                throw new JsonException($"Field '{propName}' is not allowed in request contract.");
            }
        }

        throw new JsonException("Unexpected end of JSON input.");
    }

    public override void Write(Utf8JsonWriter writer, UpdateEquipmentTypeRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.IsCodeSpecified)
        {
            if (value.Code is null) writer.WriteNull("code"); else writer.WriteString("code", value.Code);
        }
        if (value.IsNameSpecified)
        {
            if (value.Name is null) writer.WriteNull("name"); else writer.WriteString("name", value.Name);
        }
        if (value.IsDescriptionSpecified)
        {
            if (value.Description is null) writer.WriteNull("description"); else writer.WriteString("description", value.Description);
        }
        if (value.IsCategoryIdSpecified)
        {
            if (value.CategoryId is null) writer.WriteNull("categoryId"); else writer.WriteString("categoryId", value.CategoryId.Value);
        }
        if (value.IsMediaSpecified)
        {
            writer.WritePropertyName("media");
            JsonSerializer.Serialize(writer, value.Media, options);
        }
        writer.WriteEndObject();
    }
}
