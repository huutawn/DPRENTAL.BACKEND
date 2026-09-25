using System.Security.Cryptography;
using System.Text;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.Services;
using Dprental.MediaService.Common;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class MediaUploadServiceTests
{
    private readonly IMediaUploadSessionRepository _sessionRepo = Substitute.For<IMediaUploadSessionRepository>();
    private readonly IMediaAssetRepository _assetRepo = Substitute.For<IMediaAssetRepository>();
    private readonly IMediaCleanupRepository _cleanupRepo = Substitute.For<IMediaCleanupRepository>();
    private readonly IObjectStorageService _storageService = Substitute.For<IObjectStorageService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly MediaUploadService _sut;

    public MediaUploadServiceTests()
    {
        _sut = new MediaUploadService(
            _sessionRepo,
            _assetRepo,
            _cleanupRepo,
            _storageService,
            _unitOfWork);
    }

    [Fact]
    public async Task InitializeUpload_WhenUserIdEmpty_ShouldReturnUnauthorized()
    {
        var request = new InitializeUploadRequest("equipment-image", "eq-1", "file.jpg", "image/jpeg", 1000);
        var result = await _sut.InitializeUploadAsync("", "key-1", request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unauthorized, result.Error!.Type);
    }

    [Fact]
    public async Task InitializeUpload_WhenIdempotencyKeyEmpty_ShouldReturnValidation()
    {
        var request = new InitializeUploadRequest("equipment-image", "eq-1", "file.jpg", "image/jpeg", 1000);
        var result = await _sut.InitializeUploadAsync("user-1", "", request);

        Assert.True(result.IsFailure);
        Assert.Equal("MissingIdempotencyKey", result.Error!.Code);
    }

    [Fact]
    public async Task InitializeUpload_WhenPurposeUnsupported_ShouldReturnValidation()
    {
        var request = new InitializeUploadRequest("unsupported-purpose", "eq-1", "file.jpg", "image/jpeg", 1000);
        var result = await _sut.InitializeUploadAsync("user-1", "key-1", request);

        Assert.True(result.IsFailure);
        Assert.Equal("UnsupportedPurpose", result.Error!.Code);
    }

    [Theory]
    [InlineData("", "file.jpg", "image/jpeg", 1000)]
    [InlineData("eq-1", "", "image/jpeg", 1000)]
    [InlineData("eq-1", "file.jpg", "", 1000)]
    [InlineData("eq-1", "file.jpg", "image/jpeg", 0)]
    [InlineData("eq-1", "file.jpg", "image/jpeg", -10)]
    public async Task InitializeUpload_WhenInputsInvalid_ShouldReturnValidation(
        string ownerEntityId, string fileName, string contentType, long sizeBytes)
    {
        var request = new InitializeUploadRequest("equipment-image", ownerEntityId, fileName, contentType, sizeBytes);
        var result = await _sut.InitializeUploadAsync("user-1", "key-1", request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error!.Type);
    }

    [Fact]
    public async Task InitializeUpload_WhenContentTypeNotAllowedForPurpose_ShouldReturnValidation()
    {
        // equipment-image only allows jpeg, png, webp
        var request = new InitializeUploadRequest("equipment-image", "eq-1", "doc.pdf", "application/pdf", 1000);
        var result = await _sut.InitializeUploadAsync("user-1", "key-1", request);

        Assert.True(result.IsFailure);
        Assert.Equal("UnsupportedContentType", result.Error!.Code);
    }

    [Fact]
    public async Task InitializeUpload_WhenSizeExceedsPolicy_ShouldReturnValidation()
    {
        // equipment-image max 10 MiB
        long elevenMb = 11L * 1024 * 1024;
        var request = new InitializeUploadRequest("equipment-image", "eq-1", "file.jpg", "image/jpeg", elevenMb);
        var result = await _sut.InitializeUploadAsync("user-1", "key-1", request);

        Assert.True(result.IsFailure);
        Assert.Equal("FileSizeExceeded", result.Error!.Code);
    }

    [Fact]
    public async Task InitializeUpload_WhenIdempotentRetry_ShouldReturnSameSession()
    {
        var request = new InitializeUploadRequest("equipment-image", "eq-1", "file.jpg", "image/jpeg", 1000);
        string payloadHash = ComputeHash(request);

        var existingSession = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            MediaAssetId = Guid.NewGuid(),
            CallerIdentity = "user-1",
            IdempotencyKey = "key-1",
            RequestPayloadHash = payloadHash,
            TemporaryStorageKey = "temp/test.tmp",
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            MediaAsset = new MediaAsset
            {
                DeclaredContentType = "image/jpeg"
            }
        };

        _sessionRepo.FindByCallerAndIdempotencyKeyAsync("user-1", "key-1", Arg.Any<CancellationToken>())
            .Returns(existingSession);

        _storageService.GeneratePresignedUploadUrlAsync(existingSession.TemporaryStorageKey, "image/jpeg", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new PresignedUploadUrlResult("http://storage/upload", "PUT", new Dictionary<string, string>(), DateTimeOffset.UtcNow.AddMinutes(10)));

        var result = await _sut.InitializeUploadAsync("user-1", "key-1", request);

        Assert.True(result.IsSuccess);
        Assert.Equal(existingSession.Id, result.Value.UploadId);
        Assert.Equal(existingSession.MediaAssetId, result.Value.MediaId);
    }

    [Fact]
    public async Task InitializeUpload_WhenSameIdempotencyKeyDifferentPayload_ShouldReturnConflict()
    {
        var request = new InitializeUploadRequest("equipment-image", "eq-1", "file.jpg", "image/jpeg", 1000);

        var existingSession = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            MediaAssetId = Guid.NewGuid(),
            CallerIdentity = "user-1",
            IdempotencyKey = "key-1",
            RequestPayloadHash = "different-hash-value",
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        };

        _sessionRepo.FindByCallerAndIdempotencyKeyAsync("user-1", "key-1", Arg.Any<CancellationToken>())
            .Returns(existingSession);

        var result = await _sut.InitializeUploadAsync("user-1", "key-1", request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Conflict, result.Error!.Type);
    }

    [Fact]
    public async Task InitializeUpload_WhenValid_ShouldCreateAssetAndSessionWithPresignedUrl()
    {
        _storageService.GeneratePresignedUploadUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new PresignedUploadUrlResult("http://storage/signed-put", "PUT", new Dictionary<string, string> { ["Content-Type"] = "image/jpeg" }, DateTimeOffset.UtcNow.AddMinutes(15)));

        var request = new InitializeUploadRequest("equipment-image", "eq-100", "excavator.jpg", "image/jpeg", 2048);
        var result = await _sut.InitializeUploadAsync("user-1", "new-key", request);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value.MediaId);
        Assert.NotEqual(Guid.Empty, result.Value.UploadId);
        Assert.Equal("http://storage/signed-put", result.Value.UploadUrl);

        // OwnerService and OwnerEntityType determined from purpose policy
        await _assetRepo.Received(1).AddAsync(Arg.Is<MediaAsset>(a =>
            a.Status == MediaStatus.PendingUpload &&
            a.OwnerService == "CatalogService" &&
            a.OwnerEntityType == "Equipment" &&
            a.OwnerEntityId == "eq-100" &&
            a.OriginalFileName == "excavator.jpg" &&
            a.CreatedByUserId == "user-1"), Arg.Any<CancellationToken>());

        await _sessionRepo.Received(1).AddAsync(Arg.Is<MediaUploadSession>(s =>
            s.Status == UploadSessionStatus.Pending &&
            s.CallerIdentity == "user-1" &&
            s.IdempotencyKey == "new-key"), Arg.Any<CancellationToken>());

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteUpload_WhenSessionNotFound_ShouldReturnNotFound()
    {
        _sessionRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((MediaUploadSession?)null);

        var result = await _sut.CompleteUploadAsync("user-1", Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
    }

    [Fact]
    public async Task CompleteUpload_WhenCallerMismatch_ShouldReturnForbidden()
    {
        var session = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            CallerIdentity = "owner-user",
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        _sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);

        var result = await _sut.CompleteUploadAsync("attacker-user", session.Id);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Forbidden, result.Error!.Type);
    }

    [Fact]
    public async Task CompleteUpload_WhenAlreadyCompleted_ShouldReturnIdempotentSuccess()
    {
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Status = MediaStatus.Ready,
            OriginalFileName = "excavator.jpg",
            ActualContentType = "image/jpeg",
            ActualSizeBytes = 5000
        };
        var session = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            CallerIdentity = "user-1",
            Status = UploadSessionStatus.Completed,
            MediaAsset = asset
        };
        _sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);

        var result = await _sut.CompleteUploadAsync("user-1", session.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(asset.Id, result.Value.MediaId);
        Assert.Equal("Ready", result.Value.Status);
        Assert.Equal("excavator.jpg", result.Value.OriginalFileName);
        await _storageService.DidNotReceive().MoveObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteUpload_WhenSessionExpired_ShouldMarkExpiredAndReturnExpired()
    {
        var session = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            CallerIdentity = "user-1",
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            TemporaryStorageKey = "temp/expired.tmp"
        };
        _sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);

        var result = await _sut.CompleteUploadAsync("user-1", session.Id);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Expired, result.Error!.Type);
        Assert.Equal(UploadSessionStatus.Expired, session.Status);
        await _cleanupRepo.Received(1).AddAsync(Arg.Is<MediaCleanupItem>(c => c.StorageKey == "temp/expired.tmp"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteUpload_WhenObjectNotUploadedInMinio_ShouldKeepPendingAndReturnValidation()
    {
        var session = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            CallerIdentity = "user-1",
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            TemporaryStorageKey = "temp/missing.tmp",
            MediaAsset = new MediaAsset
            {
                Id = Guid.NewGuid(),
                Status = MediaStatus.PendingUpload
            }
        };
        _sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _storageService.ObjectExistsAsync("temp/missing.tmp", Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.CompleteUploadAsync("user-1", session.Id);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Equal("ObjectNotFound", result.Error!.Code);
        Assert.Equal(UploadSessionStatus.Pending, session.Status);
        Assert.Equal(MediaStatus.PendingUpload, session.MediaAsset.Status);
    }

    [Fact]
    public async Task CompleteUpload_WhenUploadedFileHasInvalidMagicBytes_ShouldMarkRejected()
    {
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Status = MediaStatus.PendingUpload,
            Purpose = "equipment-image",
            DeclaredContentType = "image/jpeg",
            DeclaredSizeBytes = 100
        };
        var session = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            CallerIdentity = "user-1",
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            TemporaryStorageKey = "temp/fake.jpg",
            MediaAsset = asset
        };

        _sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _storageService.ObjectExistsAsync(session.TemporaryStorageKey, Arg.Any<CancellationToken>()).Returns(true);

        byte[] fakeBytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        _storageService.GetObjectStreamAsync(session.TemporaryStorageKey, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(fakeBytes));

        var result = await _sut.CompleteUploadAsync("user-1", session.Id);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unprocessable, result.Error!.Type);
        Assert.Equal(MediaStatus.Rejected, asset.Status);
        Assert.Equal(UploadSessionStatus.Rejected, session.Status);
        await _cleanupRepo.Received(1).AddAsync(Arg.Any<MediaCleanupItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteUpload_WhenChecksumMismatch_ShouldMarkRejected()
    {
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Status = MediaStatus.PendingUpload,
            Purpose = "equipment-image",
            DeclaredContentType = "image/jpeg",
            DeclaredSizeBytes = 100,
            DeclaredChecksumSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var session = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            CallerIdentity = "user-1",
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            TemporaryStorageKey = "temp/checksum-test.jpg",
            MediaAsset = asset
        };

        _sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _storageService.ObjectExistsAsync(session.TemporaryStorageKey, Arg.Any<CancellationToken>()).Returns(true);

        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
        _storageService.GetObjectStreamAsync(session.TemporaryStorageKey, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(jpegBytes));

        var result = await _sut.CompleteUploadAsync("user-1", session.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("ChecksumMismatch", result.Error!.Code);
        Assert.Equal(MediaStatus.Rejected, asset.Status);
    }

    [Fact]
    public async Task CompleteUpload_WhenValid_ShouldMoveObjectAndSetReady()
    {
        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x00, 0x01];
        string sha256 = Convert.ToHexString(SHA256.HashData(jpegBytes)).ToLowerInvariant();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Status = MediaStatus.PendingUpload,
            Purpose = "equipment-image",
            DeclaredContentType = "image/jpeg",
            DeclaredSizeBytes = jpegBytes.Length,
            DeclaredChecksumSha256 = sha256,
            OriginalFileName = "excavator.jpg"
        };
        var session = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            CallerIdentity = "user-1",
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            TemporaryStorageKey = "temp/valid.jpg",
            MediaAsset = asset
        };

        _sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);
        _storageService.ObjectExistsAsync(session.TemporaryStorageKey, Arg.Any<CancellationToken>()).Returns(true);
        _storageService.GetObjectStreamAsync(session.TemporaryStorageKey, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(jpegBytes));

        var result = await _sut.CompleteUploadAsync("user-1", session.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(asset.Id, result.Value.MediaId);
        Assert.Equal("Ready", result.Value.Status);
        Assert.Equal("excavator.jpg", result.Value.OriginalFileName);
        Assert.Equal("image/jpeg", result.Value.ContentType);
        Assert.Equal(jpegBytes.Length, result.Value.SizeBytes);

        Assert.Equal(MediaStatus.Ready, asset.Status);
        Assert.Equal(UploadSessionStatus.Completed, session.Status);
        Assert.NotNull(asset.StorageKey);
        Assert.StartsWith("media/", asset.StorageKey);

        await _storageService.Received(1).MoveObjectAsync(session.TemporaryStorageKey, asset.StorageKey, Arg.Any<CancellationToken>());
        await _assetRepo.Received(1).UpdateAsync(asset, Arg.Any<CancellationToken>());
        await _sessionRepo.Received(1).UpdateAsync(session, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteUpload_WhenConcurrencyExceptionOccursAndSessionCompleted_ShouldReturnSuccess()
    {
        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Status = MediaStatus.PendingUpload,
            Purpose = "equipment-image",
            DeclaredContentType = "image/jpeg",
            DeclaredSizeBytes = jpegBytes.Length,
            OriginalFileName = "concurrent.jpg"
        };
        var session = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            CallerIdentity = "user-1",
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            TemporaryStorageKey = "temp/concurrent.jpg",
            MediaAsset = asset
        };

        var completedAsset = new MediaAsset
        {
            Id = asset.Id,
            Status = MediaStatus.Ready,
            OriginalFileName = "concurrent.jpg",
            ActualContentType = "image/jpeg",
            ActualSizeBytes = jpegBytes.Length
        };
        var completedSession = new MediaUploadSession
        {
            Id = session.Id,
            CallerIdentity = "user-1",
            Status = UploadSessionStatus.Completed,
            MediaAsset = completedAsset
        };

        _sessionRepo.GetByIdAsync(session.Id, Arg.Any<CancellationToken>())
            .Returns(session, completedSession);

        _storageService.ObjectExistsAsync(session.TemporaryStorageKey, Arg.Any<CancellationToken>()).Returns(true);
        _storageService.GetObjectStreamAsync(session.TemporaryStorageKey, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(jpegBytes));

        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException("Concurrency conflict"));

        var result = await _sut.CompleteUploadAsync("user-1", session.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("Ready", result.Value.Status);
    }

    [Fact]
    public async Task VerifyMedia_WhenServiceIdentityEmpty_ShouldReturnUnauthorized()
    {
        var result = await _sut.VerifyMediaAsync("", Guid.NewGuid());
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Unauthorized, result.Error!.Type);
    }

    [Fact]
    public async Task VerifyMedia_WhenMediaNotFound_ShouldReturnNotFound()
    {
        _assetRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((MediaAsset?)null);

        var result = await _sut.VerifyMediaAsync("CatalogService", Guid.NewGuid());
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
    }

    [Fact]
    public async Task VerifyMedia_WhenMediaExists_ShouldReturnVerificationDto()
    {
        var mediaId = Guid.NewGuid();
        var asset = new MediaAsset
        {
            Id = mediaId,
            Status = MediaStatus.Ready,
            OwnerService = "CatalogService",
            OwnerEntityType = "Equipment",
            OwnerEntityId = "eq-100",
            Purpose = "equipment-image",
            CreatedByUserId = "user-1",
            OriginalFileName = "may-xuc.jpg",
            DeclaredContentType = "image/jpeg",
            ActualContentType = "image/jpeg",
            ActualSizeBytes = 1250340,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        _assetRepo.GetByIdAsync(mediaId, Arg.Any<CancellationToken>()).Returns(asset);

        var result = await _sut.VerifyMediaAsync("CatalogService", mediaId);

        Assert.True(result.IsSuccess);
        Assert.Equal(mediaId, result.Value.MediaId);
        Assert.Equal("Ready", result.Value.Status);
        Assert.Equal("CatalogService", result.Value.OwnerService);
        Assert.Equal("Equipment", result.Value.OwnerEntityType);
        Assert.Equal("eq-100", result.Value.OwnerEntityId);
        Assert.Equal("equipment-image", result.Value.Purpose);
        Assert.Equal("user-1", result.Value.UploadedByUserId);
        Assert.Equal("may-xuc.jpg", result.Value.OriginalFileName);
        Assert.Equal("image/jpeg", result.Value.ContentType);
        Assert.Equal(1250340, result.Value.SizeBytes);
    }

    private static string ComputeHash(InitializeUploadRequest request)
    {
        string raw = $"{request.Purpose?.Trim()}|{request.OwnerEntityId?.Trim()}|{request.FileName?.Trim()}|{request.ContentType?.Trim().ToLowerInvariant()}|{request.SizeBytes}|{request.Sha256Checksum?.Trim().ToLowerInvariant()}";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
