using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Media;

/// <summary>
/// Unit tests cho InventoryMediaService: xác thực luồng verify -> check ownership -> set public -> dựng URL ổn định.
/// </summary>
public sealed class InventoryMediaServiceTests
{
    private sealed class FakeMediaServiceClient : IMediaServiceClient
    {
        public Func<Guid, CancellationToken, Task<Result<MediaVerificationInfo>>>? VerifyHandler { get; set; }
        public Func<Guid, CancellationToken, Task<Result<MediaVisibilityInfo>>>? SetPublicHandler { get; set; }

        public int VerifyCallCount { get; private set; }
        public int SetPublicCallCount { get; private set; }

        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            VerifyCallCount++;
            return VerifyHandler is not null
                ? VerifyHandler(mediaId, cancellationToken)
                : Task.FromResult(Result.Failure<MediaVerificationInfo>(Error.Validation("DEFAULT", "Not configured")));
        }

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            SetPublicCallCount++;
            return SetPublicHandler is not null
                ? SetPublicHandler(mediaId, cancellationToken)
                : Task.FromResult(Result.Failure<MediaVisibilityInfo>(Error.Validation("DEFAULT", "Not configured")));
        }
    }

    private readonly FakeMediaServiceClient _mediaClient = new();
    private readonly MediaServiceOptions _options = new()
    {
        InternalBaseUrl = "http://media-service:8080",
        PublicGatewayUrl = "https://gateway.example",
        ApiKey = "secret-key",
        TimeoutSeconds = 10
    };

    private InventoryMediaService CreateService(MediaServiceOptions? customOptions = null)
    {
        var optionsWrapper = Microsoft.Extensions.Options.Options.Create(customOptions ?? _options);
        return new InventoryMediaService(_mediaClient, optionsWrapper);
    }

    private sealed class TestCaller(Guid? userId, bool isHuman = true, bool isAuthenticated = true, bool isAdmin = false) : IInventoryCallerContext
    {
        public bool IsAuthenticated => isAuthenticated;
        public bool IsService => !isHuman;
        public bool IsAdmin => isAdmin;
        public Guid? UserId => userId;
        public IReadOnlySet<string> Permissions => new HashSet<string>();
        public IReadOnlySet<string> Scopes => new HashSet<string>();
        public IReadOnlySet<string> Roles => new HashSet<string>();
        public Guid? OrganizationId => null;
    }

    [Fact]
    public async Task PreparePublicAsync_NullMediaId_ReturnsSuccessNull_WithoutCallingClient()
    {
        var sut = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await sut.PreparePublicAsync(null, caller);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(0, _mediaClient.VerifyCallCount);
        Assert.Equal(0, _mediaClient.SetPublicCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_EmptyGuid_ReturnsValidation_WithoutCallingClient()
    {
        var sut = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await sut.PreparePublicAsync(Guid.Empty, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_MEDIA_ID", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(0, _mediaClient.VerifyCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_UnauthenticatedOrServiceCaller_IsRejected()
    {
        var sut = CreateService();
        var mediaId = Guid.NewGuid();

        // 1. Chưa xác thực -> 401
        var unauthCaller = new TestCaller(Guid.NewGuid(), isAuthenticated: false);
        var unauthRes = await sut.PreparePublicAsync(mediaId, unauthCaller);
        Assert.True(unauthRes.IsFailure);
        Assert.Equal(ErrorType.Unauthorized, unauthRes.Error.Type);

        // 2. Service principal -> 403
        var serviceCaller = new TestCaller(Guid.NewGuid(), isHuman: false);
        var svcRes = await sut.PreparePublicAsync(mediaId, serviceCaller);
        Assert.True(svcRes.IsFailure);
        Assert.Equal(ErrorType.Forbidden, svcRes.Error.Type);

        // 3. UserId null -> 403
        var noUserCaller = new TestCaller(null, isHuman: true);
        var noUserRes = await sut.PreparePublicAsync(mediaId, noUserCaller);
        Assert.True(noUserRes.IsFailure);
        Assert.Equal(ErrorType.Forbidden, noUserRes.Error.Type);

        Assert.Equal(0, _mediaClient.VerifyCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_ValidUploaderAndReady_CallsPublishAndReturnsResponse()
    {
        var sut = CreateService();
        var mediaId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var caller = new TestCaller(callerId);

        _mediaClient.VerifyHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", callerId.ToString("D"))));

        _mediaClient.SetPublicHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(id, "Public")));

        var result = await sut.PreparePublicAsync(mediaId, caller);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(mediaId, result.Value.Id);
        Assert.Equal($"https://gateway.example/api/v1/media/{mediaId:D}/content", result.Value.Url);

        Assert.Equal(1, _mediaClient.VerifyCallCount);
        Assert.Equal(1, _mediaClient.SetPublicCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_TrailingSlashInGatewayUrl_NormalizedCleanly()
    {
        var customOptions = new MediaServiceOptions
        {
            InternalBaseUrl = "http://media-service:8080",
            PublicGatewayUrl = "https://gateway.example/", // Có trailing slash
            ApiKey = "secret-key",
            TimeoutSeconds = 10
        };
        var sut = CreateService(customOptions);
        var mediaId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var caller = new TestCaller(callerId);

        _mediaClient.VerifyHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", callerId.ToString("D"))));
        _mediaClient.SetPublicHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(id, "Public")));

        var result = await sut.PreparePublicAsync(mediaId, caller);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        // Không bị lặp dấu slash
        Assert.Equal($"https://gateway.example/api/v1/media/{mediaId:D}/content", result.Value.Url);
    }

    [Fact]
    public async Task PreparePublicAsync_VerifyFails_DoesNotCallPublish()
    {
        var sut = CreateService();
        var mediaId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var caller = new TestCaller(callerId);

        _mediaClient.VerifyHandler = (id, ct) =>
            Task.FromResult(Result.Failure<MediaVerificationInfo>(Error.Validation("MEDIA_NOT_FOUND", "Không tìm thấy media.")));

        var result = await sut.PreparePublicAsync(mediaId, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("MEDIA_NOT_FOUND", result.Error.Code);
        Assert.Equal(1, _mediaClient.VerifyCallCount);
        Assert.Equal(0, _mediaClient.SetPublicCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_StatusNotReady_ReturnsConflict_DoesNotCallPublish()
    {
        var sut = CreateService();
        var mediaId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var caller = new TestCaller(callerId);

        _mediaClient.VerifyHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "PendingUpload", callerId.ToString("D"))));

        var result = await sut.PreparePublicAsync(mediaId, caller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal("MEDIA_NOT_READY", result.Error.Code);
        Assert.Equal(1, _mediaClient.VerifyCallCount);
        Assert.Equal(0, _mediaClient.SetPublicCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_UploaderMismatch_ReturnsForbidden_EvenForAdmin()
    {
        var sut = CreateService();
        var mediaId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var anotherUploaderId = Guid.NewGuid();

        // Admin caller cố dùng media của người khác -> Cấm bypass
        var adminCaller = new TestCaller(callerId, isHuman: true, isAuthenticated: true, isAdmin: true);

        _mediaClient.VerifyHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", anotherUploaderId.ToString("D"))));

        var result = await sut.PreparePublicAsync(mediaId, adminCaller);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Equal("MEDIA_OWNERSHIP_MISMATCH", result.Error.Code);
        Assert.Equal(1, _mediaClient.VerifyCallCount);
        Assert.Equal(0, _mediaClient.SetPublicCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_PublishFails_ReturnsError()
    {
        var sut = CreateService();
        var mediaId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var caller = new TestCaller(callerId);

        _mediaClient.VerifyHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", callerId.ToString("D"))));

        _mediaClient.SetPublicHandler = (id, ct) =>
            Task.FromResult(Result.Failure<MediaVisibilityInfo>(Error.Unavailable("MEDIA_SERVICE_UNAVAILABLE", "Lỗi publish.")));

        var result = await sut.PreparePublicAsync(mediaId, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("MEDIA_SERVICE_UNAVAILABLE", result.Error.Code);
    }

    [Fact]
    public async Task PreparePublicAsync_AlreadyPublic_RetrySucceeds()
    {
        var sut = CreateService();
        var mediaId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var caller = new TestCaller(callerId);

        _mediaClient.VerifyHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", callerId.ToString("D"))));

        // MediaService SetPublic có tính idempotent (nếu đã Public vẫn trả 200 Public)
        _mediaClient.SetPublicHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(id, "Public")));

        var first = await sut.PreparePublicAsync(mediaId, caller);
        var second = await sut.PreparePublicAsync(mediaId, caller);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value?.Url, second.Value?.Url);
    }
}
