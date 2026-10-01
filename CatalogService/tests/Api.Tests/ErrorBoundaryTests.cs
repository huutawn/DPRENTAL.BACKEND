using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.CatalogService.Api.Exceptions;
using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.Equipments;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class ErrorBoundaryTests
{
    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.Unauthorized, StatusCodes.Status401Unauthorized)]
    [InlineData(ErrorType.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Failure, StatusCodes.Status500InternalServerError)]
    public void ExpectedErrorFilterExtensions_MapsAllErrorTypesCorrectly(ErrorType errorType, int expectedStatusCode)
    {
        var controller = new DummyController();
        var error = new Error("TEST_CODE", "Test message", errorType);

        ObjectResult problemResult = controller.ToProblem(error);

        Assert.Equal(expectedStatusCode, problemResult.StatusCode);
        var problemDetails = Assert.IsAssignableFrom<ProblemDetails>(problemResult.Value);
        Assert.Equal("TEST_CODE", problemDetails.Title);
        Assert.Equal("Test message", problemDetails.Detail);
    }

    [Fact]
    public async Task UnexpectedException_ReturnsSafe500ProblemDetails_WithoutLeakingExceptionDetails()
    {
        using WebApplicationFactory<Program> factory = CreateThrowingFactory(
            new InvalidOperationException("SECRET_DATABASE_CREDENTIALS_LEAK"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipments");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("SECRET_DATABASE_CREDENTIALS_LEAK", content, StringComparison.OrdinalIgnoreCase);

        using JsonDocument doc = JsonDocument.Parse(content);
        Assert.Equal(CatalogErrors.Catalog.Uncategorized.Code, doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(CatalogErrors.Catalog.Uncategorized.Message, doc.RootElement.GetProperty("detail").GetString());
        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task RequestWithCorrelationIdHeader_EchoesSameCorrelationId_InResponseHeader()
    {
        const string customCorrelationId = "custom-trace-id-12345";
        using WebApplicationFactory<Program> factory = PublicCatalogEndpointTests.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/equipments");
        request.Headers.Add("X-Correlation-ID", customCorrelationId);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.Equal(customCorrelationId, values.FirstOrDefault());
    }

    [Fact]
    public async Task RequestWithoutCorrelationIdHeader_GeneratesNewUuid_InResponseHeader()
    {
        using WebApplicationFactory<Program> factory = PublicCatalogEndpointTests.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipments");

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        string? correlationId = values.FirstOrDefault();
        Assert.NotNull(correlationId);
        Assert.True(Guid.TryParse(correlationId, out Guid parsed) && parsed != Guid.Empty);
    }

    [Fact]
    public async Task GlobalExceptionHandler_CancellationRequested_ReturnsFalse_AndDoesNotConvertInto500()
    {
        var handler = new GlobalExceptionHandler(
            new DummyProblemDetailsService(),
            NullLogger<GlobalExceptionHandler>.Instance);

        var context = new DefaultHttpContext();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        OperationCanceledException oce = new(cts.Token);

        bool handled = await handler.TryHandleAsync(context, oce, cts.Token);

        Assert.False(handled);
    }

    private static WebApplicationFactory<Program> CreateThrowingFactory(Exception ex) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
                builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IEquipmentQueryRepository>();
                    services.AddScoped<IEquipmentQueryRepository>(_ => new ThrowingEquipmentRepository(ex));
                });
            });

    private sealed class DummyController : ControllerBase { }

    private sealed class DummyProblemDetailsService : IProblemDetailsService
    {
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(false);
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;
    }

    private sealed class ThrowingEquipmentRepository(Exception exception) : IEquipmentQueryRepository
    {
        public Task<PagedResult<EquipmentListItemDto>> SearchPublishedAsync(GetEquipmentsRequest request, CancellationToken cancellationToken = default)
        {
            throw exception;
        }

        public Task<EquipmentDetailDto?> GetPublishedByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }
}
