using System.Net;
using System.Text;
using Dprental.CatalogService.Api.Clients;
using Dprental.CatalogService.Api.Observability;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.IServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class MediaVerificationClientTests
{
    private const string CorrelationId = "corr-123";

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"mediaId":"7b0d3c56-5f0a-4b53-9d0e-0c1f3a1b2c3d","status":"Ready","originalFileName":"may-xuc.jpg"}""", MediaVerificationStatus.Ready)]
    [InlineData(HttpStatusCode.OK, """{"status":"ready"}""", MediaVerificationStatus.Ready)]
    [InlineData(HttpStatusCode.OK, """{"status":"PendingUpload"}""", MediaVerificationStatus.NotReady)]
    [InlineData(HttpStatusCode.OK, """{"mediaId":"7b0d3c56-5f0a-4b53-9d0e-0c1f3a1b2c3d"}""", MediaVerificationStatus.Unavailable)]
    [InlineData(HttpStatusCode.OK, "null", MediaVerificationStatus.Unavailable)]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>", MediaVerificationStatus.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, """{"title":"MediaNotFound"}""", MediaVerificationStatus.NotFound)]
    [InlineData(HttpStatusCode.Conflict, """{"title":"MediaNotReady"}""", MediaVerificationStatus.NotReady)]
    [InlineData(HttpStatusCode.BadRequest, """{"title":"InvalidMediaId"}""", MediaVerificationStatus.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, """{"title":"Unauthorized"}""", MediaVerificationStatus.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, "", MediaVerificationStatus.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "", MediaVerificationStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", MediaVerificationStatus.Unavailable)]
    public async Task VerifyAsync_MapsResponseToStatus(HttpStatusCode statusCode, string body, MediaVerificationStatus expected)
    {
        using StubHandler handler = new(_ => Task.FromResult(JsonResponse(statusCode, body)));
        using HttpClient httpClient = CreateHttpClient(handler);
        MediaVerificationClient client = CreateClient(httpClient);

        MediaVerificationStatus status = await client.VerifyAsync(Guid.NewGuid());

        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task VerifyAsync_ConnectionFailure_ReturnsUnavailable()
    {
        using StubHandler handler = new(_ => throw new HttpRequestException("connection refused"));
        using HttpClient httpClient = CreateHttpClient(handler);
        MediaVerificationClient client = CreateClient(httpClient);

        MediaVerificationStatus status = await client.VerifyAsync(Guid.NewGuid());

        Assert.Equal(MediaVerificationStatus.Unavailable, status);
    }

    [Fact]
    public async Task VerifyAsync_Timeout_ReturnsUnavailable()
    {
        using StubHandler handler = new(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(HttpStatusCode.OK, """{"status":"Ready"}""");
        });
        using HttpClient httpClient = CreateHttpClient(handler);
        httpClient.Timeout = TimeSpan.FromMilliseconds(100);
        MediaVerificationClient client = CreateClient(httpClient);

        MediaVerificationStatus status = await client.VerifyAsync(Guid.NewGuid());

        Assert.Equal(MediaVerificationStatus.Unavailable, status);
    }

    [Fact]
    public async Task VerifyAsync_CallerCancellation_PropagatesInsteadOfReportingUnavailable()
    {
        using StubHandler handler = new(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(HttpStatusCode.OK, """{"status":"Ready"}""");
        });
        using HttpClient httpClient = CreateHttpClient(handler);
        MediaVerificationClient client = CreateClient(httpClient);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.VerifyAsync(Guid.NewGuid(), cancellation.Token));
    }

    [Fact]
    public async Task VerifyAsync_SendsVerifyRequestWithCorrelationId()
    {
        Guid mediaId = Guid.NewGuid();
        using StubHandler handler = new(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"status":"Ready"}""")));
        using HttpClient httpClient = CreateHttpClient(handler);
        MediaVerificationClient client = CreateClient(httpClient);

        await client.VerifyAsync(mediaId);

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(new Uri($"http://media.test/api/v1/media/{mediaId}/verify"), handler.RequestUri);
        Assert.Equal(CorrelationId, Assert.Single(handler.Headers[CorrelationIdProvider.HeaderName]));
    }

    [Theory]
    [InlineData("http://media.test", "http://media.test/api/v1/media/")]
    [InlineData("http://media.test/", "http://media.test/api/v1/media/")]
    [InlineData("https://gateway.test/media-svc", "https://gateway.test/media-svc/api/v1/media/")]
    [InlineData("https://gateway.test/media-svc/", "https://gateway.test/media-svc/api/v1/media/")]
    public async Task Registration_BuildsClientFromConfiguration(string baseUrl, string expectedPrefix)
    {
        Guid mediaId = Guid.NewGuid();
        using StubHandler handler = new(_ => Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"status":"Ready"}""")));
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<ICorrelationIdProvider>(new FixedCorrelationIdProvider());
        services.AddMediaVerificationClient(BuildConfiguration(baseUrl, "secret-key", "7"));
        services.AddHttpClient<IMediaVerificationClient, MediaVerificationClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using ServiceProvider provider = services.BuildServiceProvider();

        MediaVerificationStatus status = await provider.GetRequiredService<IMediaVerificationClient>().VerifyAsync(mediaId);

        Assert.Equal(MediaVerificationStatus.Ready, status);
        Assert.Equal(new Uri($"{expectedPrefix}{mediaId}/verify"), handler.RequestUri);
        Assert.Equal("secret-key", Assert.Single(handler.Headers[MediaVerificationClient.ApiKeyHeaderName]));
    }

    [Theory]
    [InlineData("", "secret-key", "5")]
    [InlineData("not-a-url", "secret-key", "5")]
    [InlineData("ftp://media.test", "secret-key", "5")]
    [InlineData("http://media.test", "", "5")]
    [InlineData("http://media.test", "   ", "5")]
    [InlineData("http://media.test", "secret-key", "0")]
    [InlineData("http://media.test", "secret-key", "61")]
    public void Registration_InvalidConfiguration_FailsFast(string baseUrl, string apiKey, string timeoutSeconds)
    {
        ServiceCollection services = new();

        Assert.Throws<OptionsValidationException>(
            () => services.AddMediaVerificationClient(BuildConfiguration(baseUrl, apiKey, timeoutSeconds)));
    }

    [Fact]
    public void Registration_MissingSection_FailsFast()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.AddMediaVerificationClient(configuration));

        Assert.Contains(exception.Failures, failure => failure.Contains("MediaService:BaseUrl", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("MediaService:ApiKey", StringComparison.Ordinal));
    }

    private static IConfiguration BuildConfiguration(string baseUrl, string apiKey, string timeoutSeconds) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MediaService:BaseUrl"] = baseUrl,
                ["MediaService:ApiKey"] = apiKey,
                ["MediaService:TimeoutSeconds"] = timeoutSeconds
            })
            .Build();

    private static HttpClient CreateHttpClient(StubHandler handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("http://media.test/") };

    private static MediaVerificationClient CreateClient(HttpClient httpClient) =>
        new(httpClient, new FixedCorrelationIdProvider(), NullLogger<MediaVerificationClient>.Instance);

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FixedCorrelationIdProvider : ICorrelationIdProvider
    {
        public string GetCorrelationId() => CorrelationId;
    }

    /// <summary>Ghi lại request gửi đi và trả phản hồi do test quyết định, thay cho Media Service thật.</summary>
    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public Dictionary<string, string[]> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                Headers[header.Key] = header.Value.ToArray();
            }

            return respond(cancellationToken);
        }
    }
}
