using System.Net.Http.Headers;
using Dprental.RentalService.Api.Security;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Clients;

public sealed class HttpClientsAndTokenProviderIntegrationTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<HttpRequestMessage> SentRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task HttpPaymentRentalClient_InjectsApiKey()
    {
        const string expectedKey = "dev-internal-api-key-dprental-2026";
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.True(req.Headers.Contains("X-Api-Key"));
                Assert.Equal(expectedKey, req.Headers.GetValues("X-Api-Key").Single());
                Assert.Null(req.Headers.Authorization);
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("http://payment-service:8080/api/v1/invoices/INV-001/cancel", req.RequestUri!.ToString());

                var resp = new CancelInvoiceResponse("INV-001", true, DateTimeOffset.UtcNow, false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resp), System.Text.Encoding.UTF8, "application/json")
                };
            }
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Services:Payment:BaseUrl", "http://payment-service:8080"),
                new KeyValuePair<string, string?>("Services:Payment:ApiKey", expectedKey)
            })
            .Build();

        var httpClient = new HttpClient(mockHandler);
        var client = new HttpPaymentRentalClient(httpClient, config, NullLogger<HttpPaymentRentalClient>.Instance);

        var req = new CancelInvoiceRequest(Guid.NewGuid(), "INV-001", "Timeout", "op-cancel-1");
        var result = await client.CancelInvoiceAsync(req);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Cancelled);
    }

    [Fact]
    public async Task HttpInventoryRentalClient_Upstream401_Returns503DependencyError()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Services:Inventory:BaseUrl", "http://inventory-service:8080"),
                new KeyValuePair<string, string?>("InternalAuthentication:ApiKey", "test-key")
            }).Build();
        var client = new HttpInventoryRentalClient(new HttpClient(mockHandler), config, NullLogger<HttpInventoryRentalClient>.Instance);
        var result = await client.GetListingRentalSnapshotsAsync(new ListingRentalSnapshotsRequest([Guid.NewGuid()]));
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("INVENTORY_UNAVAILABLE", result.Error.Code);
    }

    [Fact]
    public async Task HttpPaymentRentalClient_Upstream401_Returns503DependencyError()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Services:Payment:BaseUrl", "http://payment-service:8080"),
                new KeyValuePair<string, string?>("Services:Payment:ApiKey", "test-key")
            }).Build();
        var client = new HttpPaymentRentalClient(new HttpClient(mockHandler), config, NullLogger<HttpPaymentRentalClient>.Instance);
        var result = await client.CancelInvoiceAsync(new CancelInvoiceRequest(Guid.NewGuid(), "INV-001", "Timeout", "op-1"));
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("PAYMENT_UNAVAILABLE", result.Error.Code);
    }

    [Fact]
    public async Task HttpAuthRentalClient_SendsCorrectEndpointApiKeyAndBearerToken_AndReadsOnlyIdAndIsProfileCompleted()
    {
        const string expectedKey = "internal-auth-key-123";
        const string bearerToken = "ey.sample.jwt.token";
        Guid expectedUserId = Guid.NewGuid();

        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.Equal("http://auth-service:8080/api/v1/auth/users/me", req.RequestUri!.ToString());
                Assert.True(req.Headers.Contains("X-Api-Key"));
                Assert.Equal(expectedKey, req.Headers.GetValues("X-Api-Key").Single());
                Assert.NotNull(req.Headers.Authorization);
                Assert.Equal("Bearer", req.Headers.Authorization.Scheme);
                Assert.Equal(bearerToken, req.Headers.Authorization.Parameter);

                var responseJson = $$"""
                {
                    "id": "{{expectedUserId}}",
                    "username": "renter1",
                    "email": "renter1@example.com",
                    "isProfileCompleted": true,
                    "roles": ["customer"],
                    "extraField": "ignored"
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
                };
            }
        };

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = $"Bearer {bearerToken}";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var tokenHandler = new AuthTokenPropagationHandler(accessor)
        {
            InnerHandler = mockHandler
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Services:Auth:BaseUrl", "http://auth-service:8080"),
                new KeyValuePair<string, string?>("InternalAuthentication:ApiKey", expectedKey)
            }).Build();

        var client = new HttpAuthRentalClient(new HttpClient(tokenHandler), config, NullLogger<HttpAuthRentalClient>.Instance);
        var result = await client.GetCurrentUserAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedUserId, result.Value.Id);
        Assert.True(result.Value.IsProfileCompleted);
    }

    [Fact]
    public async Task AuthTokenPropagationHandler_ConcurrentRequests_DoNotMixTokens()
    {
        var accessor = new HttpContextAccessor();
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                var auth = req.Headers.Authorization?.Parameter;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"token": "{{auth}}"}""")
                };
            }
        };

        var handler = new AuthTokenPropagationHandler(accessor)
        {
            InnerHandler = mockHandler
        };
        var httpClient = new HttpClient(handler);

        using var barrier = new Barrier(2);
        var task1 = Task.Run(async () =>
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["Authorization"] = "Bearer token-renter-1";
            accessor.HttpContext = ctx;
            barrier.SignalAndWait();

            var req = new HttpRequestMessage(HttpMethod.Get, "http://test.local/me");
            var resp = await httpClient.SendAsync(req);
            return req.Headers.Authorization?.Parameter;
        });

        var task2 = Task.Run(async () =>
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["Authorization"] = "Bearer token-renter-2";
            accessor.HttpContext = ctx;
            barrier.SignalAndWait();

            var req = new HttpRequestMessage(HttpMethod.Get, "http://test.local/me");
            var resp = await httpClient.SendAsync(req);
            return req.Headers.Authorization?.Parameter;
        });

        string?[] results = await Task.WhenAll(task1, task2);
        Assert.Equal("token-renter-1", results[0]);
        Assert.Equal("token-renter-2", results[1]);
    }

    [Fact]
    public async Task HttpAuthRentalClient_Auth401_ReturnsUnauthenticated()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        var config = new ConfigurationBuilder().Build();
        var client = new HttpAuthRentalClient(new HttpClient(mockHandler), config, NullLogger<HttpAuthRentalClient>.Instance);

        var result = await client.GetCurrentUserAsync();
        Assert.True(result.IsFailure);
        Assert.Equal("Checkout.Unauthenticated", result.Error.Code);
    }

    [Fact]
    public async Task HttpAuthRentalClient_Auth404_ReturnsUnauthenticated()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        var config = new ConfigurationBuilder().Build();
        var client = new HttpAuthRentalClient(new HttpClient(mockHandler), config, NullLogger<HttpAuthRentalClient>.Instance);

        var result = await client.GetCurrentUserAsync();
        Assert.True(result.IsFailure);
        Assert.Equal("Checkout.Unauthenticated", result.Error.Code);
    }

    [Fact]
    public async Task HttpAuthRentalClient_Auth403_ReturnsForbidden()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"title":"Auth.Forbidden","detail":"Account locked"}""")
            }
        };
        var config = new ConfigurationBuilder().Build();
        var client = new HttpAuthRentalClient(new HttpClient(mockHandler), config, NullLogger<HttpAuthRentalClient>.Instance);

        var result = await client.GetCurrentUserAsync();
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Equal("Auth.Forbidden", result.Error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpAuthRentalClient_Auth5xx_ReturnsDependencyUnavailable(HttpStatusCode status)
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(status)
        };
        var config = new ConfigurationBuilder().Build();
        var client = new HttpAuthRentalClient(new HttpClient(mockHandler), config, NullLogger<HttpAuthRentalClient>.Instance);

        var result = await client.GetCurrentUserAsync();
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("Checkout.DependencyUnavailable", result.Error.Code);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"id":123,"isProfileCompleted":true}""")]
    [InlineData("""{"id":null,"isProfileCompleted":true}""")]
    [InlineData("""{"id":{},"isProfileCompleted":true}""")]
    [InlineData("""{"id":[],"isProfileCompleted":true}""")]
    [InlineData("""{"id":true,"isProfileCompleted":true}""")]
    [InlineData("""{"id":"not-a-guid","isProfileCompleted":true}""")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000000","isProfileCompleted":true}""")]
    [InlineData("""{"id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"isProfileCompleted":true}""")]
    public async Task HttpAuthRentalClient_MalformedOrMissingFields_ReturnsDependencyUnavailable(string json)
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            }
        };
        var config = new ConfigurationBuilder().Build();
        var client = new HttpAuthRentalClient(new HttpClient(mockHandler), config, NullLogger<HttpAuthRentalClient>.Instance);

        var result = await client.GetCurrentUserAsync();
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("Checkout.DependencyUnavailable", result.Error.Code);
    }

    [Fact]
    public async Task HttpAuthRentalClient_Timeout_ReturnsDependencyUnavailable()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => throw new TimeoutException("Simulated timeout")
        };
        var config = new ConfigurationBuilder().Build();
        var client = new HttpAuthRentalClient(new HttpClient(mockHandler), config, NullLogger<HttpAuthRentalClient>.Instance);

        var result = await client.GetCurrentUserAsync();
        Assert.True(result.IsFailure);
        Assert.Equal("Checkout.DependencyUnavailable", result.Error.Code);
    }

    [Fact]
    public async Task HttpAuthRentalClient_CallerCancellation_RethrowsOperationCanceled()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        };
        var config = new ConfigurationBuilder().Build();
        var client = new HttpAuthRentalClient(new HttpClient(mockHandler), config, NullLogger<HttpAuthRentalClient>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCurrentUserAsync(cts.Token));
    }
}
