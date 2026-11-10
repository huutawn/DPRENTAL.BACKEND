#pragma warning disable CA1848, CA1873
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Reviews;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Http;
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
}
