using Dprental.MediaService.Application.Services;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class MediaCleanupServiceTests
{
    private readonly IMediaCleanupRepository _cleanupRepo = Substitute.For<IMediaCleanupRepository>();
    private readonly IMediaUploadSessionRepository _sessionRepo = Substitute.For<IMediaUploadSessionRepository>();
    private readonly IObjectStorageService _storageService = Substitute.For<IObjectStorageService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILogger<MediaCleanupService> _logger = Substitute.For<ILogger<MediaCleanupService>>();

    private readonly MediaCleanupService _sut;

    public MediaCleanupServiceTests()
    {
        _sut = new MediaCleanupService(
            _cleanupRepo,
            _sessionRepo,
            _storageService,
            _unitOfWork,
            _logger);
    }

    [Fact]
    public async Task ProcessPendingCleanups_WhenNoPendingItems_ShouldReturnZero()
    {
        _cleanupRepo.GetPendingItemsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaCleanupItem>());

        int processed = await _sut.ProcessPendingCleanupsAsync();

        Assert.Equal(0, processed);
        await _storageService.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessPendingCleanups_WhenItemsExist_ShouldDeleteAndMarkCompleted()
    {
        var item1 = new MediaCleanupItem { Id = Guid.NewGuid(), StorageKey = "temp/file1.tmp", Status = CleanupItemStatus.Pending };
        var item2 = new MediaCleanupItem { Id = Guid.NewGuid(), StorageKey = "temp/file2.tmp", Status = CleanupItemStatus.Pending };

        _cleanupRepo.GetPendingItemsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaCleanupItem> { item1, item2 });

        int processed = await _sut.ProcessPendingCleanupsAsync();

        Assert.Equal(2, processed);
        Assert.Equal(CleanupItemStatus.Completed, item1.Status);
        Assert.Equal(CleanupItemStatus.Completed, item2.Status);

        await _storageService.Received(1).DeleteObjectAsync("temp/file1.tmp", Arg.Any<CancellationToken>());
        await _storageService.Received(1).DeleteObjectAsync("temp/file2.tmp", Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessPendingCleanups_WhenDeleteFails_ShouldIncrementRetryCountAndMarkFailedAfterMaxRetries()
    {
        var item = new MediaCleanupItem
        {
            Id = Guid.NewGuid(),
            StorageKey = "temp/failed.tmp",
            Status = CleanupItemStatus.Pending,
            RetryCount = 2 // will reach 3 on failure
        };

        _cleanupRepo.GetPendingItemsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaCleanupItem> { item });

        _storageService.DeleteObjectAsync(item.StorageKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("MinIO unavailable"));

        int processed = await _sut.ProcessPendingCleanupsAsync();

        Assert.Equal(0, processed);
        Assert.Equal(3, item.RetryCount);
        Assert.Equal(CleanupItemStatus.Failed, item.Status);
        Assert.Contains("MinIO unavailable", item.LastError);
        await _cleanupRepo.Received(1).UpdateAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepExpiredSessions_WhenExpiredExist_ShouldMarkExpiredAndEnqueueCleanup()
    {
        var session1 = new MediaUploadSession
        {
            Id = Guid.NewGuid(),
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            TemporaryStorageKey = "temp/session1.tmp"
        };

        _sessionRepo.GetExpiredPendingSessionsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaUploadSession> { session1 });

        int swept = await _sut.SweepExpiredSessionsAsync();

        Assert.Equal(1, swept);
        Assert.Equal(UploadSessionStatus.Expired, session1.Status);
        await _sessionRepo.Received(1).UpdateAsync(session1, Arg.Any<CancellationToken>());
        await _cleanupRepo.Received(1).AddAsync(Arg.Is<MediaCleanupItem>(c =>
            c.StorageKey == "temp/session1.tmp" &&
            c.Reason == "SessionExpired"), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
