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

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_Success200_ReturnsPublishedType()
    {
        var equipmentTypeId = Guid.NewGuid();
        HttpRequestMessage? captured = null;

        var (client, _) = CreateTestClient(async (req, ct) =>
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

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal($"/api/v1/catalog/equipment-types/{equipmentTypeId:D}", captured.RequestUri?.AbsolutePath);
        Assert.Equal(equipmentTypeId, result.Value.Id);
        Assert.Equal("EQ-FX3", result.Value.Code);
        Assert.Equal("Sony FX3 Cinema Line", result.Value.Name);
        Assert.Equal(1, result.Value.Status);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_UnpublishedDraft_ReturnsValidationError()
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient(async (req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = equipmentTypeId,
                code = "EQ-FX3",
                name = "Sony FX3 Draft",
                status = 0 // Draft
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("EQUIPMENT_TYPE_NOT_PUBLISHED", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_IdMismatch_Returns503()
    {
        var requestedId = Guid.NewGuid();
        var differentId = Guid.NewGuid();

        var (client, _) = CreateTestClient(async (req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = differentId,
                code = "EQ-FX3",
                name = "Sony FX3",
                status = 1
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var result = await client.GetPublishedEquipmentTypeAsync(requestedId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-5)]
    public async Task GetPublishedEquipmentTypeAsync_UnknownNumericStatus_Returns503(int unknownStatus)
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient(async (req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = equipmentTypeId,
                code = "EQ-FX3",
                name = "Sony FX3",
                status = unknownStatus
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_DisabledStatus_Returns400()
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient(async (req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = equipmentTypeId,
                code = "EQ-FX3",
                name = "Sony FX3",
                status = 2 // Disabled in CatalogService contract
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("EQUIPMENT_TYPE_NOT_PUBLISHED", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_ArchivedStatus_Returns503_BecauseOutsideContract()
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient(async (req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = equipmentTypeId,
                code = "EQ-FX3",
                name = "Sony FX3",
                status = "Archived" // Outside CatalogService contract (only Draft, Published, Disabled)
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_NotFound404_ReturnsValidationError()
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient(async (req, ct) =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"title\":\"Not Found\"}", System.Text.Encoding.UTF8, "application/problem+json")
            };
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("EQUIPMENT_TYPE_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_InvalidJson_Returns503Unavailable()
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient(async (req, ct) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-valid-json", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_NetworkFailure_Returns503Unavailable()
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            throw new HttpRequestException("Connection refused"));

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_NETWORK_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_Timeout_Returns503Unavailable()
    {
        var equipmentTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            throw new OperationCanceledException());

        var result = await client.GetPublishedEquipmentTypeAsync(equipmentTypeId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_TIMEOUT", result.Error.Code);
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_CallerCancellation_PropagatesException()
    {
        var equipmentTypeId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateTestClient((req, ct) =>
            throw new OperationCanceledException(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetPublishedEquipmentTypeAsync(equipmentTypeId, cts.Token));
    }

    [Fact]
    public async Task GetPublishedEquipmentTypeAsync_EmptyGuid_ReturnsValidation()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await client.GetPublishedEquipmentTypeAsync(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_EQUIPMENT_TYPE_ID", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_ReturnsNotSupported_WithoutCallingNetwork()
    {
        bool networkCalled = false;
        var (client, _) = CreateTestClient((req, ct) =>
        {
            networkCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var result = await client.ValidateSpecificationAsync(Guid.NewGuid(), 1, "{}");

        Assert.False(networkCalled);
        Assert.True(result.IsSuccess);
        Assert.Equal(CatalogSpecValidationStatus.NotSupported, result.Value.Status);
    }

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
}
