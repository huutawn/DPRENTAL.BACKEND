using System.Net;
using System.Text;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Clients;

public sealed class MediaClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Services:Media:BaseUrl"] = "https://media.test",
        ["MediaService:ApiKey"] = "test-key"
    }).Build();

    [Fact]
    public async Task Verify_UsesFrozenRouteAndApiKey()
    {
        var id = Guid.NewGuid();
        using var handler = new Handler((request, ct) =>
        {
            Assert.Equal($"https://media.test/api/v1/media/{id}/verify", request.RequestUri!.ToString());
            Assert.Equal("test-key", request.Headers.GetValues("X-Api-Key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"mediaId":"{{id}}","uploadedByUserId":"{{Guid.NewGuid()}}","status":"Ready"}""", Encoding.UTF8, "application/json")
            });
        });
        using var http = new HttpClient(handler);
        var result = await new HttpRentalMediaClient(http, Configuration()).VerifyMediaAsync(id);
        Assert.True(result.IsSuccess);
        Assert.Equal(id, result.Value.MediaId);
    }

    [Theory]
    [InlineData(404, ErrorType.NotFound)]
    [InlineData(401, ErrorType.ServiceUnavailable)]
    [InlineData(500, ErrorType.ServiceUnavailable)]
    [InlineData(200, ErrorType.ServiceUnavailable)]
    public async Task BadResponse_DoesNotLeakUpstreamBody(int status, ErrorType errorType)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("upstream-secret", Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler);
        var result = await new HttpRentalMediaClient(http, Configuration()).VerifyMediaAsync(Guid.NewGuid());
        Assert.Equal(errorType, result.Error.Type);
        Assert.DoesNotContain("upstream-secret", result.Error.Message);
    }

    [Fact]
    public async Task CallerCancellation_IsPropagated()
    {
        using var handler = new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpRentalMediaClient(http, Configuration()).VerifyMediaAsync(Guid.NewGuid(), cts.Token));
    }
}
