using System.Net;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Common;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Clients;

public sealed partial class CatalogServiceClientTests
{
    #region GetBrandAsync Tests

    [Fact]
    public async Task GetBrandAsync_Success200_Active_WithAllFields_ReturnsBrand()
    {
        var brandId = Guid.NewGuid();
        var logoMediaId = Guid.NewGuid();
        HttpRequestMessage? captured = null;

        var (client, _) = CreateTestClient((req, ct) =>
        {
            captured = req;
            var json = JsonSerializer.Serialize(new
            {
                id = brandId,
                code = "BR-SONY",
                name = "Sony",
                slug = "sony",
                logoMediaId = logoMediaId,
                logoUrl = "http://media-service:8080/files/sony.png",
                isActive = true,
                createdAt = "2026-09-01T00:00:00Z",
                updatedAt = (string?)null
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetBrandAsync(brandId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(brandId, result.Value.Id);
        Assert.Equal("BR-SONY", result.Value.Code);
        Assert.Equal("Sony", result.Value.Name);
        Assert.True(result.Value.IsActive);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal($"http://catalog-service:8080/api/v1/catalog/brands/{brandId:D}", captured.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetBrandAsync_Success200_Inactive_ReturnsBrand()
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = brandId,
                code = "BR-OLD",
                name = "Old Brand",
                slug = "old-brand",
                logoMediaId = (Guid?)null,
                logoUrl = (string?)null,
                isActive = false,
                createdAt = "2026-09-01T00:00:00Z",
                updatedAt = (string?)null
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetBrandAsync(brandId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.IsActive);
    }

    [Fact]
    public async Task GetBrandAsync_MissingIsActiveProperty200_Returns503()
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = brandId,
                code = "BR-SONY",
                name = "Sony"
                // isActive omitted
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
    public async Task GetBrandAsync_NullIsActiveProperty200_Returns503()
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = brandId,
                code = "BR-SONY",
                name = "Sony",
                isActive = (bool?)null
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

    [Theory]
    [InlineData("\"true\"")] // string instead of boolean
    [InlineData("1")]        // number instead of boolean
    [InlineData("{}")]       // object instead of boolean
    public async Task GetBrandAsync_WrongTypeIsActiveProperty200_Returns503(string rawIsActive)
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = $"{{\"id\":\"{brandId:D}\",\"code\":\"BR-SONY\",\"name\":\"Sony\",\"isActive\":{rawIsActive}}}";

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
    public async Task GetBrandAsync_NotFound404_BrandNotFoundProblem_ReturnsValidation()
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var problem = JsonSerializer.Serialize(new
            {
                title = "Brand.NotFound",
                detail = "Thương hiệu không tồn tại trong Catalog.",
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
        Assert.Equal("Thương hiệu không tồn tại trong Catalog.", result.Error.Message);
    }

    [Fact]
    public async Task GetBrandAsync_NotFound404_EndpointMissing_Returns503()
    {
        var brandId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("<!DOCTYPE html><html>404 Not Found</html>", System.Text.Encoding.UTF8, "text/html")
            }));

        var result = await client.GetBrandAsync(brandId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_EmptyContent200_Returns503()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            }));

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_MalformedJson200_Returns503()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("malformed-not-json", System.Text.Encoding.UTF8, "application/json")
            }));

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_MissingRequiredFields200_Returns503()
    {
        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid(),
                code = "BR-01"
                // name is missing
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_MismatchedId200_Returns503()
    {
        var requestedId = Guid.NewGuid();
        var returnedId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                id = returnedId,
                code = "BR-01",
                name = "Name",
                isActive = true
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.GetBrandAsync(requestedId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task GetBrandAsync_NonSuccessStatusCodes_Returns503(int statusCode)
    {
        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)));

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_NetworkFailure_Returns503()
    {
        var (client, _) = CreateTestClient((req, ct) =>
            throw new HttpRequestException("Network failure"));

        var result = await client.GetBrandAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_NETWORK_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetBrandAsync_Timeout_Returns503()
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
}
