using System.Text.Json;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Validation;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class SpecificationValuesValidatorTests
{
    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Required_MissingField_ReturnsRequiredError()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "model", Label = "Model", DataType = "text", Required = true }
            ]
        };

        var values = ParseJson("{}");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Single(result.Errors);
        Assert.Equal("model", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeRequired, result.Errors[0].Code);
        Assert.Equal(SpecificationValuesValidator.MessageRequired, result.Errors[0].Message);
    }

    [Fact]
    public void Required_NullValue_ReturnsRequiredError()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "model", Label = "Model", DataType = "text", Required = true }
            ]
        };

        var values = ParseJson("""{ "model": null }""");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Single(result.Errors);
        Assert.Equal("model", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeRequired, result.Errors[0].Code);
        Assert.Equal(SpecificationValuesValidator.MessageRequired, result.Errors[0].Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\\t\\n")]
    public void Required_WhitespaceString_ReturnsRequiredError(string blank)
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "model", Label = "Model", DataType = "text", Required = true }
            ]
        };

        var values = ParseJson($"{{\"model\": \"{blank}\"}}");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Single(result.Errors);
        Assert.Equal("model", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeRequired, result.Errors[0].Code);
    }
    [Fact]
    public void Required_FalseAndZero_TreatedAsProvided()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "hasWifi", Label = "Wi-Fi", DataType = "boolean", Required = true },
                new EquipmentTypeSpecField { Key = "weight", Label = "Khối lượng", DataType = "number", Required = true, Min = 0, Max = 100 }
            ]
        };

        var values = ParseJson("""
            {
                "hasWifi": false,
                "weight": 0
            }
            """);
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Optional_OmittedKey_IsValid()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "notes", Label = "Ghi chú", DataType = "text", Required = false }
            ]
        };

        var values = ParseJson("{}");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Optional_SentNull_ReturnsTypeMismatchError()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "notes", Label = "Ghi chú", DataType = "text", Required = false }
            ]
        };

        var values = ParseJson("""{ "notes": null }""");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Single(result.Errors);
        Assert.Equal("notes", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeTypeMismatch, result.Errors[0].Code);
        Assert.Equal(SpecificationValuesValidator.MessageTypeMismatch, result.Errors[0].Message);
    }

    [Fact]
    public void AllDataTypes_ValidPayloads_Succeed()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "fText", Label = "Text", DataType = "text", Required = true },
                new EquipmentTypeSpecField { Key = "fNum", Label = "Num", DataType = "number", Required = true, Min = 1, Max = 10 },
                new EquipmentTypeSpecField { Key = "fBool", Label = "Bool", DataType = "boolean", Required = true },
                new EquipmentTypeSpecField
                {
                    Key = "fEnum",
                    Label = "Enum",
                    DataType = "enum",
                    Required = true,
                    Options =
                    [
                        new EquipmentTypeSpecOption { Value = "OPT_A", Label = "Option A" },
                        new EquipmentTypeSpecOption { Value = "OPT_B", Label = "Option B" }
                    ]
                }
            ]
        };

        var values = ParseJson("""
            {
                "fText": "Xin chao",
                "fNum": 5.5,
                "fBool": true,
                "fEnum": "OPT_A"
            }
            """);
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void NumericString_NoCoercion_ReturnsTypeMismatch()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "iso", Label = "ISO", DataType = "number", Required = true, Min = 100, Max = 25600 }
            ]
        };

        var values = ParseJson("""{ "iso": "400" }""");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Single(result.Errors);
        Assert.Equal("iso", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeTypeMismatch, result.Errors[0].Code);
    }

    [Theory]
    [InlineData("text", "123")]
    [InlineData("boolean", "\"true\"")]
    [InlineData("enum", "123")]
    public void DataTypeMismatch_ReturnsTypeMismatch(string dataType, string invalidValue)
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField
                {
                    Key = "testField",
                    Label = "Test",
                    DataType = dataType,
                    Required = true,
                    Options = dataType == "enum" ? [new EquipmentTypeSpecOption { Value = "V1", Label = "L1" }] : null
                }
            ]
        };

        var values = ParseJson($$"""{ "testField": {{invalidValue}} }""");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Single(result.Errors);
        Assert.Equal("testField", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeTypeMismatch, result.Errors[0].Code);
    }

    [Fact]
    public void NumberBounds_InclusiveBoundaries_Succeed()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "weight", Label = "Weight", DataType = "number", Required = true, Min = 10, Max = 50 }
            ]
        };

        var minValues = ParseJson("""{ "weight": 10 }""");
        var maxValues = ParseJson("""{ "weight": 50 }""");

        Assert.True(SpecificationValuesValidator.Validate(definition, minValues).Valid);
        Assert.True(SpecificationValuesValidator.Validate(definition, maxValues).Valid);
    }

    [Theory]
    [InlineData(9.99)]
    [InlineData(50.01)]
    [InlineData(-10)]
    [InlineData(100)]
    public void NumberBounds_OutOfRange_ReturnsOutOfRange(decimal outOfRangeValue)
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "weight", Label = "Weight", DataType = "number", Required = true, Min = 10, Max = 50 }
            ]
        };

        var values = ParseJson($$"""{ "weight": {{outOfRangeValue}} }""");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Single(result.Errors);
        Assert.Equal("weight", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeOutOfRange, result.Errors[0].Code);
        Assert.Equal(SpecificationValuesValidator.MessageOutOfRange, result.Errors[0].Message);
    }

    [Fact]
    public void DecimalOverflow_ReturnsOutOfRange()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "distance", Label = "Distance", DataType = "number", Required = true }
            ]
        };

        // 1e100 exceeds System.Decimal.MaxValue (~7.9e28)
        var values = ParseJson("""{ "distance": 1e100 }""");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Single(result.Errors);
        Assert.Equal("distance", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeOutOfRange, result.Errors[0].Code);
    }

    [Fact]
    public void Enum_MatchesValueExactOrdinal_NotLabel()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField
                {
                    Key = "mount",
                    Label = "Mount",
                    DataType = "enum",
                    Required = true,
                    Options =
                    [
                        new EquipmentTypeSpecOption { Value = "e_mount", Label = "Sony E-Mount" }
                    ]
                }
            ]
        };

        // Matching value succeeds
        var validResult = SpecificationValuesValidator.Validate(definition, ParseJson("""{ "mount": "e_mount" }"""));
        Assert.True(validResult.Valid);

        // Matching label fails
        var labelResult = SpecificationValuesValidator.Validate(definition, ParseJson("""{ "mount": "Sony E-Mount" }"""));
        Assert.False(labelResult.Valid);
        Assert.Equal(SpecificationValuesValidator.CodeInvalidEnumOption, labelResult.Errors[0].Code);
        Assert.Equal(SpecificationValuesValidator.MessageInvalidEnumOption, labelResult.Errors[0].Message);

        // Case mismatch fails
        var caseResult = SpecificationValuesValidator.Validate(definition, ParseJson("""{ "mount": "E_MOUNT" }"""));
        Assert.False(caseResult.Valid);
        Assert.Equal(SpecificationValuesValidator.CodeInvalidEnumOption, caseResult.Errors[0].Code);
    }

    [Fact]
    public void Key_CaseSensitivity_AndUnknownFields()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "resolution", Label = "Resolution", DataType = "text", Required = true }
            ]
        };

        var values = ParseJson("""{ "Resolution": "4K" }""");
        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Equal(2, result.Errors.Count);

        // 1. Schema field resolution was not provided -> REQUIRED
        Assert.Equal("resolution", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeRequired, result.Errors[0].Code);

        // 2. Resolution is not in schema -> UNKNOWN_FIELD
        Assert.Equal("Resolution", result.Errors[1].Field);
        Assert.Equal(SpecificationValuesValidator.CodeUnknownField, result.Errors[1].Code);
        Assert.Equal(SpecificationValuesValidator.MessageUnknownField, result.Errors[1].Message);
    }

    [Fact]
    public void MultipleErrors_MaxOnePerField_SchemaOrderThenUnknownSorted()
    {
        var definition = new EquipmentTypeSpecDefinition
        {
            Fields =
            [
                new EquipmentTypeSpecField { Key = "f1", Label = "F1", DataType = "text", Required = true },
                new EquipmentTypeSpecField { Key = "f2", Label = "F2", DataType = "number", Required = true, Min = 0, Max = 10 },
                new EquipmentTypeSpecField { Key = "f3", Label = "F3", DataType = "text", Required = false }
            ]
        };

        var values = ParseJson("""
            {
                "f2": 999,
                "f3": null,
                "zebra": 1,
                "apple": 2
            }
            """);

        var result = SpecificationValuesValidator.Validate(definition, values);

        Assert.False(result.Valid);
        Assert.Equal(5, result.Errors.Count);

        // Errors ordered: schema fields (f1, f2, f3), then unknown fields sorted by key (apple, zebra)
        Assert.Equal("f1", result.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeRequired, result.Errors[0].Code);

        Assert.Equal("f2", result.Errors[1].Field);
        Assert.Equal(SpecificationValuesValidator.CodeOutOfRange, result.Errors[1].Code);

        Assert.Equal("f3", result.Errors[2].Field);
        Assert.Equal(SpecificationValuesValidator.CodeTypeMismatch, result.Errors[2].Code);

        Assert.Equal("apple", result.Errors[3].Field);
        Assert.Equal(SpecificationValuesValidator.CodeUnknownField, result.Errors[3].Code);

        Assert.Equal("zebra", result.Errors[4].Field);
        Assert.Equal(SpecificationValuesValidator.CodeUnknownField, result.Errors[4].Code);
    }

    [Fact]
    public void EmptySchema_AcceptsEmptyObject_AndRejectsAnyFields()
    {
        var emptyDefinition = new EquipmentTypeSpecDefinition
        {
            Fields = []
        };

        var validResult = SpecificationValuesValidator.Validate(emptyDefinition, ParseJson("{}"));
        Assert.True(validResult.Valid);
        Assert.Empty(validResult.Errors);

        var rejectedResult = SpecificationValuesValidator.Validate(emptyDefinition, ParseJson("""{ "foo": 1, "bar": 2 }"""));
        Assert.False(rejectedResult.Valid);
        Assert.Equal(2, rejectedResult.Errors.Count);
        Assert.Equal("bar", rejectedResult.Errors[0].Field);
        Assert.Equal(SpecificationValuesValidator.CodeUnknownField, rejectedResult.Errors[0].Code);
        Assert.Equal("foo", rejectedResult.Errors[1].Field);
        Assert.Equal(SpecificationValuesValidator.CodeUnknownField, rejectedResult.Errors[1].Code);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1 }""")] // missing specValues
    [InlineData("""{ "specValues": {} }""")] // missing schemaVersion
    [InlineData("""{ "schemaVersion": 0, "specValues": {} }""")] // non-positive version
    [InlineData("""{ "schemaVersion": -5, "specValues": {} }""")] // negative version
    [InlineData("""{ "schemaVersion": 1.5, "specValues": {} }""")] // floating point version
    [InlineData("""{ "schemaVersion": "1", "specValues": {} }""")] // string version
    [InlineData("""{ "schemaVersion": 1, "specValues": null }""")] // null specValues
    [InlineData("""{ "schemaVersion": 1, "specValues": [] }""")] // array specValues
    [InlineData("""{ "schemaVersion": 1, "specValues": "str" }""")] // string specValues
    [InlineData("""{ "schemaVersion": 1, "specValues": {}, "extra": 123 }""")] // unknown envelope property
    [InlineData("""{ "schemaVersion": 1, "SchemaVersion": 1, "specValues": {} }""")] // duplicate envelope property
    [InlineData("""{ "schemaVersion": 1, "specValues": { "k": 1, "k": 2 } }""")] // duplicate property in specValues
    [InlineData("""[]""")] // not an object
    public void ValidateSpecificationRequest_InvalidEnvelope_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Dprental.CatalogService.Domain.DTOs.EquipmentTypes.ValidateSpecificationRequest>(json));
    }

    [Fact]
    public void ValidateSpecificationRequest_ValidPayload_DeserializesAndSerializesCorrectly()
    {
        string json = """{ "schemaVersion": 2, "specValues": { "resolution": "4K" } }""";
        var request = JsonSerializer.Deserialize<Dprental.CatalogService.Domain.DTOs.EquipmentTypes.ValidateSpecificationRequest>(json);

        Assert.NotNull(request);
        Assert.Equal(2, request.SchemaVersion);
        Assert.Equal(JsonValueKind.Object, request.SpecValues.ValueKind);
        Assert.True(request.SpecValues.TryGetProperty("resolution", out var prop));
        Assert.Equal("4K", prop.GetString());

        string serialized = JsonSerializer.Serialize(request);
        Assert.Contains("\"schemaVersion\":2", serialized);
        Assert.Contains("\"resolution\":\"4K\"", serialized);
    }
}
