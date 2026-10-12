using System.Net;
using System.Text.Json;
using Dprental.RentalService.Api.Clients;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
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
    public async Task AuthServiceTokenProvider_CallsServiceTokenEndpoint_WithAudienceAndScope_AndCachesToken()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("http://auth-service:8080/api/v1/auth/service-token", req.RequestUri!.ToString());

                string body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("test-client", doc.RootElement.GetProperty("clientId").GetString());
                Assert.Equal("test-secret", doc.RootElement.GetProperty("clientSecret").GetString());
                Assert.Equal("inventory-api", doc.RootElement.GetProperty("audience").GetString());
                Assert.Equal("inventory.allocation.write", doc.RootElement.GetProperty("scope").GetString());

                var response = new
                {
                    accessToken = "jwt-mock-inventory-token",
                    expiresIn = 3600
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(response), System.Text.Encoding.UTF8, "application/json")
                };
            }
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Services:Auth:BaseUrl", "http://auth-service:8080"),
                new KeyValuePair<string, string?>("Authentication:ServiceCredentials:ClientId", "test-client"),
                new KeyValuePair<string, string?>("Authentication:ServiceCredentials:ClientSecret", "test-secret")
            })
            .Build();

        var httpClient = new HttpClient(mockHandler);
        var provider = new AuthServiceTokenProvider(httpClient, config, NullLogger<AuthServiceTokenProvider>.Instance);

        // First call fetches from auth service
        var result1 = await provider.GetServiceTokenAsync("inventory-api", "inventory.allocation.write");
        Assert.True(result1.IsSuccess);
        Assert.Equal("jwt-mock-inventory-token", result1.Value);
        Assert.Single(mockHandler.SentRequests);

        // Second call with same audience and scope hits cache
        var result2 = await provider.GetServiceTokenAsync("inventory-api", "inventory.allocation.write");
        Assert.True(result2.IsSuccess);
        Assert.Equal("jwt-mock-inventory-token", result2.Value);
        Assert.Single(mockHandler.SentRequests); // No new HTTP call!
    }

    [Fact]
    public async Task AuthServiceTokenProvider_WhenCredentialsMissing_FailsClosed()
    {
        var mockHandler = new MockHttpMessageHandler();
        var emptyConfig = new ConfigurationBuilder().Build();
        var httpClient = new HttpClient(mockHandler);
        var provider = new AuthServiceTokenProvider(httpClient, emptyConfig, NullLogger<AuthServiceTokenProvider>.Instance);

        var result = await provider.GetServiceTokenAsync("payment-api", "payment.invoice.write");

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_CREDENTIALS_MISSING", result.Error.Code);
        Assert.Empty(mockHandler.SentRequests);
    }

    [Fact]
    public async Task HttpInventoryRentalClient_AppendsBaseUrlAndInjectsToken()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                Assert.Equal("mock-token", req.Headers.Authorization?.Parameter);
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("http://inventory-service:8080/api/v1/inventory/allocation-groups/grp-123/extend-hold", req.RequestUri!.ToString());

                var resp = new ExtendHoldResponse("REC-1", "grp-123", DateTimeOffset.UtcNow.AddHours(24), DateTimeOffset.UtcNow, false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resp), System.Text.Encoding.UTF8, "application/json")
                };
            }
        };

        var tokenProvider = new StubTokenProvider("mock-token");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Services:Inventory:BaseUrl", "http://inventory-service:8080")
            })
            .Build();

        var httpClient = new HttpClient(mockHandler);
        var client = new HttpInventoryRentalClient(httpClient, tokenProvider, config, NullLogger<HttpInventoryRentalClient>.Instance);

        var req = new ExtendHoldRequest(DateTimeOffset.UtcNow.AddHours(24), "op-key-1");
        var result = await client.ExtendHoldAsync("grp-123", req);

        Assert.True(result.IsSuccess);
        Assert.Equal("REC-1", result.Value.ReceiptRef);
    }

    [Fact]
    public async Task HttpPaymentRentalClient_InjectsConfiguredAudienceAndBearerToken()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                Assert.Equal("mock-pay-token", req.Headers.Authorization?.Parameter);
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("http://payment-service:8080/api/v1/invoices/INV-001/cancel", req.RequestUri!.ToString());

                var resp = new CancelInvoiceResponse("INV-001", true, DateTimeOffset.UtcNow, false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resp), System.Text.Encoding.UTF8, "application/json")
                };
            }
        };

        var tokenProvider = new StubTokenProvider("mock-pay-token");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Services:Payment:BaseUrl", "http://payment-service:8080"),
                new KeyValuePair<string, string?>("Services:Payment:Audience", "payment-api")
            })
            .Build();

        var httpClient = new HttpClient(mockHandler);
        var client = new HttpPaymentRentalClient(httpClient, tokenProvider, config, NullLogger<HttpPaymentRentalClient>.Instance);

        var req = new CancelInvoiceRequest(Guid.NewGuid(), "INV-001", "Timeout", "op-cancel-1");
        var result = await client.CancelInvoiceAsync(req);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Cancelled);
    }

    private sealed class StubTokenProvider : IServiceTokenProvider
    {
        private readonly string _token;
        public StubTokenProvider(string token) => _token = token;
        public Task<Result<string>> GetServiceTokenAsync(string audience, string scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(_token));
    }
}
