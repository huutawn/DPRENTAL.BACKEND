using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Dprental.CatalogService.Api.Tests;

public class CatalogWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string DefaultInternalApiKey = "dev-internal-api-key-dprental-2026";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalAuthentication:ApiKey"] = DefaultInternalApiKey
            }));
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", DefaultInternalApiKey);
    }
}
