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
    public async Task PreparePublicAsync_EmptyListAndNullThumbnail_ReturnsEmptyList_WithoutCallingClient()
    {
        var sut = CreateService();
        var caller = new TestCaller(Guid.NewGuid());

        var result = await sut.PreparePublicAsync([], null, caller);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
        Assert.Equal(0, _mediaClient.VerifyCallCount);
        Assert.Equal(0, _mediaClient.SetPublicCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_EmptyGuidOrDuplicateOrLimitOrThumbnailOutsideList_ReturnsValidation_WithoutCallingClient()
    {
        var sut = CreateService();
        var caller = new TestCaller(Guid.NewGuid());
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // 1. Empty Guid in mediaIds
        var emptyRes = await sut.PreparePublicAsync([Guid.Empty], null, caller);
        Assert.True(emptyRes.IsFailure);
        Assert.Equal("INVALID_MEDIA_ID", emptyRes.Error.Code);

        // 2. Duplicate Guid in mediaIds
        var dupRes = await sut.PreparePublicAsync([id1, id1], null, caller);
        Assert.True(dupRes.IsFailure);
        Assert.Equal("DUPLICATE_MEDIA_ID", dupRes.Error.Code);

        // 3. Exceeding 20 items
        var twentyOne = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToList();
        var limitRes = await sut.PreparePublicAsync(twentyOne, null, caller);
        Assert.True(limitRes.IsFailure);
        Assert.Equal("MEDIA_LIMIT_EXCEEDED", limitRes.Error.Code);

        // 4. Thumbnail not in mediaIds
        var thumbOutsideRes = await sut.PreparePublicAsync([id1], id2, caller);
        Assert.True(thumbOutsideRes.IsFailure);
        Assert.Equal("INVALID_THUMBNAIL_MEDIA_ID", thumbOutsideRes.Error.Code);

        Assert.Equal(0, _mediaClient.VerifyCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_UnauthenticatedOrServiceCaller_IsRejected()
    {
        var sut = CreateService();
        var mediaId = Guid.NewGuid();

        // 1. Chưa xác thực -> 401
        var unauthCaller = new TestCaller(Guid.NewGuid(), isAuthenticated: false);
        var unauthRes = await sut.PreparePublicAsync([mediaId], mediaId, unauthCaller);
        Assert.True(unauthRes.IsFailure);
        Assert.Equal(ErrorType.Unauthorized, unauthRes.Error.Type);

        // 2. Service principal -> 403
        var serviceCaller = new TestCaller(Guid.NewGuid(), isHuman: false);
        var svcRes = await sut.PreparePublicAsync([mediaId], mediaId, serviceCaller);
        Assert.True(svcRes.IsFailure);
        Assert.Equal(ErrorType.Forbidden, svcRes.Error.Type);

        // 3. UserId null -> 403
        var noUserCaller = new TestCaller(null, isHuman: true);
        var noUserRes = await sut.PreparePublicAsync([mediaId], mediaId, noUserCaller);
        Assert.True(noUserRes.IsFailure);
        Assert.Equal(ErrorType.Forbidden, noUserRes.Error.Type);

        Assert.Equal(0, _mediaClient.VerifyCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_MultipleImagesAndVideos_PreservesOrderAndPublishesAll()
    {
        var sut = CreateService();
        var videoId = Guid.NewGuid();
        var imgId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var caller = new TestCaller(callerId);

        _mediaClient.VerifyHandler = (id, ct) =>
        {
            string contentType = id == videoId ? "video/mp4" : "image/webp";
            return Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", callerId.ToString("D"), contentType)));
        };

        _mediaClient.SetPublicHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(id, "Public")));

        var result = await sut.PreparePublicAsync([videoId, imgId], imgId, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(videoId, result.Value[0].Id);
        Assert.Equal($"https://gateway.example/api/v1/media/{videoId:D}/content", result.Value[0].Url);
        Assert.Equal(imgId, result.Value[1].Id);
        Assert.Equal($"https://gateway.example/api/v1/media/{imgId:D}/content", result.Value[1].Url);

        Assert.Equal(2, _mediaClient.VerifyCallCount);
        Assert.Equal(2, _mediaClient.SetPublicCallCount);
    }

    [Fact]
    public async Task PreparePublicAsync_ThumbnailIsVideo_OrMediaIsPdf_RejectedWithoutCallingSetPublic()
    {
        var sut = CreateService();
        var videoId = Guid.NewGuid();
        var pdfId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var caller = new TestCaller(callerId);

        _mediaClient.VerifyHandler = (id, ct) =>
        {
            string contentType = id == videoId ? "video/mp4" : "application/pdf";
            return Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", callerId.ToString("D"), contentType)));
        };

        // 1. Thumbnail là video -> bị từ chối, không gọi SetPublic
        var videoThumbRes = await sut.PreparePublicAsync([videoId], videoId, caller);
        Assert.True(videoThumbRes.IsFailure);
        Assert.Equal(ErrorType.Validation, videoThumbRes.Error.Type);
        Assert.Equal("INVALID_THUMBNAIL_CONTENT_TYPE", videoThumbRes.Error.Code);
        Assert.Equal(0, _mediaClient.SetPublicCallCount);

        // 2. Media là PDF (không phải ảnh/video) -> bị từ chối, không gọi SetPublic
        var pdfRes = await sut.PreparePublicAsync([pdfId], null, caller);
        Assert.True(pdfRes.IsFailure);
        Assert.Equal(ErrorType.Validation, pdfRes.Error.Type);
        Assert.Equal("INVALID_MEDIA_CONTENT_TYPE", pdfRes.Error.Code);
        Assert.Equal(0, _mediaClient.SetPublicCallCount);
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
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", callerId.ToString("D"), "image/jpeg")));
        _mediaClient.SetPublicHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(id, "Public")));

        var result = await sut.PreparePublicAsync([mediaId], mediaId, caller);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal($"https://gateway.example/api/v1/media/{mediaId:D}/content", result.Value[0].Url);
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

        var result = await sut.PreparePublicAsync([mediaId], mediaId, caller);

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
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "PendingUpload", callerId.ToString("D"), "image/jpeg")));

        var result = await sut.PreparePublicAsync([mediaId], mediaId, caller);

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

        var adminCaller = new TestCaller(callerId, isHuman: true, isAuthenticated: true, isAdmin: true);

        _mediaClient.VerifyHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", anotherUploaderId.ToString("D"), "image/jpeg")));

        var result = await sut.PreparePublicAsync([mediaId], mediaId, adminCaller);

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
            Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", callerId.ToString("D"), "image/jpeg")));

        _mediaClient.SetPublicHandler = (id, ct) =>
            Task.FromResult(Result.Failure<MediaVisibilityInfo>(Error.Unavailable("MEDIA_SERVICE_UNAVAILABLE", "Lỗi publish.")));

        var result = await sut.PreparePublicAsync([mediaId], mediaId, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("MEDIA_SERVICE_UNAVAILABLE", result.Error.Code);
    }

    [Fact]
    public async Task PreparePublicAsync_WithExistingMedia_OnlyVerifiesNewAndChangedThumbnail()
    {
        var sut = CreateService();
        var existingImg = Guid.NewGuid();
        var existingVid = Guid.NewGuid();
        var newImg = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var caller = new TestCaller(callerId);

        _mediaClient.VerifyHandler = (id, ct) =>
        {
            string ctStr = id == existingVid ? "video/mp4" : "image/jpeg";
            return Task.FromResult(Result.Success(new MediaVerificationInfo(id, "Ready", callerId.ToString("D"), ctStr)));
        };
        _mediaClient.SetPublicHandler = (id, ct) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(id, "Public")));

        // 1. Giữ nguyên existingImg và existingVid, chỉ thêm newImg -> chỉ verify & publish newImg (1 lần)
        var res1 = await sut.PreparePublicAsync(
            [existingImg, existingVid, newImg],
            existingImg,
            caller,
            existingMediaIds: [existingImg, existingVid],
            existingThumbnailMediaId: existingImg);

        Assert.True(res1.IsSuccess);
        Assert.Equal(1, _mediaClient.VerifyCallCount);
        Assert.Equal(1, _mediaClient.SetPublicCallCount);

        // 2. Đổi thumbnail sang existingVid (đã có trong danh sách nhưng là video) -> bị từ chối!
        var res2 = await sut.PreparePublicAsync(
            [existingImg, existingVid],
            existingVid,
            caller,
            existingMediaIds: [existingImg, existingVid],
            existingThumbnailMediaId: existingImg);

        Assert.True(res2.IsFailure);
        Assert.Equal("INVALID_THUMBNAIL_CONTENT_TYPE", res2.Error.Code);
    }
}
