using System.Security.Cryptography;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Application.Policies;
using Dprental.MediaService.Application.Services;
using Dprental.MediaService.Common;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class MediaUploadServiceTests
{
    private readonly IMediaUploadSessionRepository _sessions = Substitute.For<IMediaUploadSessionRepository>();
    private readonly IMediaAssetRepository _assets = Substitute.For<IMediaAssetRepository>();
    private readonly IMediaCleanupRepository _cleanup = Substitute.For<IMediaCleanupRepository>();
    private readonly IObjectStorageService _storage = Substitute.For<IObjectStorageService>();
    private readonly IMediaAccessService _access = Substitute.For<IMediaAccessService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MediaUploadService _sut;
    private MediaUploadSession? _storedSession;

    public MediaUploadServiceTests()
    {
        _sut = new MediaUploadService(_sessions, _assets, _cleanup, _storage, _access, _unitOfWork,
            Microsoft.Extensions.Options.Options.Create(new UploadSettings()),
            NullLogger<MediaUploadService>.Instance);
        _access.CreateUploaderPreviewUrlAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(new MediaReadUrlDto(call.ArgAt<Guid>(1),
                "https://storage.test/preview", DateTimeOffset.UtcNow.AddMinutes(5))));
        _sessions.AddAsync(Arg.Any<MediaUploadSession>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _storedSession = call.Arg<MediaUploadSession>();
                return Task.CompletedTask;
            });
        _sessions.FindByCallerAndIdempotencyKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => _storedSession);
        _sessions.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => _storedSession);
    }

    private static byte[] ValidJpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x01, 0x02];

    private static UploadMediaRequest Request(byte[] bytes, string contentType = "image/jpeg", string? checksum = null) =>
        new("machine.jpg", contentType, bytes.Length, new MemoryStream(bytes), checksum);

    private void ConfigureStoredObject(byte[] bytes)
    {
        _storage.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectMetadataResult(bytes.Length, "image/jpeg", null));
        _storage.GetObjectStreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));
    }

    [Fact]
    public async Task Upload_RejectsMissingIdentityAndInvalidInputBeforeStorage()
    {
        byte[] bytes = ValidJpeg();

        Assert.Equal("UnauthorizedUser", (await _sut.UploadAsync("", null, Request(bytes))).Error.Code);
        Assert.Equal("UnsupportedContentType", (await _sut.UploadAsync("user-1", null, Request(bytes, "application/unknown"))).Error.Code);
        Assert.Equal("FileSizeExceeded", (await _sut.UploadAsync("user-1", null,
            Request(bytes) with { SizeBytes = 11 * 1024 * 1024 })).Error.Code);
        Assert.Equal("FileSizeMismatch", (await _sut.UploadAsync("user-1", null,
            Request(bytes) with { SizeBytes = bytes.Length - 1 })).Error.Code);
        await _storage.DidNotReceive().PutObjectAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_ValidFileReturnsReadyWithoutExposingSession()
    {
        byte[] bytes = ValidJpeg();
        ConfigureStoredObject(bytes);

        var result = await _sut.UploadAsync("user-1", "key-1", Request(bytes));

        Assert.True(result.IsSuccess);
        Assert.Equal("Ready", result.Value.Status);
        Assert.Equal("machine.jpg", result.Value.OriginalFileName);
        Assert.Equal(bytes.Length, result.Value.SizeBytes);
        Assert.Equal("https://storage.test/preview", result.Value.PreviewUrl);
        Assert.NotNull(result.Value.PreviewUrlExpiresAtUtc);
        Assert.NotNull(_storedSession);
        Assert.Equal(UploadSessionStatus.Completed, _storedSession.Status);
        Assert.Equal(MediaStatus.Ready, _storedSession.MediaAsset?.Status);
        await _storage.Received(1).PutObjectAsync(
            _storedSession.TemporaryStorageKey, Arg.Any<Stream>(), bytes.Length,
            "image/jpeg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_SameKeyAndContentReturnsSameMedia_ChangedContentConflicts()
    {
        byte[] bytes = ValidJpeg();
        ConfigureStoredObject(bytes);
        var first = await _sut.UploadAsync("user-1", "key-1", Request(bytes));
        var replay = await _sut.UploadAsync("user-1", "key-1", Request(bytes));
        byte[] changed = (byte[])bytes.Clone();
        changed[^1] ^= 0xFF;
        var conflict = await _sut.UploadAsync("user-1", "key-1", Request(changed));

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value.MediaId, replay.Value.MediaId);
        Assert.Equal("https://storage.test/preview", replay.Value.PreviewUrl);
        Assert.Equal("IdempotencyConflict", conflict.Error.Code);
        await _storage.Received(1).PutObjectAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_InvalidSignatureRejectsMediaAndSchedulesCleanup()
    {
        byte[] invalid = [0x00, 0x01, 0x02, 0x03];
        ConfigureStoredObject(invalid);

        var result = await _sut.UploadAsync("user-1", "key-1", Request(invalid));

        Assert.Equal("UnsupportedContentType", result.Error.Code);
        Assert.Equal(UploadSessionStatus.Rejected, _storedSession?.Status);
        Assert.Equal(MediaStatus.Rejected, _storedSession?.MediaAsset?.Status);
        await _cleanup.Received().AddAsync(Arg.Any<MediaCleanupItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_DeclaredMimeMustMatchFileSignature()
    {
        byte[] bytes = ValidJpeg();
        ConfigureStoredObject(bytes);

        var result = await _sut.UploadAsync("user-1", "mismatch", Request(bytes, "application/pdf"));

        Assert.Equal("ContentTypeMismatch", result.Error.Code);
        Assert.Equal(MediaStatus.Rejected, _storedSession?.MediaAsset?.Status);
    }

    [Fact]
    public async Task Upload_ChecksumMismatchRejectsMedia()
    {
        byte[] bytes = ValidJpeg();
        ConfigureStoredObject(bytes);
        string wrongHash = new('0', 64);
        Assert.NotEqual(wrongHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        var result = await _sut.UploadAsync("user-1", "key-1", Request(bytes, checksum: wrongHash));

        Assert.Equal("ChecksumMismatch", result.Error.Code);
        Assert.Equal(MediaStatus.Rejected, _storedSession?.MediaAsset?.Status);
    }

    [Fact]
    public async Task Upload_StorageFailureKeepsSessionRetryable()
    {
        byte[] bytes = ValidJpeg();
        _storage.PutObjectAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("private storage detail"));

        var result = await _sut.UploadAsync("user-1", "key-1", Request(bytes));

        Assert.Equal("StorageUnavailable", result.Error.Code);
        Assert.DoesNotContain("private storage detail", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(UploadSessionStatus.Pending, _storedSession?.Status);
    }

    [Fact]
    public async Task Upload_PreviewSigningFailureStillReturnsReadyMedia()
    {
        byte[] bytes = ValidJpeg();
        ConfigureStoredObject(bytes);
        _access.CreateUploaderPreviewUrlAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<MediaReadUrlDto>(Error.Failure("StorageUnavailable", "Cannot sign")));

        var result = await _sut.UploadAsync("user-1", "key-1", Request(bytes));

        Assert.True(result.IsSuccess);
        Assert.Equal("Ready", result.Value.Status);
        Assert.Null(result.Value.PreviewUrl);
        Assert.Null(result.Value.PreviewUrlExpiresAtUtc);
    }
}
