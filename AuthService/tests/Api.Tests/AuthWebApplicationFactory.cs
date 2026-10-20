using System.Net.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dprental.AuthService.Api.Tests;

public class AuthWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string DefaultInternalApiKey = "dev-internal-api-key-dprental-2026";

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", DefaultInternalApiKey);
    }
}
