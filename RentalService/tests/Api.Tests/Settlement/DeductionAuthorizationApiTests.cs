using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Settlement;

public sealed class DeductionAuthorizationApiTests
{
    [Fact]
    public async Task SettlementEndpointsRejectAnonymousCallers()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });
        using var client = factory.CreateClient();
        Guid itemId = Guid.NewGuid();
        using var response = await client.PostAsJsonAsync($"/api/v1/rental/items/{itemId}/deductions/response",
            new { decision = "ACCEPT" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var dispute = await client.GetAsync($"/api/v1/rental/items/{itemId}/deductions/dispute");
        Assert.Equal(HttpStatusCode.Unauthorized, dispute.StatusCode);
        using var resolve = await client.PostAsJsonAsync($"/api/v1/rental/items/{itemId}/deductions/dispute/resolution",
            new { finalDeductionAmount = 0, decisionNote = "Reviewed" });
        Assert.Equal(HttpStatusCode.Unauthorized, resolve.StatusCode);
        using var adminList = await client.GetAsync("/api/v1/rental/admin/disputes");
        Assert.Equal(HttpStatusCode.Unauthorized, adminList.StatusCode);
    }
}
