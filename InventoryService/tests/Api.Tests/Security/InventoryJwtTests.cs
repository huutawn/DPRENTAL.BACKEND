using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Security;

public sealed class InventoryJwtTests
{
    [Fact]
    public async Task MissingToken_Returns401Unauthorized()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/test/availability/sanitized");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_Returns401Unauthorized()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        string expiredToken = factory.CreateToken(
            Guid.NewGuid().ToString("D"),
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidSignature_Returns401Unauthorized()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        using var rogueKey = RSA.Create(2048);
        string rogueToken = factory.CreateToken(
            Guid.NewGuid().ToString("D"),
            customKey: rogueKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rogueToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongAudience_Returns401Unauthorized()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        string wrongAudienceToken = factory.CreateToken(
            Guid.NewGuid().ToString("D"),
            audience: "untrusted-audience");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wrongAudienceToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedSub_NonGuid_Returns401Unauthorized()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        string malformedSubToken = factory.CreateToken("not-a-valid-guid");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", malformedSubToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedSub_EmptyGuid_Returns401Unauthorized()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        string emptyGuidSubToken = factory.CreateToken(Guid.Empty.ToString("D"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", emptyGuidSubToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidHumanToken_SucceedsAuthentication()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        string validToken = factory.CreateHumanToken(Guid.NewGuid());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validToken);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownKeyId_TriggersJwksRefresh()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var client = factory.CreateClient();

        // Initial request để tải cache JWKS
        string token1 = factory.CreateHumanToken(Guid.NewGuid());
        using var request1 = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        request1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token1);
        var resp1 = await client.SendAsync(request1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        int initialRequests = factory.JwksRequestCount;

        // Token với kid chưa biết
        string tokenUnknownKid = factory.CreateToken(
            Guid.NewGuid().ToString("D"),
            keyId: "key-unknown-rotating");

        using var request2 = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        request2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUnknownKid);
        await client.SendAsync(request2);

        // Yêu cầu tiếp theo sẽ thực sự thực hiện reload JWKS từ endpoint
        using var request3 = new HttpRequestMessage(HttpMethod.Get, "/api/test/availability/sanitized");
        request3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token1);
        await client.SendAsync(request3);

        Assert.True(factory.JwksRequestCount > initialRequests);
    }
}
