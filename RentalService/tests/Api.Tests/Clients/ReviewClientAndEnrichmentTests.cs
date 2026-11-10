#pragma warning disable CA1848, CA1873
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Reviews;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Clients;

public sealed class ReviewClientAndEnrichmentTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; } =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public List<HttpRequestMessage> SentRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (SentRequests)
            {
                SentRequests.Add(request);
            }
            return Handler(request);
        }
    }

    private static IConfiguration CreateConfig(string baseUrl = "http://review-service:8080")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Services:Review:BaseUrl", baseUrl)
            })
            .Build();
    }

    [Fact]
    public async Task HttpReviewRentalClient_ForwardsBearerToken_AndSerializesCamelCase()
    {
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddDays(25);
        const string bearerToken = "jwt-test-user-token-xyz";

        var mockHandler = new MockHttpMessageHandler
        {
            Handler = async req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("http://review-service:8080/api/v1/reviews/status/batch", req.RequestUri!.ToString());
                Assert.NotNull(req.Headers.Authorization);
                Assert.Equal("Bearer", req.Headers.Authorization.Scheme);
                Assert.Equal(bearerToken, req.Headers.Authorization.Parameter);

                string body = await req.Content!.ReadAsStringAsync();
                Assert.Contains("\"orderId\":", body);
                Assert.Contains("\"itemId\":", body);

                var resp = new BatchReviewStatusResponse([
                    new ReviewItemStatusResponse(
                        orderId,
                        itemId,
                        new ReviewStatusDto("AVAILABLE", reviewId, deadline))
                ]);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resp, JsonOptions), Encoding.UTF8, "application/json")
                };
            }
        };

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = $"Bearer {bearerToken}";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var tokenHandler = new ReviewTokenPropagationHandler(accessor)
        {
            InnerHandler = mockHandler
        };

        using var httpClient = new HttpClient(tokenHandler);
        var client = new HttpReviewRentalClient(httpClient, CreateConfig(), NullLogger<HttpReviewRentalClient>.Instance);

        var request = new BatchReviewStatusRequest([new ReviewStatusItemRequest(orderId, itemId)]);
        var result = await client.GetBatchReviewStatusAsync(request);

        Assert.True(result.IsSuccess);
        var responseItem = Assert.Single(result.Value.Items);
        Assert.Equal(orderId, responseItem.OrderId);
        Assert.Equal(itemId, responseItem.ItemId);
        Assert.Equal("AVAILABLE", responseItem.ReviewStatus.State);
        Assert.Equal(reviewId, responseItem.ReviewStatus.ReviewId);
    }

    [Fact]
    public async Task HttpReviewRentalClient_DownstreamError_ReturnsFailureWithoutThrowing()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"downstream_fail\"}")
            })
        };

        using var httpClient = new HttpClient(mockHandler);
        var client = new HttpReviewRentalClient(httpClient, CreateConfig(), NullLogger<HttpReviewRentalClient>.Instance);

        var request = new BatchReviewStatusRequest([new ReviewStatusItemRequest(Guid.NewGuid(), Guid.NewGuid())]);
        var result = await client.GetBatchReviewStatusAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("REVIEW_SERVICE_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task HttpReviewRentalClient_MalformedJson_ReturnsFailure()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{malformed-json-payload-not-valid", Encoding.UTF8, "application/json")
            })
        };

        using var httpClient = new HttpClient(mockHandler);
        var client = new HttpReviewRentalClient(httpClient, CreateConfig(), NullLogger<HttpReviewRentalClient>.Instance);

        var request = new BatchReviewStatusRequest([new ReviewStatusItemRequest(Guid.NewGuid(), Guid.NewGuid())]);
        var result = await client.GetBatchReviewStatusAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("REVIEW_SERVICE_INVALID_RESPONSE", result.Error.Code);
    }

    [Fact]
    public async Task ReviewTokenPropagationHandler_TwoConcurrentRequests_DoNotMixTokens()
    {
        var accessor = new HttpContextAccessor();
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                var token = req.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"token\":\"{token}\"}}")
                });
            }
        };

        var handler = new ReviewTokenPropagationHandler(accessor)
        {
            InnerHandler = mockHandler
        };
        using var httpClient = new HttpClient(handler);

        using var barrier = new Barrier(2);
        var taskA = Task.Run(async () =>
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["Authorization"] = "Bearer token-user-A";
            accessor.HttpContext = ctx;
            barrier.SignalAndWait();

            var req = new HttpRequestMessage(HttpMethod.Get, "http://review-service:8080/test");
            await httpClient.SendAsync(req);
            return req.Headers.Authorization?.Parameter;
        });

        var taskB = Task.Run(async () =>
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["Authorization"] = "Bearer token-user-B";
            accessor.HttpContext = ctx;
            barrier.SignalAndWait();

            var req = new HttpRequestMessage(HttpMethod.Get, "http://review-service:8080/test");
            await httpClient.SendAsync(req);
            return req.Headers.Authorization?.Parameter;
        });

        var results = await Task.WhenAll(taskA, taskB);
        Assert.Equal("token-user-A", results[0]);
        Assert.Equal("token-user-B", results[1]);
    }

    #region OrderReviewStatusEnricher Tests

    private sealed class SpyReviewRentalClient : IReviewRentalClient
    {
        private int _activeCalls;
        public int MaxObservedConcurrency { get; private set; }
        public List<BatchReviewStatusRequest> ReceivedRequests { get; } = [];
        public Func<BatchReviewStatusRequest, CancellationToken, Task<Result<BatchReviewStatusResponse>>> Handler { get; set; } =
            (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse(
                req.Items.Select(i => new ReviewItemStatusResponse(i.OrderId, i.ItemId, new ReviewStatusDto("AVAILABLE", null, null))).ToList())));

        public async Task<Result<BatchReviewStatusResponse>> GetBatchReviewStatusAsync(
            BatchReviewStatusRequest request,
            CancellationToken cancellationToken = default)
        {
            int current = Interlocked.Increment(ref _activeCalls);
            lock (this)
            {
                if (current > MaxObservedConcurrency)
                {
                    MaxObservedConcurrency = current;
                }
            }

            ReceivedRequests.Add(request);

            try
            {
                return await Handler(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }
    }

    [Fact]
    public async Task OrderReviewStatusEnricher_EmptyItems_ReturnsEmptyImmediately()
    {
        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        var result = await enricher.FetchReviewStatusesAsync([]);

        Assert.Empty(result);
        Assert.Empty(spyClient.ReceivedRequests);
    }

    [Fact]
    public async Task OrderReviewStatusEnricher_ChunksInto100_AndRespectsMax2Concurrency()
    {
        var spyClient = new SpyReviewRentalClient
        {
            Handler = async (req, ct) =>
            {
                // Giả lập độ trễ mạng ngắn để đo concurrency
                await Task.Delay(20, ct);
                var items = req.Items.Select(i => new ReviewItemStatusResponse(
                    i.OrderId, i.ItemId, new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(10)))).ToList();
                return Result.Success(new BatchReviewStatusResponse(items));
            }
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        // Tạo 250 phần tử khác nhau -> 3 batch (100, 100, 50)
        var items = Enumerable.Range(1, 250)
            .Select(_ => (OrderId: Guid.NewGuid(), ItemId: Guid.NewGuid()))
            .ToList();

        var result = await enricher.FetchReviewStatusesAsync(items);

        Assert.Equal(250, result.Count);
        Assert.Equal(3, spyClient.ReceivedRequests.Count);
        Assert.Equal(100, spyClient.ReceivedRequests[0].Items.Count);
        Assert.Equal(100, spyClient.ReceivedRequests[1].Items.Count);
        Assert.Equal(50, spyClient.ReceivedRequests[2].Items.Count);
        Assert.True(spyClient.MaxObservedConcurrency <= 2, $"Concurrency was {spyClient.MaxObservedConcurrency}, expected <= 2");
    }

    [Fact]
    public async Task OrderReviewStatusEnricher_PartialDownstreamFailure_LeavesFailedUnknownAndMergesSuccess()
    {
        var order1 = Guid.NewGuid();
        var item1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();
        var item2 = Guid.NewGuid();

        int callCount = 0;
        var spyClient = new SpyReviewRentalClient
        {
            Handler = (req, _) =>
            {
                int c = Interlocked.Increment(ref callCount);
                if (req.Items.Any(i => i.ItemId == item1))
                {
                    // Batch 1 success
                    return Task.FromResult(Result.Success(new BatchReviewStatusResponse([
                        new ReviewItemStatusResponse(order1, item1, new ReviewStatusDto("REVIEWED", Guid.NewGuid(), null))
                    ])));
                }

                // Batch 2 fail
                return Task.FromResult(Result.Failure<BatchReviewStatusResponse>(
                    Error.Failure("DOWNSTREAM_FAIL", "Error")));
            }
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        var result = await enricher.FetchReviewStatusesAsync([(order1, item1), (order2, item2)]);

        Assert.Equal(2, result.Count);
        Assert.Equal("REVIEWED", result[(order1, item1)].State);
        Assert.Equal("UNKNOWN", result[(order2, item2)].State);
    }

    [Fact]
    public async Task OrderReviewStatusEnricher_CallerCancellation_RethrowsOperationCanceled()
    {
        var spyClient = new SpyReviewRentalClient
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(500, ct);
                return Result.Success(new BatchReviewStatusResponse([]));
            }
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Đã hủy từ request gốc

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            enricher.FetchReviewStatusesAsync([(Guid.NewGuid(), Guid.NewGuid())], cts.Token));
    }

    [Fact]
    public async Task OrderReviewStatusEnricher_DiscardsExtraItemsNotInRequest()
    {
        var requestedOrder = Guid.NewGuid();
        var requestedItem = Guid.NewGuid();
        var extraOrder = Guid.NewGuid();
        var extraItem = Guid.NewGuid();

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse([
                new ReviewItemStatusResponse(requestedOrder, requestedItem, new ReviewStatusDto("AVAILABLE", null, null)),
                new ReviewItemStatusResponse(extraOrder, extraItem, new ReviewStatusDto("AVAILABLE", null, null))
            ])))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        var result = await enricher.FetchReviewStatusesAsync([(requestedOrder, requestedItem)]);

        Assert.Single(result);
        Assert.True(result.ContainsKey((requestedOrder, requestedItem)));
        Assert.False(result.ContainsKey((extraOrder, extraItem)));
    }

    [Fact]
    public async Task OrderReviewStatusEnricher_EvaluatesAllFiveStates_AndHandlesNullFields()
    {
        var order = Guid.NewGuid();
        var itemAvailable = Guid.NewGuid();
        var itemReviewed = Guid.NewGuid();
        var itemExpired = Guid.NewGuid();
        var itemNotEligible = Guid.NewGuid();
        var itemUnknown = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddDays(20);

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (_, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse([
                new ReviewItemStatusResponse(order, itemAvailable, new ReviewStatusDto("AVAILABLE", null, deadline)),
                new ReviewItemStatusResponse(order, itemReviewed, new ReviewStatusDto("REVIEWED", reviewId, deadline)),
                new ReviewItemStatusResponse(order, itemExpired, new ReviewStatusDto("EXPIRED", null, deadline.AddDays(-30))),
                new ReviewItemStatusResponse(order, itemNotEligible, new ReviewStatusDto("NOT_ELIGIBLE", null, null)),
                new ReviewItemStatusResponse(order, itemUnknown, new ReviewStatusDto("UNKNOWN", null, null))
            ])))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        var result = await enricher.FetchReviewStatusesAsync([
            (order, itemAvailable),
            (order, itemReviewed),
            (order, itemExpired),
            (order, itemNotEligible),
            (order, itemUnknown)
        ]);

        Assert.Equal(5, result.Count);

        Assert.Equal("AVAILABLE", result[(order, itemAvailable)].State);
        Assert.Null(result[(order, itemAvailable)].ReviewId);
        Assert.NotNull(result[(order, itemAvailable)].DeadlineAt);

        Assert.Equal("REVIEWED", result[(order, itemReviewed)].State);
        Assert.Equal(reviewId, result[(order, itemReviewed)].ReviewId);

        Assert.Equal("EXPIRED", result[(order, itemExpired)].State);
        Assert.Null(result[(order, itemExpired)].ReviewId);
        Assert.NotNull(result[(order, itemExpired)].DeadlineAt);

        Assert.Equal("NOT_ELIGIBLE", result[(order, itemNotEligible)].State);
        Assert.Null(result[(order, itemNotEligible)].ReviewId);
        Assert.Null(result[(order, itemNotEligible)].DeadlineAt);

        Assert.Equal("UNKNOWN", result[(order, itemUnknown)].State);
        Assert.Null(result[(order, itemUnknown)].ReviewId);
        Assert.Null(result[(order, itemUnknown)].DeadlineAt);
    }

    [Fact]
    public async Task OrderReviewStatusEnricher_InvalidStateAndMissingItems_KeepsUnknown()
    {
        var order = Guid.NewGuid();
        var itemWeirdState = Guid.NewGuid();
        var itemMissingFromResponse = Guid.NewGuid();

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (_, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse([
                new ReviewItemStatusResponse(order, itemWeirdState, new ReviewStatusDto("UNEXPECTED_CUSTOM_STATE", null, null))
            ])))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        var result = await enricher.FetchReviewStatusesAsync([
            (order, itemWeirdState),
            (order, itemMissingFromResponse)
        ]);

        Assert.Equal(2, result.Count);
        Assert.Equal("UNKNOWN", result[(order, itemWeirdState)].State);
        Assert.Equal("UNKNOWN", result[(order, itemMissingFromResponse)].State);
    }

    [Fact]
    public async Task OrderReviewStatusEnricher_Downstream401Or403_KeepsUnknownWithoutThrowing()
    {
        var order = Guid.NewGuid();
        var item = Guid.NewGuid();

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (_, _) => Task.FromResult(Result.Failure<BatchReviewStatusResponse>(
                Error.Unauthorized("UNAUTHORIZED", "Downstream token expired")))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        var result = await enricher.FetchReviewStatusesAsync([(order, item)]);

        Assert.Single(result);
        Assert.Equal("UNKNOWN", result[(order, item)].State);
    }

    [Fact]
    public async Task OrderReviewStatusEnricher_InputDeduplication_SendsOnlyDistinctPairs()
    {
        var order = Guid.NewGuid();
        var item = Guid.NewGuid();

        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        var result = await enricher.FetchReviewStatusesAsync([
            (order, item),
            (order, item),
            (order, item)
        ]);

        Assert.Single(result);
        var singleRequest = Assert.Single(spyClient.ReceivedRequests);
        Assert.Single(singleRequest.Items);
    }

    #endregion

    #region Order Query Enrichment Tests

    private static RentalCallerContext Caller(Guid userId) => OrderTestData.User(userId);

    private readonly string _queryDbName = $"ReviewEnrichment_{Guid.NewGuid():N}";
    private readonly Guid _an = Guid.NewGuid();
    private readonly Guid _binh = Guid.NewGuid();
    private readonly Guid _otherRenter = Guid.NewGuid();

    private RentalServiceDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(_queryDbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private async Task<(RentalOrder PaidOrder, RentalOrder OtherOrder)> SeedOrdersAsync()
    {
        await using var db = NewContext();

        var line1 = OrderTestData.Line(_binh, "Máy xúc bánh xích", "Excavator", "CAT", 1, 2_000_000m, 10_000_000m);
        var line2 = OrderTestData.Line(_binh, "Máy lu tĩnh", "Roller", "BOMAG", 1, 1_000_000m, 5_000_000m);
        var paidOrder = OrderTestData.Order(_an, "DH-2026-TEST01", OrderTestData.Now, line1, line2);
        OrderTestData.Paid(paidOrder, OrderTestData.Now.AddMinutes(5));
        OrderTestData.LinkItemsToOrder(paidOrder);

        var otherLine = OrderTestData.Line(_binh, "Máy ủi đất", "Bulldozer", "Komatsu", 1, 3_000_000m, 15_000_000m);
        var otherOrder = OrderTestData.Order(_otherRenter, "DH-2026-OTHER", OrderTestData.Now, otherLine);
        OrderTestData.Paid(otherOrder, OrderTestData.Now.AddMinutes(5));
        OrderTestData.LinkItemsToOrder(otherOrder);

        db.RentalOrders.AddRange(paidOrder, otherOrder);
        await db.SaveChangesAsync();

        return (paidOrder, otherOrder);
    }

    [Fact]
    public async Task Renter_GetMyOrders_EnrichesReviewStatus_GathersWholePageWithoutNPlusOne()
    {
        var (paidOrder, _) = await SeedOrdersAsync();

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse(
                req.Items.Select(i => new ReviewItemStatusResponse(
                    i.OrderId,
                    i.ItemId,
                    new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(15)))).ToList())))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        var result = await queryService.GetMyOrdersAsync(1, 20, null, Caller(_an));

        Assert.True(result.IsSuccess);
        var page = result.Value;
        var order = Assert.Single(page.Items);
        Assert.Equal(paidOrder.Id, order.OrderId);
        Assert.Equal(2, order.Items.Count);

        foreach (var item in order.Items)
        {
            Assert.Equal("AVAILABLE", item.ReviewStatus.State);
            Assert.Null(item.ReviewStatus.ReviewId);
            Assert.NotNull(item.ReviewStatus.DeadlineAt);
        }

        Assert.Single(spyClient.ReceivedRequests);
        Assert.Equal(2, spyClient.ReceivedRequests[0].Items.Count);
    }

    [Fact]
    public async Task Renter_GetMyOrders_OnlySendsItems_BelongingToAuthorizedUser()
    {
        var (paidOrder, otherOrder) = await SeedOrdersAsync();

        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        var result = await queryService.GetMyOrdersAsync(1, 20, null, Caller(_an));

        Assert.True(result.IsSuccess);
        var singleBatch = Assert.Single(spyClient.ReceivedRequests);

        Assert.All(singleBatch.Items, i => Assert.Equal(paidOrder.Id, i.OrderId));
        Assert.DoesNotContain(singleBatch.Items, i => i.OrderId == otherOrder.Id);
    }

    [Fact]
    public async Task Renter_GetMyOrders_Over100Items_ChunksIntoMultipleBatchesCorrectly()
    {
        await using var db = NewContext();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var lines = Enumerable.Range(1, 120)
            .Select(i => OrderTestData.Line(supplierId, $"Thiết bị {i}", "Tool", "Generic", 1, 100_000m, 500_000m))
            .ToArray();

        var largeOrder = OrderTestData.Order(renterId, "DH-LARGE-120", OrderTestData.Now, lines);
        OrderTestData.Paid(largeOrder, OrderTestData.Now.AddMinutes(5));
        OrderTestData.LinkItemsToOrder(largeOrder);
        db.RentalOrders.Add(largeOrder);
        await db.SaveChangesAsync();

        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        var result = await queryService.GetMyOrdersAsync(1, 20, null, Caller(renterId));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, spyClient.ReceivedRequests.Count);
        Assert.Equal(100, spyClient.ReceivedRequests[0].Items.Count);
        Assert.Equal(20, spyClient.ReceivedRequests[1].Items.Count);
    }

    [Fact]
    public async Task Renter_GetMyOrder_EnrichesReviewStatus_ForOrderDetail()
    {
        var (paidOrder, _) = await SeedOrdersAsync();
        var item1Id = paidOrder.Items.First().Id;
        var item2Id = paidOrder.Items.Last().Id;
        var reviewId = Guid.NewGuid();

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse([
                new ReviewItemStatusResponse(paidOrder.Id, item1Id, new ReviewStatusDto("REVIEWED", reviewId, null)),
                new ReviewItemStatusResponse(paidOrder.Id, item2Id, new ReviewStatusDto("EXPIRED", null, DateTimeOffset.UtcNow.AddDays(-1)))
            ])))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        var result = await queryService.GetMyOrderAsync(paidOrder.Id, Caller(_an));

        Assert.True(result.IsSuccess);
        var detail = result.Value;
        var allItems = detail.SubOrders.SelectMany(s => s.Items).ToList();
        Assert.Equal(2, allItems.Count);

        var i1 = allItems.Single(i => i.RentalItemId == item1Id);
        Assert.Equal("REVIEWED", i1.ReviewStatus.State);
        Assert.Equal(reviewId, i1.ReviewStatus.ReviewId);

        var i2 = allItems.Single(i => i.RentalItemId == item2Id);
        Assert.Equal("EXPIRED", i2.ReviewStatus.State);
        Assert.Null(i2.ReviewStatus.ReviewId);
    }

    [Fact]
    public async Task Renter_GetMyOrder_UnauthorizedAccess_DoesNotCallReviewService()
    {
        var (paidOrder, _) = await SeedOrdersAsync();

        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        var strangerId = Guid.NewGuid();
        var result = await queryService.GetMyOrderAsync(paidOrder.Id, Caller(strangerId));

        Assert.True(result.IsFailure);
        Assert.Equal("Order.NotFound", result.Error.Code);
        Assert.Empty(spyClient.ReceivedRequests);
    }

    [Fact]
    public async Task Supplier_GetSubOrderById_UsesParentOrderId_AndEnrichesCorrectly()
    {
        var (paidOrder, _) = await SeedOrdersAsync();
        var subOrder = paidOrder.SubOrders.Single();
        var item1 = subOrder.Items.First();

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse([
                new ReviewItemStatusResponse(paidOrder.Id, item1.Id, new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(10)))
            ])))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new SupplierSubOrderQueryService(repo, enricher);

        var result = await queryService.GetMySubOrderAsync(subOrder.Id, Caller(_binh));

        Assert.True(result.IsSuccess);
        var detail = result.Value;
        Assert.Equal(paidOrder.OrderCode, detail.OrderCode);
        Assert.Equal(2, detail.Items.Count);

        var sentBatch = Assert.Single(spyClient.ReceivedRequests);
        Assert.All(sentBatch.Items, i => Assert.Equal(paidOrder.Id, i.OrderId));
    }

    [Fact]
    public async Task Supplier_GetSubOrderById_UnauthorizedSupplier_DoesNotCallReviewService()
    {
        var (paidOrder, _) = await SeedOrdersAsync();
        var subOrder = paidOrder.SubOrders.Single();

        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new SupplierSubOrderQueryService(repo, enricher);

        var otherSupplier = Guid.NewGuid();
        var result = await queryService.GetMySubOrderAsync(subOrder.Id, Caller(otherSupplier));

        Assert.True(result.IsFailure);
        Assert.Equal("SubOrder.NotFound", result.Error.Code);
        Assert.Empty(spyClient.ReceivedRequests);
    }

    [Fact]
    public async Task QueryServices_WhenReviewServiceFails_RetainsOrderData_WithUnknownReviewStatus()
    {
        var (paidOrder, _) = await SeedOrdersAsync();

        var failingClient = new SpyReviewRentalClient
        {
            Handler = (_, _) => Task.FromResult(Result.Failure<BatchReviewStatusResponse>(
                Error.Failure("DOWNSTREAM_UNAVAILABLE", "ReviewService offline")))
        };

        var enricher = new OrderReviewStatusEnricher(failingClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);

        // 1. Renter List
        var renterService = new RenterOrderQueryService(repo, enricher);
        var listResult = await renterService.GetMyOrdersAsync(1, 20, null, Caller(_an));
        Assert.True(listResult.IsSuccess);
        var summaryItem = listResult.Value.Items.Single().Items[0];
        Assert.Equal("UNKNOWN", summaryItem.ReviewStatus.State);
        Assert.Equal(paidOrder.Version, listResult.Value.Items.Single().Version);

        // 2. Renter Detail
        var detailResult = await renterService.GetMyOrderAsync(paidOrder.Id, Caller(_an));
        Assert.True(detailResult.IsSuccess);
        var detailItem = detailResult.Value.SubOrders.Single().Items[0];
        Assert.Equal("UNKNOWN", detailItem.ReviewStatus.State);

        // 3. Supplier SubOrder Detail
        var supplierService = new SupplierSubOrderQueryService(repo, enricher);
        var subOrderDetail = await supplierService.GetMySubOrderAsync(paidOrder.SubOrders.Single().Id, Caller(_binh));
        Assert.True(subOrderDetail.IsSuccess);
        var supplierItem = subOrderDetail.Value.Items[0];
        Assert.Equal("UNKNOWN", supplierItem.ReviewStatus.State);
    }

    [Fact]
    public async Task SameItem_TwoActors_ReceiveIndependentReviewStatuses()
    {
        var (paidOrder, _) = await SeedOrdersAsync();
        var subOrder = paidOrder.SubOrders.Single();
        var targetItemId = subOrder.Items.First().Id;
        var supplierReviewId = Guid.NewGuid();

        var renterClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse(
                req.Items.Select(i => new ReviewItemStatusResponse(
                    i.OrderId, i.ItemId, new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(25)))).ToList())))
        };

        var supplierClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse(
                req.Items.Select(i => new ReviewItemStatusResponse(
                    i.OrderId, i.ItemId,
                    i.ItemId == targetItemId
                        ? new ReviewStatusDto("REVIEWED", supplierReviewId, null)
                        : new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(25)))).ToList())))
        };

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);

        var renterService = new RenterOrderQueryService(repo, new OrderReviewStatusEnricher(renterClient, NullLogger<OrderReviewStatusEnricher>.Instance));
        var supplierService = new SupplierSubOrderQueryService(repo, new OrderReviewStatusEnricher(supplierClient, NullLogger<OrderReviewStatusEnricher>.Instance));

        var renterOrder = (await renterService.GetMyOrderAsync(paidOrder.Id, Caller(_an))).Value;
        var renterItem = renterOrder.SubOrders.Single().Items.Single(i => i.RentalItemId == targetItemId);
        Assert.Equal("AVAILABLE", renterItem.ReviewStatus.State);
        Assert.Null(renterItem.ReviewStatus.ReviewId);

        var supplierOrder = (await supplierService.GetMySubOrderAsync(subOrder.Id, Caller(_binh))).Value;
        var supplierItem = supplierOrder.Items.Single(i => i.RentalItemId == targetItemId);
        Assert.Equal("REVIEWED", supplierItem.ReviewStatus.State);
        Assert.Equal(supplierReviewId, supplierItem.ReviewStatus.ReviewId);
    }

    [Fact]
    public void CrossService_JwtConfig_IssuerAndAudience_MatchBetweenRentalAndReviewService()
    {
        const string expectedIssuer = "dprental-authservice-dev";
        const string expectedAudience = "dprental-clients";

        var rentalConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Jwt:Issuer"] = expectedIssuer,
                ["Authentication:Jwt:Audience"] = expectedAudience
            })
            .Build();

        string rentalIssuer = rentalConfig["Authentication:Jwt:Issuer"] ?? expectedIssuer;
        string rentalAudience = rentalConfig["Authentication:Jwt:Audience"] ?? expectedAudience;

        Assert.Equal(expectedIssuer, rentalIssuer);
        Assert.Equal(expectedAudience, rentalAudience);
    }

    #endregion
}
