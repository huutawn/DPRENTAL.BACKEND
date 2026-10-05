using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Dprental.InventoryService.Api.Clients;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Common;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Clients;

public sealed class CatalogServiceClientTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handlerFunc(request, cancellationToken);
        }
    }

    private static (CatalogServiceClient client, CatalogServiceOptions options) CreateTestClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc)
    {
        var options = new CatalogServiceOptions
        {
            BaseUrl = "http://catalog-service:8080",
            TimeoutSeconds = 10
        };

        var handler = new MockHttpMessageHandler(handlerFunc);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/")
        };

        var optionsWrapper = Options.Create(options);
        return (new CatalogServiceClient(httpClient, optionsWrapper), options);
    }

    #region GetPublishedEquipmentTypeAsync Tests

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_Success200_ReturnsPublishedType()
    {
        var equipmentTypeId = Guid.NewGuid();
        HttpRequestMessage? captured = null;

        var (client, _) = CreateTestClient((req, ct) =>
        {
            captured = req;
            var json = JsonSerializer.Serialize(new
            {
                id = equipmentTypeId,
                code = "EQ-FX3",
                name = "Sony FX3 Cinema Line",
                description = "Full-frame cinema camera",
                status = 1, // Published
                createdAt = "2026-09-01T00:00:00Z",
                updatedAt = (string?)null,
                publishedAt = "2026-09-02T00:00:00Z"
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal($"http://catalog-service:8080/api/v1/catalog/equipment-types/{equipmentTypeId:D}", captured.RequestUri?.ToString());
        Assert.Equal(equipmentTypeId, result.Value.Id);
        Assert.Equal("EQ-FX3", result.Value.Code);
        Assert.Equal("Sony FX3 Cinema Line", result.Value.Name);
        Assert.Equal(1, result.Value.Status);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_StatusStringPublished_ReturnsSuccess()
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = equipmentTypeId,
                code = "EQ-FX3",
                name = "Sony FX3",
                status = "Published",
                createdAt = "2026-09-01T00:00:00Z"
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Status);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Disabled")]
    public async Task GetPublishedEquipmentTypeAsync_KnownUnpublishedStatus_ReturnsValidationError(string statusString)
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = equipmentTypeId,
                code = "EQ-DRAFT",
                name = "Draft Item",
                status = statusString,
                createdAt = "2026-09-01T00:00:00Z"
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("EQUIPMENT_TYPE_NOT_PUBLISHED", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task GetPublishedEquipmentTypeAsync_KnownUnpublishedIntStatus_ReturnsValidationError(int statusCode)
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = equipmentTypeId,
                code = "EQ-DRAFT",
                name = "Draft Item",
                status = statusCode,
                createdAt = "2026-09-01T00:00:00Z"
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("EQUIPMENT_TYPE_NOT_PUBLISHED", result.Error.Code);
    }
    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_NotFound404_ReturnsValidationError()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await client.GetPublishedEquipmentTypeAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("EQUIPMENT_TYPE_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_ServerError500_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await client.GetPublishedEquipmentTypeAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_EmptyResponse_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            }));

        var result = await client.GetPublishedEquipmentTypeAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_MalformedJson_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-valid-json")
            }));

        var result = await client.GetPublishedEquipmentTypeAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_IdMismatch_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid(), // Different ID
                code = "EQ-001",
                name = "Name",
                status = 1,
                createdAt = "2026-09-01T00:00:00Z"
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetPublishedEquipmentTypeAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_NetworkException_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            throw new HttpRequestException("Connection refused"));

        var result = await client.GetPublishedEquipmentTypeAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_NETWORK_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_TimeoutException_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            throw new OperationCanceledException());

        var result = await client.GetPublishedEquipmentTypeAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_TIMEOUT", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_CallerCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateTestClient((req, ct) =>
            throw new OperationCanceledException(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetPublishedEquipmentTypeAsync(Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_EmptyGuid_ReturnsValidationWithoutNetworkCall()
    {
        bool networkCalled = false;
        var (client, _) = CreateTestClient((req, ct) =>
        {
            networkCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var result = await client.GetPublishedEquipmentTypeAsync(Guid.Empty);

        Assert.False(networkCalled);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_EQUIPMENT_TYPE_ID", result.Error.Code);
    }

    #endregion

    #region GetBrandAsync Tests

    [Fact]
    public async Task GetBrandAsync_Success200_Active_ReturnsBrand()
    {
        var brandId = Guid.NewGuid();
        HttpRequestMessage? captured = null;

        var (client, _) = CreateTestClient((req, ct) =>
        {
            captured = req;
            var json = JsonSerializer.Serialize(new
            {
                id = brandId,
                code = "BR-SONY",
                name = "Sony",
                status = "Active"
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetBrandAsync(brandId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal($"http://catalog-service:8080/api/v1/catalog/brands/{brandId:D}", captured.RequestUri?.ToString());
        Assert.Equal(brandId, result.Value.Id);
        Assert.Equal("BR-SONY", result.Value.Code);
        Assert.Equal("Sony", result.Value.Name);
        Assert.Equal("Active", result.Value.Status);
    }

    [Fact]
    public async Task GetBrandAsync_Success200_Inactive_ReturnsBrandWithInactiveStatus()
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = brandId,
                code = "BR-OLD",
                name = "Old Brand",
                status = "Inactive"
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetBrandAsync(brandId);

        Assert.True(result.IsSuccess);
        Assert.Equal(brandId, result.Value.Id);
        Assert.Equal("Inactive", result.Value.Status);
    }

    [Fact]
    public async Task GetBrandAsync_NotFound404_WithBrandNotFoundProblem_ReturnsValidationError()
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var problem = JsonSerializer.Serialize(new
            {
                title = "Brand.NotFound",
                detail = "Thương hiệu không tồn tại trong hệ thống.",
                status = 404
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(problem, System.Text.Encoding.UTF8, "application/problem+json")
            });
        });

        var result = await client.GetBrandAsync(brandId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("Brand.NotFound", result.Error.Code);
        Assert.Equal("Thương hiệu không tồn tại trong hệ thống.", result.Error.Message);
    }

    [Fact]
    public async Task GetBrandAsync_NotFound404_EndpointMissing_Returns503Unavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(string.Empty)
            }));

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_ServerError500_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_NotImplemented501_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented)));

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_MalformedJson_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("malformed-not-json")
            }));

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_IdMismatch_ReturnsUnavailable()
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid(), // Mismatched ID
                code = "BR-SONY",
                name = "Sony",
                status = "Active"
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetBrandAsync(brandId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_InvalidStatus_ReturnsUnavailable()
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = brandId,
                code = "BR-SONY",
                name = "Sony",
                status = "PendingApproval" // Invalid non-contract status
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetBrandAsync(brandId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_NetworkException_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            throw new HttpRequestException("Network failure"));

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_NETWORK_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_Timeout_ReturnsUnavailable()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            throw new OperationCanceledException());

        var result = await client.GetBrandAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_TIMEOUT", result.Error.Code);
    }
    [Fact]
    public async Task GetBrandAsync_CallerCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateTestClient((req, ct) =>
            throw new OperationCanceledException(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetBrandAsync(Guid.NewGuid(), cts.Token));
    }


    [Fact]
    public async Task GetBrandAsync_EmptyGuid_ReturnsValidationWithoutNetworkCall()
    {
        bool networkCalled = false;
        var (client, _) = CreateTestClient((req, ct) =>
        {
            networkCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var result = await client.GetBrandAsync(Guid.Empty);

        Assert.False(networkCalled);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_BRAND_ID", result.Error.Code);
    }

    #endregion

    #region ValidateSpecificationAsync Tests

    [Fact]
    public async Task ValidateSpecificationAsync_Success204_ReturnsSuccess()
    {
        var eqTypeId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        string? requestBody = null;

        var (client, _) = CreateTestClient(async (req, ct) =>
        {
            captured = req;
            requestBody = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{\"sensor\":\"Full-Frame\"}");

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal($"http://catalog-service:8080/api/v1/catalog/equipment-types/{eqTypeId:D}/validate-specification", captured.RequestUri?.ToString());
        Assert.NotNull(requestBody);

        using var parsedBody = JsonDocument.Parse(requestBody);
        Assert.Equal(1, parsedBody.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, parsedBody.RootElement.GetProperty("specValues").ValueKind);
        Assert.Equal("Full-Frame", parsedBody.RootElement.GetProperty("specValues").GetProperty("sensor").GetString());
    }

    [Theory]
    [InlineData("EquipmentType.InvalidAssetSchemaVersion", "Schema version 3 chưa được publish cho loại thiết bị.")]
    [InlineData("EquipmentType.SpecRequired", "Trường 'resolution' là bắt buộc.")]
    [InlineData("EquipmentType.SpecDatatype", "Trường 'frameRate' phải là kiểu số nguyên.")]
    [InlineData("EquipmentType.SpecRange", "Giá trị 'frameRate' phải từ 24 đến 240.")]
    [InlineData("EquipmentType.SpecEnum", "Giá trị 'mount' phải thuộc danh sách hỗ trợ (E-mount, PL).")]
    public async Task ValidateSpecificationAsync_BusinessError400_PreservesContractCodeAndDetail(string code, string detail)
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var problem = JsonSerializer.Serialize(new
            {
                title = code,
                detail = detail,
                status = 400
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(problem, System.Text.Encoding.UTF8, "application/problem+json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{\"sensor\":\"APS-C\"}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(code, result.Error.Code);
        Assert.Equal(detail, result.Error.Message);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_PublishErrorReusedOrUnrecognizedCode_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        // Catalog returns 400 with publish error code EquipmentType.InvalidSchemaVersion (not contract code EquipmentType.InvalidAssetSchemaVersion)
        var (client, _) = CreateTestClient((req, ct) =>
        {
            var problem = JsonSerializer.Serialize(new
            {
                title = "EquipmentType.InvalidSchemaVersion", // Old publish code
                detail = "Version publish phải lớn hơn version hiện tại.",
                status = 400
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(problem, System.Text.Encoding.UTF8, "application/problem+json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_PublishError409Conflict_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_EndpointMissing404_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_NotSupported501_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented)));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_ServerError500_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_MalformedJsonIn400_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("malformed-not-problem-details")
            }));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_NetworkFailure_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            throw new HttpRequestException("Connection refused"));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_NETWORK_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_Timeout_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            throw new OperationCanceledException());

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}", CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_TIMEOUT", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_CallerCancelled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateTestClient((req, ct) =>
            throw new OperationCanceledException(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ValidateSpecificationAsync(Guid.NewGuid(), 1, "{}", cts.Token));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", 1, "{}", "INVALID_EQUIPMENT_TYPE_ID")]
    [InlineData("11111111-1111-1111-1111-111111111111", 0, "{}", "INVALID_SCHEMA_VERSION")]
    [InlineData("11111111-1111-1111-1111-111111111111", -1, "{}", "INVALID_SCHEMA_VERSION")]
    [InlineData("11111111-1111-1111-1111-111111111111", 1, "", "INVALID_SPEC_VALUES")]
    [InlineData("11111111-1111-1111-1111-111111111111", 1, "[]", "INVALID_SPEC_VALUES")]
    [InlineData("11111111-1111-1111-1111-111111111111", 1, "not-json", "INVALID_SPEC_VALUES")]
    public async Task ValidateSpecificationAsync_LocalValidation_FailsWithoutNetworkCall(string typeIdStr, int version, string specs, string expectedCode)
    {
        bool networkCalled = false;
        var (client, _) = CreateTestClient((req, ct) =>
        {
            networkCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        var result = await client.ValidateSpecificationAsync(Guid.Parse(typeIdStr), version, specs);

        Assert.False(networkCalled);
        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    #endregion

    #region Options Tests

    [Fact]
    public void CatalogServiceOptions_Validation_DetectsErrors()
    {
        var invalidOptions = new CatalogServiceOptions
        {
            BaseUrl = "ftp://invalid-scheme",
            TimeoutSeconds = 10
        };

        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(invalidOptions, new ValidationContext(invalidOptions), validationResults, true);

        Assert.False(isValid);
        Assert.Contains(validationResults, v => v.ErrorMessage!.Contains("http hoặc https"));
    }

    [Theory]
    [InlineData("http://user:pass@localhost:8080", "credentials")]
    [InlineData("http://localhost:8080/api?query=1", "query")]
    [InlineData("http://localhost:8080/api#fragment", "fragment")]
    public void CatalogServiceOptions_Validation_RejectsMalformedUrls(string url, string expectedWord)
    {
        var invalidOptions = new CatalogServiceOptions
        {
            BaseUrl = url,
            TimeoutSeconds = 10
        };

        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(invalidOptions, new ValidationContext(invalidOptions), validationResults, true);

        Assert.False(isValid);
        Assert.Contains(validationResults, v => v.ErrorMessage!.Contains(expectedWord, StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
