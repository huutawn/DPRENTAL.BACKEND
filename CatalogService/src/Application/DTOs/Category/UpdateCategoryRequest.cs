using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatalogService.Application.DTOs.Category;

[JsonConverter(typeof(UpdateCategoryRequestJsonConverter))]
public sealed class UpdateCategoryRequest
{
    [JsonIgnore]
    public bool IsNameSpecified { get; init; }
    public string? Name { get; init; }

    [JsonIgnore]
    public bool IsDescriptionSpecified { get; init; }
    public string? Description { get; init; }

    [JsonIgnore]
    public bool IsActiveSpecified { get; init; }
    public bool? IsActive { get; init; }

    [JsonIgnore]
    public bool IsImageMediaIdSpecified { get; init; }
    public Guid? ImageMediaId { get; init; }

    public UpdateCategoryRequest()
    {
    }

    public UpdateCategoryRequest(
        string? name,
        string? description,
        bool? isActive,
        Guid? imageMediaId = null,
        bool isImageMediaIdSpecified = false)
    {
        Name = name;
        IsNameSpecified = name != null;
        Description = description;
        IsDescriptionSpecified = description != null;
        IsActive = isActive;
        IsActiveSpecified = isActive.HasValue;
        ImageMediaId = imageMediaId;
        IsImageMediaIdSpecified = isImageMediaIdSpecified || imageMediaId.HasValue;
    }
}

public sealed class UpdateCategoryRequestJsonConverter : JsonConverter<UpdateCategoryRequest>
{
    public override UpdateCategoryRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object.");
        }

        bool isNameSpecified = false;
        string? name = null;
        bool isDescriptionSpecified = false;
        string? description = null;
        bool isActiveSpecified = false;
        bool? isActive = null;
        bool isImageMediaIdSpecified = false;
        Guid? imageMediaId = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new UpdateCategoryRequest
                {
                    IsNameSpecified = isNameSpecified,
                    Name = name,
                    IsDescriptionSpecified = isDescriptionSpecified,
                    Description = description,
                    IsActiveSpecified = isActiveSpecified,
                    IsActive = isActive,
                    IsImageMediaIdSpecified = isImageMediaIdSpecified,
                    ImageMediaId = imageMediaId
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name.");
            }

            string propName = reader.GetString() ?? string.Empty;
            reader.Read();

            if (string.Equals(propName, "name", StringComparison.OrdinalIgnoreCase))
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
            else if (string.Equals(propName, "isActive", StringComparison.OrdinalIgnoreCase))
            {
                isActiveSpecified = true;
                if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                {
                    isActive = reader.GetBoolean();
                }
                else
                {
                    throw new JsonException("IsActive must be a boolean.");
                }
            }
            else if (string.Equals(propName, "imageMediaId", StringComparison.OrdinalIgnoreCase))
            {
                isImageMediaIdSpecified = true;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    imageMediaId = null;
                }
                else if (reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var g))
                {
                    imageMediaId = g;
                }
                else
                {
                    throw new JsonException("ImageMediaId must be a valid GUID or null.");
                }
            }
            else
            {
                throw new JsonException($"Field '{propName}' is not allowed in request contract.");
            }
        }

        throw new JsonException("Unexpected end of JSON input.");
    }

    public override void Write(Utf8JsonWriter writer, UpdateCategoryRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.IsNameSpecified)
        {
            if (value.Name is null) writer.WriteNull("name"); else writer.WriteString("name", value.Name);
        }
        if (value.IsDescriptionSpecified)
        {
            if (value.Description is null) writer.WriteNull("description"); else writer.WriteString("description", value.Description);
        }
        if (value.IsActiveSpecified)
        {
            if (value.IsActive.HasValue) writer.WriteBoolean("isActive", value.IsActive.Value);
        }
        if (value.IsImageMediaIdSpecified)
        {
            if (value.ImageMediaId is null) writer.WriteNull("imageMediaId"); else writer.WriteString("imageMediaId", value.ImageMediaId.Value);
        }
        writer.WriteEndObject();
    }
}
