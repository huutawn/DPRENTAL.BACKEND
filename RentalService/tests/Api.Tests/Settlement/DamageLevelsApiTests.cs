using System.Net;
using System.Net.Http.Json;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Settlement;

public sealed class DamageLevelsApiTests
{
    private const string Route = "/api/v1/rental/damage-levels";

    [Fact]
    public async Task AuthenticatedUser_GetsSeededLevelsInOrder()
    {
        using OrderApiFactory factory = new();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>().Database.EnsureCreated();
        }

        using HttpClient client = factory.CreateClientFor(factory.SupplierBinh);
        var levels = (await client.GetFromJsonAsync<List<DamageLevelDto>>(Route))!;

        Assert.Equal([0, 1, 2, 3, 4], levels.Select(l => l.Level));
        Assert.Equal([0, 10, 30, 50, 100], levels.Select(l => l.Percent));
        Assert.All(levels, l => Assert.False(string.IsNullOrWhiteSpace(l.Name)));
    }

    [Fact]
    public async Task AnonymousCaller_IsRejected()
    {
        using OrderApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
