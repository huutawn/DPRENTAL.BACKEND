using System.Net;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Domain.Clients.Catalog;
using Dprental.InventoryService.Common;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Clients;

public sealed partial class CatalogServiceClientTests
{
    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_EmptyId_ReturnsValidationError()
    {
        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(Guid.Empty, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_EQUIPMENT_TYPE_ID", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_Success200_ParsesCurrentAndRequestedSchemas()
    {
        var eqId = Guid.NewGuid();
        var rawResponse = $$"""
        {
            "equipmentTypeId": "{{eqId:D}}",
            "currentSchemaVersion": 2,
            "schemas": [
                {
                    "version": 1,
                    "fields": [
                        { "key": "f1", "label": "Field 1", "dataType": "text", "filterable": true, "required": true, "primary": false, "displayOrder": 0 }
                    ]
                },
                {
                    "version": 2,
                    "fields": [
                        { "key": "f1", "label": "Field 1", "dataType": "text", "filterable": true, "required": true, "primary": false, "displayOrder": 0 },
                        { "key": "capacityKg", "label": "Capacity", "dataType": "number", "filterable": true, "required": false, "primary": false, "displayOrder": 1, "min": 100, "max": 5000, "unit": "kg" }
                    ]
                }
            ]
        }
        """;

        var (client, _) = CreateTestClient((req, ct) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal($"/api/v1/catalog/equipment-types/{eqId:D}/schemas/batch", req.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(rawResponse, Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1, 2]);

        Assert.True(result.IsSuccess);
        var val = result.Value;
        Assert.Equal(eqId, val.EquipmentTypeId);
        Assert.Equal(2, val.CurrentSchemaVersion);
        Assert.Equal(2, val.Schemas.Count);
        Assert.Contains(val.Schemas, s => s.Version == 1);
        Assert.Contains(val.Schemas, s => s.Version == 2);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_CatalogNotFound404_MapsTo400ValidationError()
    {
        var eqId = Guid.NewGuid();
        var problem = """
        {
            "title": "EquipmentType.NotFound",
            "detail": "Loại thiết bị không tồn tại."
        }
        """;

        var (client, _) = CreateTestClient((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(problem, Encoding.UTF8, "application/problem+json")
            });
        });

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("EquipmentType.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_CatalogBadRequest400_MapsTo400ValidationError()
    {
        var eqId = Guid.NewGuid();
        var problem = """
        {
            "title": "Catalog.InvalidRequest",
            "detail": "Tham số schema không hợp lệ."
        }
        """;

        var (client, _) = CreateTestClient((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(problem, Encoding.UTF8, "application/problem+json")
            });
        });

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [0]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("Catalog.InvalidRequest", result.Error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GetEquipmentTypeSchemasBatchAsync_ServerErrors_Return503Unavailable(HttpStatusCode status)
    {
        var eqId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(status)));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_Timeout_Returns503()
    {
        var eqId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) => throw new OperationCanceledException());

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_TIMEOUT", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_CallerCancellation_PropagatesException()
    {
        var eqId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateTestClient((req, ct) => Task.FromCanceled<HttpResponseMessage>(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetEquipmentTypeSchemasBatchAsync(eqId, [1], cts.Token));
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_NetworkError_Returns503()
    {
        var eqId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) => throw new HttpRequestException("Connection refused"));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_NETWORK_ERROR", result.Error.Code);
    }

    [Theory]
    [InlineData("")] // Empty body
    [InlineData("not-json")] // Invalid JSON
    [InlineData("[]")] // Root is array, not object
    public async Task GetEquipmentTypeSchemasBatchAsync_CorruptResponse_Returns503(string body)
    {
        var eqId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_MissingRequestedVersionInResponse_Returns503()
    {
        var eqId = Guid.NewGuid();
        // Requested version 2, but only version 1 returned
        var rawResponse = $$"""
        {
            "equipmentTypeId": "{{eqId:D}}",
            "currentSchemaVersion": 1,
            "schemas": [
                {
                    "version": 1,
                    "fields": [
                        { "key": "f1", "dataType": "text", "filterable": true, "required": true }
                    ]
                }
            ]
        }
        """;

        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rawResponse, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1, 2]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Theory]
    [InlineData("")] // Bare 404
    [InlineData("<html>404 Not Found</html>")] // HTML 404
    [InlineData("{\"title\":\"Gateway.RouteNotFound\",\"detail\":\"Route not found\"}")] // Route not found
    [InlineData("{\"title\":\"UnknownError\",\"detail\":\"Random\"}")] // Unknown title
    [InlineData("{not-json")] // Malformed JSON
    public async Task GetEquipmentTypeSchemasBatchAsync_NonContract404_Returns503(string body)
    {
        var eqId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_CatalogSchemaNotFound404_MapsTo400ValidationError()
    {
        var eqId = Guid.NewGuid();
        var problem = """
        {
            "title": "EquipmentType.SchemaNotFound",
            "detail": "Schema không tồn tại."
        }
        """;

        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(problem, Encoding.UTF8, "application/problem+json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("EquipmentType.SchemaNotFound", result.Error.Code);
    }

    [Theory]
    [InlineData("")] // Bare 400
    [InlineData("<html>400 Bad Request</html>")] // HTML 400
    [InlineData("{\"title\":\"Unknown.BadRequest\",\"detail\":\"Random\"}")] // Unknown title
    [InlineData("{bad-json")] // Malformed JSON
    public async Task GetEquipmentTypeSchemasBatchAsync_NonContract400_Returns503(string body)
    {
        var eqId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_CurrentSchemaVersionString_Returns503()
    {
        var eqId = Guid.NewGuid();
        var raw = $$"""
        {
            "equipmentTypeId": "{{eqId:D}}",
            "currentSchemaVersion": "1",
            "schemas": [
                { "version": 1, "fields": [{ "key": "f1", "dataType": "text", "filterable": true }] }
            ]
        }
        """;

        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Theory]
    [InlineData("{\"key\":\"f1\",\"dataType\":\"text\"}")] // Missing filterable
    [InlineData("{\"key\":\"f1\",\"dataType\":\"text\",\"filterable\":\"true\"}")] // filterable is string
    [InlineData("{\"key\":\"f1\",\"dataType\":\"text\",\"filterable\":1}")] // filterable is number
    [InlineData("{\"key\":\"f1\",\"dataType\":\"text\",\"filterable\":null}")] // filterable is null
    [InlineData("{\"key\":\"f1\",\"dataType\":\"unknown_type\",\"filterable\":true}")] // dataType outside whitelist
    [InlineData("{\"key\":\"f1\",\"dataType\":\"text\",\"filterable\":true,\"unit\":123}")] // unit is number
    [InlineData("{\"key\":\"f1\",\"dataType\":\"number\",\"filterable\":true,\"min\":\"100\"}")] // min is string
    [InlineData("{\"key\":\"f1\",\"dataType\":\"number\",\"filterable\":true,\"min\":1e100}")] // min unrepresentable decimal
    [InlineData("{\"key\":\"f1\",\"dataType\":\"enum\",\"filterable\":true}")] // enum missing options
    [InlineData("{\"key\":\"f1\",\"dataType\":\"enum\",\"filterable\":true,\"options\":null}")] // enum null options
    [InlineData("{\"key\":\"f1\",\"dataType\":\"enum\",\"filterable\":true,\"options\":[]}")] // enum empty options
    [InlineData("{\"key\":\"f1\",\"dataType\":\"enum\",\"filterable\":true,\"options\":[{\"value\":\"a\",\"label\":\"A\"},{\"value\":\"a\",\"label\":\"A2\"}]}")] // duplicate option value
    [InlineData("{\"key\":\"f1\",\"dataType\":\"enum\",\"filterable\":true,\"options\":[{\"value\":\"a\"}]}")] // option missing label
    [InlineData("{\"key\":\"f1\",\"dataType\":\"text\",\"filterable\":true,\"required\":\"true\"}")] // required wrong type
    [InlineData("{\"key\":\"f1\",\"dataType\":\"text\",\"filterable\":true,\"displayOrder\":\"0\"}")] // displayOrder wrong type
    public async Task GetEquipmentTypeSchemasBatchAsync_FieldValidationViolations_Return503(string fieldJson)
    {
        var eqId = Guid.NewGuid();
        var raw = $$"""
        {
            "equipmentTypeId": "{{eqId:D}}",
            "currentSchemaVersion": 1,
            "schemas": [
                { "version": 1, "fields": [ {{fieldJson}} ] }
            ]
        }
        """;

        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_DuplicateVersion_Returns503()
    {
        var eqId = Guid.NewGuid();
        var raw = $$"""
        {
            "equipmentTypeId": "{{eqId:D}}",
            "currentSchemaVersion": 1,
            "schemas": [
                { "version": 1, "fields": [{ "key": "f1", "dataType": "text", "filterable": true }] },
                { "version": 1, "fields": [{ "key": "f2", "dataType": "text", "filterable": true }] }
            ]
        }
        """;

        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_DuplicateFieldKeyInSameVersion_Returns503()
    {
        var eqId = Guid.NewGuid();
        var raw = $$"""
        {
            "equipmentTypeId": "{{eqId:D}}",
            "currentSchemaVersion": 1,
            "schemas": [
                {
                    "version": 1,
                    "fields": [
                        { "key": "duplicate_key", "dataType": "text", "filterable": true },
                        { "key": "duplicate_key", "dataType": "number", "filterable": true }
                    ]
                }
            ]
        }
        """;

        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_ValidNullableBoundsAndOmittedProperties_ParsesSuccessfully()
    {
        var eqId = Guid.NewGuid();
        var raw = $$"""
        {
            "equipmentTypeId": "{{eqId:D}}",
            "currentSchemaVersion": 1,
            "schemas": [
                {
                    "version": 1,
                    "fields": [
                        {
                            "key": "f1",
                            "label": null,
                            "dataType": "number",
                            "filterable": true,
                            "required": null,
                            "primary": null,
                            "displayOrder": null,
                            "min": null,
                            "max": null,
                            "unit": null,
                            "description": null,
                            "options": null
                        }
                    ]
                }
            ]
        }
        """;

        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(raw, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1]);

        Assert.True(result.IsSuccess);
        var field = result.Value.Schemas[0].Fields[0];
        Assert.Equal("f1", field.Key);
        Assert.True(field.Filterable);
        Assert.Null(field.Min);
        Assert.Null(field.Max);
        Assert.Null(field.Unit);
        Assert.Null(field.Description);
        Assert.Null(field.Options);
    }

    [Fact]
    public async Task GetEquipmentTypeSchemasBatchAsync_SerializedFromActualCatalogDto_ParsesSuccessfully()
    {
        var eqId = Guid.NewGuid();
        // Using actual serialized shape identical to Dprental.CatalogService.Domain.DTOs.EquipmentTypes.EquipmentTypeBatchSchemasResponse
        var catalogResponseDto = new
        {
            equipmentTypeId = eqId,
            currentSchemaVersion = 2,
            schemas = new[]
            {
                new
                {
                    version = 1,
                    fields = new object[]
                    {
                        new { key = "fuelType", label = "Nhiên liệu", dataType = "enum", required = true, filterable = true, primary = false, displayOrder = 1, options = new[] { new { value = "diesel", label = "Diesel" } } }
                    }
                },
                new
                {
                    version = 2,
                    fields = new object[]
                    {
                        new { key = "fuelType", label = "Nhiên liệu", dataType = "enum", required = true, filterable = true, primary = false, displayOrder = 1, options = new[] { new { value = "diesel", label = "Diesel" }, new { value = "electric", label = "Điện" } } },
                        new { key = "capacityKg", label = "Tải trọng", dataType = "number", required = false, filterable = true, primary = true, displayOrder = 2, min = 500m, max = 50000m, unit = "kg", description = "Sức nâng tối đa" }
                    }
                }
            }
        };

        string serializedJson = JsonSerializer.Serialize(catalogResponseDto);

        var (client, _) = CreateTestClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(serializedJson, Encoding.UTF8, "application/json")
        }));

        var result = await client.GetEquipmentTypeSchemasBatchAsync(eqId, [1, 2]);

        Assert.True(result.IsSuccess);
        Assert.Equal(eqId, result.Value.EquipmentTypeId);
        Assert.Equal(2, result.Value.CurrentSchemaVersion);
        Assert.Equal(2, result.Value.Schemas.Count);

        var v2 = result.Value.Schemas.First(s => s.Version == 2);
        Assert.Equal(2, v2.Fields.Count);
        var numField = v2.Fields.First(f => f.Key == "capacityKg");
        Assert.Equal("number", numField.DataType);
        Assert.Equal(500m, numField.Min);
        Assert.Equal(50000m, numField.Max);
        Assert.Equal("kg", numField.Unit);
        Assert.True(numField.Primary);
        Assert.True(numField.Filterable);
    }
}
