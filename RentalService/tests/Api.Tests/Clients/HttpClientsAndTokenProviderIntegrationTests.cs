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
        var result = await client.GetListingRentalSnapshotAsync(Guid.NewGuid());
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
}
