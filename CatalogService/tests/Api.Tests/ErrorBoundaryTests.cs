using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.CatalogService.Api.Exceptions;
using Dprental.CatalogService.Api.Filters;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;
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
    [InlineData(ErrorType.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public void ExpectedErrorFilterExtensions_MapsAllErrorTypesCorrectly(ErrorType errorType, int expectedStatusCode)
    {
        var controller = new DummyController();
        var error = new Error("Test.Error", "Test message", errorType);

        var result = controller.ToProblem(error);

        Assert.Equal(expectedStatusCode, result.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("Test.Error", problemDetails.Title);
        Assert.Equal("Test message", problemDetails.Detail);
    }

    [Fact]
    public async Task UnexpectedException_ReturnsSafe500ProblemDetails_WithoutLeakingExceptionDetails()
    {
        using WebApplicationFactory<Program> factory = CreateThrowingFactory(
            new InvalidOperationException("SECRET_DATABASE_CREDENTIALS_LEAK"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipment-types");

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

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/equipment-types");
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

        using HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/equipment-types");

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        string? correlationId = values.FirstOrDefault();
        Assert.NotNull(correlationId);
        Assert.True(Guid.TryParse(correlationId, out Guid parsed) && parsed != Guid.Empty);
    }

    [Fact]
    public async Task GlobalExceptionHandler_CancellationRequested_ReturnsFalse_AndDoesNotConvertInto500()
    {
        var problemDetailsService = new DummyProblemDetailsService();
        var handler = new GlobalExceptionHandler(problemDetailsService, NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        bool handled = await handler.TryHandleAsync(context, new OperationCanceledException(cts.Token), cts.Token);

        Assert.False(handled);
    }

    private static WebApplicationFactory<Program> CreateThrowingFactory(Exception ex) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
                builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
                builder.UseTestJwks();

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IEquipmentTypeRepository>();
                    services.AddScoped<IEquipmentTypeRepository>(_ => new ThrowingEquipmentTypeRepository(ex));
                });
            });

    private sealed class DummyController : ControllerBase { }

    private sealed class DummyProblemDetailsService : IProblemDetailsService
    {
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(false);
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;
    }

    private sealed class ThrowingEquipmentTypeRepository(Exception exception) : IEquipmentTypeRepository
    {
        public Task<PagedResult<EquipmentTypeListItemDto>> SearchPublishedAsync(GetEquipmentTypesRequest request, CancellationToken cancellationToken = default)
        {
            throw exception;
        }

        public Task<EquipmentTypeDetailDto?> GetPublishedByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            throw exception;
        }

        public Task<EquipmentType?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task<EquipmentTypeDetailDto?> GetDetailByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task<bool> ExistsByCodeAsync(string code, Guid? excludeId = null, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task<Result<EquipmentTypeDetailDto>> AddAsync(EquipmentType equipmentType, IReadOnlyList<EquipmentTypeMediaItemDto?>? mediaItems, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task<Result<EquipmentTypeDetailDto>> SaveAsync(EquipmentType entity, IReadOnlyList<EquipmentTypeMediaItemDto?>? desiredMedia, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task<Result<EquipmentTypeDetailDto>> SaveStateTransitionAsync(EquipmentType entity, CancellationToken cancellationToken = default) =>
            throw exception;
    }
}
