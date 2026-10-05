using System.Net;
using System.Text.Json;
using Dprental.InventoryService.Api.Clients;
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
    #region ValidateSpecificationAsync Tests

    [Fact]
    public async Task ValidateSpecificationAsync_Success200_ValidTrue_EmptyErrors_ReturnsSuccess()
    {
        var eqTypeId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        string? requestBody = null;
        const string expectedToken = "jwt-valid-token-123";

        var (client, _) = CreateTestClient(async (req, ct) =>
        {
            captured = req;
            requestBody = await req.Content!.ReadAsStringAsync(ct);
            var json = JsonSerializer.Serialize(new
            {
                valid = true,
                errors = Array.Empty<object>()
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }, token: expectedToken);

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{\"sensor\":\"Full-Frame\"}");

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal($"http://catalog-service:8080/api/v1/catalog/equipment-types/{eqTypeId:D}/specifications/validate", captured.RequestUri?.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal(expectedToken, captured.Headers.Authorization?.Parameter);

        Assert.NotNull(requestBody);
        using var parsedBody = JsonDocument.Parse(requestBody);
        Assert.Equal(1, parsedBody.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, parsedBody.RootElement.GetProperty("specValues").ValueKind);
        Assert.Equal("Full-Frame", parsedBody.RootElement.GetProperty("specValues").GetProperty("sensor").GetString());
    }

    [Fact]
    public async Task ValidateSpecificationAsync_Success200_ValidFalse_ReturnsValidationFailedWithErrors()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                valid = false,
                errors = new[]
                {
                    new { field = "mount", code = "INVALID_ENUM_OPTION", message = "Giá trị không thuộc danh sách tùy chọn hợp lệ." }
                }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{\"mount\":\"UNKNOWN\"}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("SPEC_VALIDATION_FAILED", result.Error.Code);
        Assert.NotNull(result.Error.Errors);
        var err = Assert.Single(result.Error.Errors);
        Assert.Equal("mount", err.Field);
        Assert.Equal("INVALID_ENUM_OPTION", err.Code);
        Assert.Equal("Giá trị không thuộc danh sách tùy chọn hợp lệ.", err.Message);
    }

    [Theory]
    [InlineData("sensor", "REQUIRED", "Trường này là bắt buộc.")]
    [InlineData("frameRate", "TYPE_MISMATCH", "Kiểu dữ liệu không hợp lệ.")]
    [InlineData("frameRate", "OUT_OF_RANGE", "Giá trị nằm ngoài phạm vi cho phép.")]
    [InlineData("mount", "INVALID_ENUM_OPTION", "Giá trị không thuộc danh sách tùy chọn hợp lệ.")]
    [InlineData("legacyPort", "UNKNOWN_FIELD", "Trường này không tồn tại trong schema.")]
    public async Task ValidateSpecificationAsync_FiveContractFieldCodes_AcceptedAndPreserved(string field, string code, string message)
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                valid = false,
                errors = new[]
                {
                    new { field, code, message }
                }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{\"foo\":\"bar\"}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("SPEC_VALIDATION_FAILED", result.Error.Code);
        Assert.NotNull(result.Error.Errors);
        var err = Assert.Single(result.Error.Errors);
        Assert.Equal(field, err.Field);
        Assert.Equal(code, err.Code);
        Assert.Equal(message, err.Message);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_MultipleErrors_PreservesExactOrderAndElements()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                valid = false,
                errors = new[]
                {
                    new { field = "sensor", code = "REQUIRED", message = "Trường này là bắt buộc." },
                    new { field = "resolution", code = "TYPE_MISMATCH", message = "Kiểu dữ liệu không hợp lệ." },
                    new { field = "extraField", code = "UNKNOWN_FIELD", message = "Trường này không tồn tại trong schema." }
                }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{\"resolution\":123,\"extraField\":true}");

        Assert.True(result.IsFailure);
        Assert.Equal("SPEC_VALIDATION_FAILED", result.Error.Code);
        Assert.NotNull(result.Error.Errors);
        Assert.Equal(3, result.Error.Errors.Count);

        Assert.Equal("sensor", result.Error.Errors[0].Field);
        Assert.Equal("REQUIRED", result.Error.Errors[0].Code);

        Assert.Equal("resolution", result.Error.Errors[1].Field);
        Assert.Equal("TYPE_MISMATCH", result.Error.Errors[1].Code);

        Assert.Equal("extraField", result.Error.Errors[2].Field);
        Assert.Equal("UNKNOWN_FIELD", result.Error.Errors[2].Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_UnrecognizedFieldErrorCode_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                valid = false,
                errors = new[]
                {
                    new { field = "sensor", code = "CUSTOM_UNKNOWN_CODE", message = "Lỗi lạ." }
                }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_MissingToken_Returns503DependencyError()
    {
        var eqTypeId = Guid.NewGuid();
        bool networkCalled = false;

        // Create client without token
        var (client, _) = CreateTestClient((req, ct) =>
        {
            networkCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, token: null);

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.False(networkCalled);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_TOKEN_MISSING", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_AuthenticationFailedTicket_DoesNotCallCatalog_Returns503()
    {
        var eqTypeId = Guid.NewGuid();
        bool networkCalled = false;

        var options = new CatalogServiceOptions
        {
            BaseUrl = "http://catalog-service:8080",
            TimeoutSeconds = 10
        };

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            networkCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/")
        };

        var context = new DefaultHttpContext();
        var feature = new TestAuthenticateResultFeature
        {
            AuthenticateResult = AuthenticateResult.Fail("Token expired or invalid signature")
        };
        context.Features.Set<IAuthenticateResultFeature>(feature);

        var accessor = new HttpContextAccessor { HttpContext = context };
        var client = new CatalogServiceClient(httpClient, Options.Create(options), accessor);

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.False(networkCalled);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_TOKEN_MISSING", result.Error.Code);
    }

    [Theory]
    [InlineData("EquipmentType.SchemaNotFound", "Schema version 3 chưa được publish cho loại thiết bị.")]
    [InlineData("EquipmentType.NotFound", "Loại thiết bị không tồn tại trong Catalog.")]
    public async Task ValidateSpecificationAsync_Catalog404WithKnownProblem_Returns400Validation(string title, string detail)
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var problem = JsonSerializer.Serialize(new
            {
                title,
                detail,
                status = 404
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(problem, System.Text.Encoding.UTF8, "application/problem+json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(title, result.Error.Code);
        Assert.Equal(detail, result.Error.Message);
        Assert.Null(result.Error.Errors);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_Catalog400WithInvalidRequest_Returns400Validation()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var problem = JsonSerializer.Serialize(new
            {
                title = "Catalog.InvalidRequest",
                detail = "Request validation không hợp lệ.",
                status = 400
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(problem, System.Text.Encoding.UTF8, "application/problem+json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("Catalog.InvalidRequest", result.Error.Code);
        Assert.Equal("Request validation không hợp lệ.", result.Error.Message);
        Assert.Null(result.Error.Errors);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_Catalog404WithoutKnownProblem_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("<!DOCTYPE html><html>Endpoint missing</html>", System.Text.Encoding.UTF8, "text/html")
            }));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_Catalog400WithoutKnownProblem_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var problem = JsonSerializer.Serialize(new
            {
                title = "Catalog.OtherError",
                detail = "Unknown bad request.",
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
    public async Task ValidateSpecificationAsync_NoContent204_Returns503_NoFallback()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_NotImplemented501_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented)));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task ValidateSpecificationAsync_OtherHttpErrors_Returns503(int statusCode)
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)));

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_Contradictory_ValidTrueWithErrors_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                valid = true,
                errors = new[]
                {
                    new { field = "sensor", code = "REQUIRED", message = "Bắt buộc." }
                }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_Contradictory_ValidFalseWithEmptyErrors_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                valid = false,
                errors = Array.Empty<object>()
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_MissingValidProperty_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                errors = Array.Empty<object>()
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_MissingErrorsProperty_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                valid = true
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var result = await client.ValidateSpecificationAsync(eqTypeId, 1, "{}");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
        Assert.Equal("CATALOG_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task ValidateSpecificationAsync_MalformedJson_Returns503()
    {
        var eqTypeId = Guid.NewGuid();

        var (client, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("malformed-not-json-content")
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var result = await client.ValidateSpecificationAsync(Guid.Parse(typeIdStr), version, specs);

        Assert.False(networkCalled);
        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    #endregion
}
