using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.Policies;
using Dprental.MediaService.Application.Services;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class MediaAccessServiceTests
{
    private readonly IMediaAssetRepository _assets = Substitute.For<IMediaAssetRepository>();
    private readonly IObjectStorageService _storage = Substitute.For<IObjectStorageService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MediaAccessService _sut;

    private static readonly ServiceCaller VerifyCaller = new("11111111-1111-4111-8111-111111111111", new HashSet<string> { "media.verify" });
    private static readonly ServiceCaller ReadCaller = new("11111111-1111-4111-8111-111111111111", new HashSet<string> { "media.read" });
    private static readonly ServiceCaller PublishCaller = new("11111111-1111-4111-8111-111111111111", new HashSet<string> { "media.publish" });

    public MediaAccessServiceTests()
    {
        _sut = new MediaAccessService(_assets, _storage, _unitOfWork,
            Microsoft.Extensions.Options.Options.Create(new MediaAccessSettings()), NullLogger<MediaAccessService>.Instance);
    }

    private static MediaAsset Asset() => new()
    {
        Id = Guid.NewGuid(),
        Status = MediaStatus.Ready,
        Visibility = MediaVisibility.Private,
        OriginalFileName = "may-xuc.jpg",
        DeclaredContentType = "image/jpeg",
        ActualContentType = "image/jpeg",
        ActualSizeBytes = 1250340,
        StorageKey = "media/test/file.bin",
        CreatedByUserId = "user-1",
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private void Store(MediaAsset asset) =>
        _assets.GetByIdAsync(asset.Id, Arg.Any<CancellationToken>()).Returns(asset);

    private void ConfigureStorage()
    {
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadataResult(1250340, "image/jpeg", null));
        _storage.GeneratePresignedReadUrlAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns("https://storage.test/signed");
    }

    [Fact]
    public async Task Verify_RequiresServiceIdentityAndScope()
    {
        var anonymous = await _sut.VerifyMediaAsync(new ServiceCaller(null, new HashSet<string>()), Guid.NewGuid());
        var invalidId = await _sut.VerifyMediaAsync(new ServiceCaller("not-a-guid", new HashSet<string> { "media.verify" }), Guid.NewGuid());
        var wrongScope = await _sut.VerifyMediaAsync(ReadCaller, Guid.NewGuid());

        Assert.Equal("UnauthorizedService", anonymous.Error.Code);
        Assert.Equal("UnauthorizedService", invalidId.Error.Code);
        Assert.Equal("MissingServiceScope", wrongScope.Error.Code);
        await _assets.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_OnlyReadyMediaForScopedService()
    {
        MediaAsset asset = Asset();
        Store(asset);

        var otherService = await _sut.VerifyMediaAsync(
            new ServiceCaller("22222222-2222-4222-8222-222222222222", new HashSet<string> { "media.verify" }), asset.Id);
        Assert.True(otherService.IsSuccess);

        asset.Status = MediaStatus.PendingUpload;
        var pending = await _sut.VerifyMediaAsync(VerifyCaller, asset.Id);
        Assert.Equal("MediaNotReady", pending.Error.Code);

        asset.Status = MediaStatus.Ready;
        var ready = await _sut.VerifyMediaAsync(VerifyCaller, asset.Id);
        Assert.True(ready.IsSuccess);
        Assert.Equal("image/jpeg", ready.Value.ContentType);
        Assert.Equal("user-1", ready.Value.UploadedByUserId);
        Assert.Equal(1250340, ready.Value.SizeBytes);
    }

    [Fact]
    public async Task Metadata_ShowsPendingWithoutUnverifiedContentAndHidesDeleted()
    {
        MediaAsset asset = Asset();
        asset.Status = MediaStatus.PendingUpload;
        asset.ActualContentType = null;
        asset.ActualSizeBytes = null;
        Store(asset);

        var pending = await _sut.GetMetadataAsync(ReadCaller, asset.Id);
        Assert.True(pending.IsSuccess);
        Assert.Equal("PendingUpload", pending.Value.Status);
        Assert.Null(pending.Value.ContentType);
        Assert.Null(pending.Value.SizeBytes);

        asset.Status = MediaStatus.Deleted;
        var deleted = await _sut.GetMetadataAsync(ReadCaller, asset.Id);
        Assert.Equal("MediaNotFound", deleted.Error.Code);
    }

    [Fact]
    public async Task PrivateAccess_ValidatesActionScopeStateAndStorage()
    {
        MediaAsset asset = Asset();
        Store(asset);
        var request = new MediaAccessRequest("read", true);

        Assert.Equal("InvalidAction", (await _sut.CreatePrivateReadUrlAsync(
            ReadCaller, asset.Id, request with { Action = "write" })).Error.Code);
        Assert.Equal("MissingServiceScope", (await _sut.CreatePrivateReadUrlAsync(
            VerifyCaller, asset.Id, request)).Error.Code);

        asset.Status = MediaStatus.Rejected;
        Assert.Equal("MediaNotReady", (await _sut.CreatePrivateReadUrlAsync(
            ReadCaller, asset.Id, request)).Error.Code);

        asset.Status = MediaStatus.Ready;
        Assert.Equal("MediaNotFound", (await _sut.CreatePrivateReadUrlAsync(
            ReadCaller, asset.Id, request)).Error.Code);

        ConfigureStorage();
        var granted = await _sut.CreatePrivateReadUrlAsync(ReadCaller, asset.Id, request);
        Assert.True(granted.IsSuccess);
        Assert.Equal("https://storage.test/signed", granted.Value.Url);
        Assert.InRange(granted.Value.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(6));
        await _storage.Received(1).GeneratePresignedReadUrlAsync(
            asset.StorageKey!, "image/jpeg", "may-xuc.jpg", true,
            TimeSpan.FromMinutes(5), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrivateAccess_StorageFailureReturnsSafeError()
    {
        MediaAsset asset = Asset();
        Store(asset);
        _storage.GetObjectMetadataAsync(asset.StorageKey!, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("internal storage detail"));

        var result = await _sut.CreatePrivateReadUrlAsync(
            ReadCaller, asset.Id, new MediaAccessRequest("read"));

        Assert.Equal("StorageUnavailable", result.Error.Code);
        Assert.DoesNotContain("internal storage detail", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploaderPreview_OnlyGrantsReadyMediaToUploader()
    {
        MediaAsset asset = Asset();
        Store(asset);
        ConfigureStorage();

        Assert.Equal("UnauthorizedUser", (await _sut.CreateUploaderPreviewUrlAsync("", asset.Id)).Error.Code);
        Assert.Equal("MediaNotFound", (await _sut.CreateUploaderPreviewUrlAsync("other-user", asset.Id)).Error.Code);
        asset.Status = MediaStatus.PendingUpload;
        Assert.Equal("MediaNotFound", (await _sut.CreateUploaderPreviewUrlAsync("user-1", asset.Id)).Error.Code);
        asset.Status = MediaStatus.Ready;

        var granted = await _sut.CreateUploaderPreviewUrlAsync("user-1", asset.Id);

        Assert.True(granted.IsSuccess);
        Assert.Equal("https://storage.test/signed", granted.Value.Url);
        await _storage.Received(1).GeneratePresignedReadUrlAsync(
            asset.StorageKey!, "image/jpeg", asset.OriginalFileName, false,
            TimeSpan.FromMinutes(5), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublicAccess_OnlyGrantsReadyPublicMedia()
    {
        MediaAsset asset = Asset();
        Store(asset);
        ConfigureStorage();

        Assert.Equal("MediaNotFound", (await _sut.CreatePublicReadUrlAsync(asset.Id, false)).Error.Code);
        asset.Visibility = MediaVisibility.Public;
        asset.Status = MediaStatus.Deleted;
        Assert.Equal("MediaNotFound", (await _sut.CreatePublicReadUrlAsync(asset.Id, false)).Error.Code);
        asset.Status = MediaStatus.Ready;
        asset.ActualContentType = "application/pdf";
        Assert.True((await _sut.CreatePublicReadUrlAsync(asset.Id, false)).IsSuccess);
    }

    [Fact]
    public async Task Visibility_RequiresScopeAndReadyMedia()
    {
        MediaAsset asset = Asset();
        Store(asset);
        var request = new ChangeMediaVisibilityRequest("public");

        Assert.Equal("MissingServiceScope", (await _sut.SetVisibilityAsync(ReadCaller, asset.Id, request)).Error.Code);
        asset.Status = MediaStatus.PendingUpload;
        Assert.Equal("MediaNotReady", (await _sut.SetVisibilityAsync(PublishCaller, asset.Id, request)).Error.Code);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Visibility_IsIdempotentAndHandlesConcurrentSameTarget()
    {
        MediaAsset asset = Asset();
        Store(asset);
        var request = new ChangeMediaVisibilityRequest("public");

        var first = await _sut.SetVisibilityAsync(PublishCaller, asset.Id, request);
        Assert.True(first.IsSuccess);
        Assert.Equal(MediaVisibility.Public, asset.Visibility);
        var repeated = await _sut.SetVisibilityAsync(PublishCaller, asset.Id, request);
        Assert.True(repeated.IsSuccess);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        asset.Visibility = MediaVisibility.Private;
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new MediaWriteConflictException("conflict", new InvalidOperationException()));
        _assets.GetByIdUntrackedAsync(asset.Id, Arg.Any<CancellationToken>())
            .Returns(new MediaAsset
            {
                Id = asset.Id,
                Status = MediaStatus.Ready,
                Visibility = MediaVisibility.Public
            });

        var raced = await _sut.SetVisibilityAsync(PublishCaller, asset.Id, request);
        Assert.True(raced.IsSuccess);
    }
}
