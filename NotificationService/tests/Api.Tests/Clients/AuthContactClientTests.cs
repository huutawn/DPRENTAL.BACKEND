using System.Net;
using System.Net.Http.Json;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Clients;

public sealed class AuthContactClientTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        private readonly byte[] _content;
        public bool Disposed { get; private set; }

        public TrackingContent(string json)
        {
            _content = System.Text.Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(_content, 0, _content.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _content.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task AuthContactClient_SendsXApiKeyHeader_WithConfiguredValue()
    {
        HttpRequestMessage? interceptedRequest = null;
        var handler = new MockHttpMessageHandler((req, _) =>
        {
            interceptedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AuthContactBatchResponse(Array.Empty<AuthContactDto>()))
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalAuthentication:ApiKey"] = "custom-prod-internal-key-2026"
            })
            .Build();

        var client = new AuthContactClient(httpClient, config, NullLogger<AuthContactClient>.Instance);

        var result = await client.GetUserContactsBatchAsync(new[] { Guid.NewGuid() });

        Assert.True(result.IsSuccess);
        Assert.NotNull(interceptedRequest);
        Assert.Equal(HttpMethod.Post, interceptedRequest.Method);
        Assert.Equal("/api/v1/auth/internal/users/notification-contacts:batch", interceptedRequest.RequestUri?.AbsolutePath);
        Assert.True(interceptedRequest.Headers.Contains("X-Api-Key"));
        Assert.Equal("custom-prod-internal-key-2026", interceptedRequest.Headers.GetValues("X-Api-Key").Single());
        Assert.False(interceptedRequest.Headers.Contains("X-Internal-Api-Key"));
    }

    [Fact]
    public async Task AuthContactClient_SendsXApiKeyHeader_WithDefaultFallbackValue()
    {
        HttpRequestMessage? interceptedRequest = null;
        var handler = new MockHttpMessageHandler((req, _) =>
        {
            interceptedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AuthContactBatchResponse(Array.Empty<AuthContactDto>()))
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var emptyConfig = new ConfigurationBuilder().Build();

        var client = new AuthContactClient(httpClient, emptyConfig, NullLogger<AuthContactClient>.Instance);

        var result = await client.GetUserContactsBatchAsync(new[] { Guid.NewGuid() });

        Assert.True(result.IsSuccess);
        Assert.NotNull(interceptedRequest);
        Assert.True(interceptedRequest.Headers.Contains("X-Api-Key"));
        Assert.Equal("dev-internal-api-key-dprental-2026", interceptedRequest.Headers.GetValues("X-Api-Key").Single());
        Assert.False(interceptedRequest.Headers.Contains("X-Internal-Api-Key"));
    }

    [Fact]
    public void AuthContactClient_SetsFiniteTimeoutOf10Seconds()
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var httpClient = new HttpClient(handler);
        var config = new ConfigurationBuilder().Build();

        _ = new AuthContactClient(httpClient, config, NullLogger<AuthContactClient>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(10), httpClient.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), AuthContactClient.RequestTimeout);
    }

    [Fact]
    public async Task AuthContactClient_EmptyOrNullUserIds_ReturnsSuccessfulEmptyListImmediately()
    {
        var callCount = 0;
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var client = new AuthContactClient(httpClient, new ConfigurationBuilder().Build(), NullLogger<AuthContactClient>.Instance);

        var resultEmpty = await client.GetUserContactsBatchAsync(Array.Empty<Guid>());
        var resultNull = await client.GetUserContactsBatchAsync(null!);

        Assert.True(resultEmpty.IsSuccess);
        Assert.Empty(resultEmpty.Value);
        Assert.True(resultNull.IsSuccess);
        Assert.Empty(resultNull.Value);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task AuthContactClient_SuccessfulResponseWithContacts_ReturnsContacts()
    {
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var contacts = new[]
        {
            new AuthContactDto(userId1, "user1@example.com", IsActive: true, IsEmailVerified: true),
            new AuthContactDto(userId2, "user2@example.com", IsActive: false, IsEmailVerified: false)
        };

        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AuthContactBatchResponse(contacts))
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var client = new AuthContactClient(httpClient, new ConfigurationBuilder().Build(), NullLogger<AuthContactClient>.Instance);

        var result = await client.GetUserContactsBatchAsync(new[] { userId1, userId2 });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(userId1, result.Value[0].UserId);
        Assert.Equal("user1@example.com", result.Value[0].Email);
        Assert.True(result.Value[0].IsActive);
        Assert.True(result.Value[0].IsEmailVerified);
    }

    [Fact]
    public async Task AuthContactClient_SuccessfulResponseWithEmptyContacts_ReturnsEmptyListAsSuccess()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AuthContactBatchResponse(Array.Empty<AuthContactDto>()))
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var client = new AuthContactClient(httpClient, new ConfigurationBuilder().Build(), NullLogger<AuthContactClient>.Instance);

        var result = await client.GetUserContactsBatchAsync(new[] { Guid.NewGuid() });

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task AuthContactClient_Server5xx_ReturnsDistinguished5xxFailure(HttpStatusCode statusCode)
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)));

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var client = new AuthContactClient(httpClient, new ConfigurationBuilder().Build(), NullLogger<AuthContactClient>.Instance);

        var result = await client.GetUserContactsBatchAsync(new[] { Guid.NewGuid() });

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_SERVICE_5XX", result.Error.Code);
        Assert.Equal(ErrorType.Failure, result.Error.Type);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AuthContactClient_Client4xx_ReturnsDistinguished4xxFailure(HttpStatusCode statusCode)
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)));

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var client = new AuthContactClient(httpClient, new ConfigurationBuilder().Build(), NullLogger<AuthContactClient>.Instance);

        var result = await client.GetUserContactsBatchAsync(new[] { Guid.NewGuid() });

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_SERVICE_4XX", result.Error.Code);
        Assert.Equal(ErrorType.Failure, result.Error.Type);
    }

    [Fact]
    public async Task AuthContactClient_NetworkFailure_ReturnsDistinguishedNetworkFailure()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            throw new HttpRequestException("DNS resolution failed or connection refused"));

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var client = new AuthContactClient(httpClient, new ConfigurationBuilder().Build(), NullLogger<AuthContactClient>.Instance);

        var result = await client.GetUserContactsBatchAsync(new[] { Guid.NewGuid() });

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_NETWORK_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task AuthContactClient_Timeout_ReturnsDistinguishedTimeoutFailure()
    {
        var handler = new MockHttpMessageHandler((_, _) =>
            throw new TimeoutException("The HTTP request timed out."));

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var client = new AuthContactClient(httpClient, new ConfigurationBuilder().Build(), NullLogger<AuthContactClient>.Instance);

        var result = await client.GetUserContactsBatchAsync(new[] { Guid.NewGuid() });

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_NETWORK_TIMEOUT", result.Error.Code);
    }

    [Fact]
    public async Task AuthContactClient_DisposesHttpResponseMessage()
    {
        TrackingContent? trackingContent = null;
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            trackingContent = new TrackingContent("{\"contacts\":[]}");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = trackingContent
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://auth-service:8080") };
        var client = new AuthContactClient(httpClient, new ConfigurationBuilder().Build(), NullLogger<AuthContactClient>.Instance);

        var result = await client.GetUserContactsBatchAsync(new[] { Guid.NewGuid() });

        Assert.True(result.IsSuccess);
        Assert.NotNull(trackingContent);
        Assert.True(trackingContent.Disposed, "HttpResponseMessage and its content MUST be disposed properly.");
    }
}
