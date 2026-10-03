using System.Net;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Api.Clients;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Common;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Media;

/// <summary>
/// Unit tests cho MediaServiceClient: xác thực HTTP method, headers, request body, status code mapping và error handling.
/// </summary>
public sealed class MediaServiceClientTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handlerFunc(request, cancellationToken);
        }
    }

    private static (MediaServiceClient client, MediaServiceOptions options) CreateTestClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc,
        string apiKey = "test-secret-api-key")
    {
        var options = new MediaServiceOptions
        {
            InternalBaseUrl = "http://media-service:8080",
            PublicGatewayUrl = "http://localhost:5050",
            ApiKey = apiKey,
            TimeoutSeconds = 10
        };

        var handler = new MockHttpMessageHandler(handlerFunc);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.InternalBaseUrl.TrimEnd('/') + "/")
        };

        var optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        return (new MediaServiceClient(httpClient, optionsWrapper), options);
    }

    [Fact]
    public async Task VerifyAsync_Success200_SendsCorrectMethodPathAndHeader_ReturnsParsedInfo()
    {
        var mediaId = Guid.NewGuid();
        var uploaderId = Guid.NewGuid().ToString("D");
        HttpRequestMessage? capturedRequest = null;

        var (client, options) = CreateTestClient(async (req, ct) =>
        {
            capturedRequest = req;
            var responseJson = JsonSerializer.Serialize(new
            {
                mediaId = mediaId,
                status = "Ready",
                uploadedByUserId = uploaderId
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var result = await client.VerifyAsync(mediaId);

        Assert.True(result.IsSuccess);
        Assert.Equal(mediaId, result.Value.MediaId);
        Assert.Equal("Ready", result.Value.Status);
        Assert.Equal(uploaderId, result.Value.UploadedByUserId);

        // Kiểm tra request HTTP
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest.Method);
        Assert.Equal($"http://media-service:8080/api/v1/media/{mediaId:D}/verify", capturedRequest.RequestUri?.ToString());
        Assert.True(capturedRequest.Headers.Contains("X-Api-Key"));
        Assert.Equal(options.ApiKey, capturedRequest.Headers.GetValues("X-Api-Key").Single());
        Assert.False(capturedRequest.Headers.Contains("Authorization")); // Không forward token user
    }

    [Fact]
    public async Task SetPublicAsync_Success200_SendsCorrectBodyAndHeader_ReturnsSuccess()
    {
        var mediaId = Guid.NewGuid();
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var (client, options) = CreateTestClient(async (req, ct) =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);

            var responseJson = JsonSerializer.Serialize(new
            {
                mediaId = mediaId,
                visibility = "Public"
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var result = await client.SetPublicAsync(mediaId);

        Assert.True(result.IsSuccess);
        Assert.Equal(mediaId, result.Value.MediaId);
        Assert.Equal("Public", result.Value.Visibility);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Put, capturedRequest.Method);
        Assert.Equal($"http://media-service:8080/api/v1/media/{mediaId:D}/visibility", capturedRequest.RequestUri?.ToString());
        Assert.Equal(options.ApiKey, capturedRequest.Headers.GetValues("X-Api-Key").Single());

        // Body chính xác { "visibility": "public" }
        Assert.NotNull(capturedBody);
        var jsonDoc = JsonDocument.Parse(capturedBody);
        Assert.Equal("public", jsonDoc.RootElement.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task SetPublicAsync_Response202Accepted_RejectedAsUnavailable()
    {
        var mediaId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });

        var result = await client.SetPublicAsync(mediaId);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
    }

    [Fact]
    public async Task SetPublicAsync_ResponseDifferentIdOrNotPublic_RejectedAsUnavailable()
    {
        var mediaId = Guid.NewGuid();

        // 1. Khác ID
        var (clientDiffId, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new { mediaId = Guid.NewGuid(), visibility = "Public" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var resDiffId = await clientDiffId.SetPublicAsync(mediaId);
        Assert.True(resDiffId.IsFailure);
        Assert.Equal(ErrorType.Unavailable, resDiffId.Error.Type);

        // 2. Visibility không phải Public
        var (clientNotPublic, _) = CreateTestClient((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new { mediaId = mediaId, visibility = "Private" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var resNotPublic = await clientNotPublic.SetPublicAsync(mediaId);
        Assert.True(resNotPublic.IsFailure);
        Assert.Equal(ErrorType.Unavailable, resNotPublic.Error.Type);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ErrorType.Validation)]
    [InlineData(HttpStatusCode.BadRequest, ErrorType.Validation)]
    [InlineData(HttpStatusCode.Conflict, ErrorType.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorType.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, ErrorType.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorType.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorType.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorType.Unavailable)]
    public async Task StatusCodes_MapCorrectlyToErrorTypes(HttpStatusCode upstreamStatus, ErrorType expectedErrorType)
    {
        var mediaId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(upstreamStatus));
        });

        var verifyRes = await client.VerifyAsync(mediaId);
        Assert.True(verifyRes.IsFailure);
        Assert.Equal(expectedErrorType, verifyRes.Error.Type);

        var pubRes = await client.SetPublicAsync(mediaId);
        Assert.True(pubRes.IsFailure);
        Assert.Equal(expectedErrorType, pubRes.Error.Type);
    }

    [Fact]
    public async Task VerifyAsync_EmptyOrMalformedJson_ReturnsUnavailable()
    {
        var mediaId = Guid.NewGuid();

        // Rỗng
        var (clientEmpty, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "application/json") }));
        var resEmpty = await clientEmpty.VerifyAsync(mediaId);
        Assert.True(resEmpty.IsFailure);
        Assert.Equal(ErrorType.Unavailable, resEmpty.Error.Type);

        // JSON lỗi cú pháp
        var (clientMalformed, _) = CreateTestClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid-json{", Encoding.UTF8, "application/json") }));
        var resMalformed = await clientMalformed.VerifyAsync(mediaId);
        Assert.True(resMalformed.IsFailure);
        Assert.Equal(ErrorType.Unavailable, resMalformed.Error.Type);
    }

    [Fact]
    public async Task Timeout_MapsToUnavailable()
    {
        var mediaId = Guid.NewGuid();
        var (client, _) = CreateTestClient((req, ct) =>
            throw new TaskCanceledException("Timeout simulated"));

        var result = await client.VerifyAsync(mediaId);
        Assert.True(result.IsFailure);
        Assert.Equal("MEDIA_SERVICE_TIMEOUT", result.Error.Code);
        Assert.Equal(ErrorType.Unavailable, result.Error.Type);
    }

    [Fact]
    public async Task Cancellation_PropagatesException()
    {
        var mediaId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateTestClient((req, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.VerifyAsync(mediaId, cts.Token));
    }
}
