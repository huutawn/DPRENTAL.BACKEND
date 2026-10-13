using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Controllers;

public sealed class SwaggerDocumentationTests
{
    [Fact]
    public async Task Swagger_DocumentsCanonicalOperationsWithoutCartAliases()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        string[] methods = ["get", "post", "patch", "delete"];
        int operationCount = 0;
        foreach (var path in paths.EnumerateObject())
        {
            Assert.StartsWith("/api/v1/rental/", path.Name);
            foreach (var operation in path.Value.EnumerateObject().Where(p => methods.Contains(p.Name)))
            {
                operationCount++;
                Assert.False(string.IsNullOrWhiteSpace(operation.Value.GetProperty("summary").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(operation.Value.GetProperty("description").GetString()));
                Assert.True(operation.Value.GetProperty("responses").TryGetProperty("500", out _));
            }
        }
        Assert.Equal(11, operationCount);
        Assert.False(paths.TryGetProperty("/rental/cart", out _));
        Assert.False(paths.TryGetProperty("/rental/checkout", out _));
        var checkoutResponses = paths.GetProperty("/api/v1/rental/checkout").GetProperty("post").GetProperty("responses");
        Assert.True(checkoutResponses.TryGetProperty("201", out _));
        Assert.True(checkoutResponses.TryGetProperty("200", out _));
        Assert.True(checkoutResponses.TryGetProperty("503", out _));
        var cart = paths.GetProperty("/api/v1/rental/cart");
        Assert.True(cart.GetProperty("delete").GetProperty("responses").TryGetProperty("404", out _));
        var calendarResponses = paths.GetProperty("/api/v1/rental/me/calendar").GetProperty("get").GetProperty("responses");
        Assert.False(calendarResponses.TryGetProperty("404", out _));
        Assert.False(calendarResponses.TryGetProperty("409", out _));
    }
}
