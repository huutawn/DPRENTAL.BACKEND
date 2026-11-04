using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dprental.InventoryService.Application.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Clients;

public sealed class SupplierOrganizationLookupTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    [Fact]
    public async Task ReadsOrganizationFromAuthenticatedInternalEndpoint()
    {
        var owner = Guid.NewGuid();
        var organization = Guid.NewGuid();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal($"/api/v1/auth/internal/supplier-organizations/by-owner/{owner:D}", request.RequestUri!.AbsolutePath);
            Assert.True(request.Headers.Contains("X-Inventory-Lookup-Key"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"organizationId\":\"{organization:D}\"}}") };
        })) { BaseAddress = new Uri("http://auth/") };
        http.DefaultRequestHeaders.Add("X-Inventory-Lookup-Key", "test-key");
        var client = new SupplierOrganizationLookup(http, NullLogger<SupplierOrganizationLookup>.Instance);
        Assert.Equal(organization, await client.FindByOwnerAsync(owner));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task MissingAmbiguousOrUnavailableOrganizationDoesNotBreakListing(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(status))) { BaseAddress = new Uri("http://auth/") };
        http.DefaultRequestHeaders.Add("X-Inventory-Lookup-Key", "test-key");
        var client = new SupplierOrganizationLookup(http, NullLogger<SupplierOrganizationLookup>.Instance);
        Assert.Null(await client.FindByOwnerAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task MissingKeyDoesNotSendRequest()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException())) { BaseAddress = new Uri("http://auth/") };
        var client = new SupplierOrganizationLookup(http, NullLogger<SupplierOrganizationLookup>.Instance);
        Assert.Null(await client.FindByOwnerAsync(Guid.NewGuid()));
    }
}
