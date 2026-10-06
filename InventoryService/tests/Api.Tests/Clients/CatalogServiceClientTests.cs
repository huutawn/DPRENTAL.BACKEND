using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Clients;

public sealed partial class CatalogServiceClientTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handlerFunc(request, cancellationToken);
        }
    }
    private sealed class TestAuthenticateResultFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }


    private static (CatalogServiceClient client, CatalogServiceOptions options) CreateTestClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc,
        string? token = "default-test-jwt-token")
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

        IHttpContextAccessor accessor;
        if (token is not null)
        {
            var context = new DefaultHttpContext();
            var authProperties = new AuthenticationProperties();
            authProperties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = token }]);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(), authProperties, "Bearer");
            var feature = new TestAuthenticateResultFeature { AuthenticateResult = AuthenticateResult.Success(ticket) };
            context.Features.Set<IAuthenticateResultFeature>(feature);

            accessor = new HttpContextAccessor { HttpContext = context };
        }
        else
        {
            accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        }

        var optionsWrapper = Options.Create(options);
        return (new CatalogServiceClient(httpClient, optionsWrapper, accessor), options);
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
                id = Guid.NewGuid(),
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
