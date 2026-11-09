using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dprental.ReviewService.Api.Filters;
using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Application.Kafka.Workers;
using Dprental.ReviewService.Application.Options;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ReviewService.Api.Tests;

public sealed class RentalServiceClientAndErrorMappingTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetReviewSnapshotAsync_When404HasBusinessCode_ReturnsRentalItemNotFound()
    {
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var handler = new MockHttpMessageHandler(req =>
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "RENTAL_ITEM_NOT_FOUND",
                Detail = "Item not found"
            };
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(problem)
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://rental-service") };
        var client = new RentalServiceClient(httpClient);

        var result = await client.GetReviewSnapshotAsync(orderId, itemId);
        Assert.True(result.IsFailure);
        Assert.Equal("RENTAL_ITEM_NOT_FOUND", result.Error.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public async Task GetReviewSnapshotAsync_When404IsGenericOrRouteMissing_Returns502InvalidResponse()
    {
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var handler = new MockHttpMessageHandler(req =>
        {
            // Generic 404 returned by ASP.NET Core when route is not found
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not Found",
                Detail = "No endpoint matched"
            };
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(problem)
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://rental-service") };
        var client = new RentalServiceClient(httpClient);

        var result = await client.GetReviewSnapshotAsync(orderId, itemId);
        Assert.True(result.IsFailure);
        Assert.Equal("RENTAL_SERVICE_INVALID_RESPONSE", result.Error.Code);
        Assert.Equal(ErrorType.BadGateway, result.Error.Type);
    }

    [Fact]
    public async Task GetReviewSnapshotAsync_WhenResponseSchemaMismatchedOrMissingIds_Returns502InvalidResponse()
    {
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var handler = new MockHttpMessageHandler(req =>
        {
            // Return 200 OK but with mismatched orderId
            var snapshot = new RentalItemReviewSnapshotDto(
                Guid.NewGuid(), itemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Completed", FixedNow, 1);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(snapshot)
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://rental-service") };
        var client = new RentalServiceClient(httpClient);

        var result = await client.GetReviewSnapshotAsync(orderId, itemId);
        Assert.True(result.IsFailure);
        Assert.Equal("RENTAL_SERVICE_INVALID_RESPONSE", result.Error.Code);
        Assert.Equal(ErrorType.BadGateway, result.Error.Type);
    }

    [Fact]
    public async Task GetReconciliationBatchAsync_CallsCorrectContractRoute()
    {
        string? requestedPath = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            requestedPath = req.RequestUri?.PathAndQuery;
            var batch = new ReconciliationBatchDto([], null, false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(batch)
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://rental-service") };
        var client = new RentalServiceClient(httpClient);

        var result = await client.GetReconciliationBatchAsync(cursor: "test-cursor", limit: 50);
        Assert.True(result.IsSuccess);
        Assert.NotNull(requestedPath);
        Assert.Equal("/internal/orders/review-reconciliation?limit=50&cursor=test-cursor", requestedPath);
    }

    [Fact]
    public async Task GetReconciliationBatchAsync_When404_Returns502InvalidResponse()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Route not found")
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://rental-service") };
        var client = new RentalServiceClient(httpClient);

        var result = await client.GetReconciliationBatchAsync();
        Assert.True(result.IsFailure);
        Assert.Equal("RENTAL_SERVICE_INVALID_RESPONSE", result.Error.Code);
        Assert.Equal(ErrorType.BadGateway, result.Error.Type);
    }

    [Fact]
    public void ExpectedErrorFilterExtensions_MapsBadGatewayTo502()
    {
        var controller = new TestController();
        var error = Error.BadGateway("RENTAL_SERVICE_INVALID_RESPONSE", "Invalid response from upstream");

        var problemResult = controller.ToProblem(error);
        Assert.Equal(StatusCodes.Status502BadGateway, problemResult.StatusCode);

        var problemDetails = Assert.IsType<ProblemDetails>(problemResult.Value);
        Assert.Equal("RENTAL_SERVICE_INVALID_RESPONSE", problemDetails.Title);
        Assert.Equal("Invalid response from upstream", problemDetails.Detail);
    }

    [Fact]
    public async Task ReviewAuthoringService_RejectsInvalidSnapshotSchema_With502BadGateway()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString("N");
        services.AddDbContext<ReviewServiceDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddScoped<IReviewRepository, ReviewRepository>();
        services.AddScoped<IReviewAuditRepository, ReviewAuditRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();
        services.AddScoped<IIdempotencyService, IdempotencyService>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();

        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var renterId = Guid.NewGuid();

        // Snapshot returns empty EquipmentListingId
        var corruptSnapshot = new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, Guid.NewGuid(), Guid.Empty, "Completed", FixedNow.AddDays(-5), 1);

        var mockRentalClient = new SimpleMockRentalClient(corruptSnapshot);
        services.AddSingleton<IRentalServiceClient>(mockRentalClient);
        services.AddSingleton<TimeProvider>(new TestTimeProvider(FixedNow));

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReviewServiceDbContext>();
        var service = new ReviewAuthoringService(
            db,
            scope.ServiceProvider.GetRequiredService<IReviewRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewAuditRepository>(),
            scope.ServiceProvider.GetRequiredService<IIdempotencyRepository>(),
            scope.ServiceProvider.GetRequiredService<IIdempotencyService>(),
            mockRentalClient,
            scope.ServiceProvider.GetRequiredService<IOutboxRepository>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>());

        var actor = new ReviewActorContext(renterId, "Renter", false);
        var request = new CreateReviewRequest(orderId, itemId, 5, "Great product");

        var result = await service.CreateReviewAsync(actor, request, "idemp-corrupt-1");
        Assert.True(result.IsFailure);
        Assert.Equal("RENTAL_SERVICE_INVALID_RESPONSE", result.Error.Code);
        Assert.Equal(ErrorType.BadGateway, result.Error.Type);
    }

    [Fact]
    public async Task RentalReconciliationSweepWorker_WhenPageFetchFails_RetainsCheckpointUncompletedForRetry()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString("N");
        services.AddDbContext<ReviewServiceDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddReviewServiceRepositories();
        services.AddScoped<IReviewEligibilityService, ReviewEligibilityService>();

        var mockRentalClient = new SimpleMockRentalClient(null)
        {
            BatchHandler = (cursor, limit) =>
                Task.FromResult(Result.Failure<ReconciliationBatchDto>(
                    Error.BadGateway("RENTAL_SERVICE_INVALID_RESPONSE", "Gateway timeout")))
        };
        services.AddSingleton<IRentalServiceClient>(mockRentalClient);
        var timeProvider = new TestTimeProvider(FixedNow);
        services.AddSingleton<TimeProvider>(timeProvider);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var worker = new RentalReconciliationSweepWorker(scopeFactory, timeProvider);
        int processed = await worker.RunSweepCycleAsync();

        Assert.Equal(0, processed);

        using var checkScope = scopeFactory.CreateScope();
        var repo = checkScope.ServiceProvider.GetRequiredService<IReconciliationRepository>();
        var checkpoint = await repo.GetLatestAsync();
        Assert.NotNull(checkpoint);
        Assert.False(checkpoint.IsCompleted); // Must NOT be marked completed!
    }

    #region Helpers

    private sealed class TestController : ControllerBase { }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class SimpleMockRentalClient : IRentalServiceClient
    {
        private readonly RentalItemReviewSnapshotDto? _snapshot;
        public Func<string?, int, Task<Result<ReconciliationBatchDto>>>? BatchHandler { get; set; }

        public SimpleMockRentalClient(RentalItemReviewSnapshotDto? snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(Guid orderId, Guid itemId, CancellationToken cancellationToken = default)
        {
            if (_snapshot != null) return Task.FromResult(Result.Success(_snapshot));
            return Task.FromResult(Result.Failure<RentalItemReviewSnapshotDto>(Error.NotFound("RENTAL_ITEM_NOT_FOUND", "Not found")));
        }

        public Task<Result<ReconciliationBatchDto>> GetReconciliationBatchAsync(string? cursor = null, int limit = 100, CancellationToken cancellationToken = default)
        {
            if (BatchHandler != null) return BatchHandler(cursor, limit);
            return Task.FromResult(Result.Success(new ReconciliationBatchDto([], null, false)));
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    #endregion
}
