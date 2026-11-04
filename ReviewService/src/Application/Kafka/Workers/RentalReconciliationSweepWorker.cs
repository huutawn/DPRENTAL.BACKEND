using System.Text;
using System.Text.Json;
using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Kafka.Workers;

/// <summary>
/// Worker quét đối soát định kỳ toàn cửa sổ 30 ngày (Full-Window Sweep Reconciliation).
/// Chạy chu kỳ 5 phút, lưu checkpoint nguyên tử theo từng trang và tự động khôi phục sau sự cố.
/// </summary>
public sealed class RentalReconciliationSweepWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalReconciliationSweepWorker>? _logger;
    private TimeSpan _sweepInterval = TimeSpan.FromMinutes(5);

    public RentalReconciliationSweepWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider? timeProvider = null,
        ILogger<RentalReconciliationSweepWorker>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public TimeSpan SweepInterval
    {
        get => _sweepInterval;
        set => _sweepInterval = value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("RentalReconciliationSweepWorker đã khởi động.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Đã xảy ra lỗi trong chu kỳ quét đối soát.");
            }

            try
            {
                await Task.Delay(_sweepInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Thực thi một chu kỳ quét đối soát. Có thể gọi trực tiếp trong integration/unit tests.
    /// </summary>
    /// <returns>Tổng số mục thuê đã đối soát thành công trong chu kỳ.</returns>
    public async Task<int> RunSweepCycleAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var reconciliationRepo = scope.ServiceProvider.GetRequiredService<IReconciliationRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var rentalClient = scope.ServiceProvider.GetRequiredService<IRentalServiceClient>();
        var eligibilityService = scope.ServiceProvider.GetRequiredService<IReviewEligibilityService>();

        // 1. Kiểm tra checkpoint chưa hoàn tất để phục hồi sau crash
        var lastCheckpoint = await reconciliationRepo.GetLatestAsync(cancellationToken);

        string sweepId;
        DateTimeOffset windowStart;
        string? cursor = null;
        ReconciliationCheckpoint checkpoint;

        if (lastCheckpoint != null && !lastCheckpoint.IsCompleted)
        {
            sweepId = lastCheckpoint.SweepId;
            windowStart = lastCheckpoint.WindowStart;
            checkpoint = lastCheckpoint;
            // Lưu nguyên NextCursor do Rental trả về vào checkpoint. Nếu checkpoint cũ chưa có cursor thì bắt đầu lại từ đầu cửa sổ (cursor = null).
            cursor = lastCheckpoint.Cursor;
            _logger?.LogInformation("Khôi phục tiến trình sweep chưa hoàn tất {SweepId} với cursor '{Cursor}' từ ItemId {LastItemId}.",
                sweepId, cursor, checkpoint.LastItemId);
        }
        else
        {
            // Bắt đầu vòng sweep mới với cửa sổ 30 ngày cố định
            DateTimeOffset now = _timeProvider.GetUtcNow();
            windowStart = now.AddDays(-30);
            sweepId = $"sweep-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            checkpoint = new ReconciliationCheckpoint(sweepId, windowStart, null, null, false, now, now);
            await reconciliationRepo.AddAsync(checkpoint, cancellationToken);
            _logger?.LogInformation("Bắt đầu chu kỳ sweep mới {SweepId} với windowStart = {WindowStart}.",
                sweepId, windowStart);
        }

        bool hasMore = true;
        int totalProcessed = 0;

        // 2. Lặp từng trang kết quả từ RentalService
        while (hasMore && !cancellationToken.IsCancellationRequested)
        {
            var batchResult = await rentalClient.GetReconciliationBatchAsync(cursor, limit: 100, cancellationToken);
            if (batchResult.IsFailure)
            {
                _logger?.LogError("Thất bại khi lấy lô đối soát với cursor {Cursor}: {Error}",
                    cursor, batchResult.Error.Message);
                break;
            }

            var batch = batchResult.Value;
            hasMore = batch.HasMore;

            // Cập nhật review_projections và lưu checkpoint trang trong cùng một Transaction
            await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);

            foreach (var item in batch.Items)
            {
                var upsertResult = await eligibilityService.UpsertEligibilityAsync(item, cancellationToken);
                if (upsertResult.IsSuccess)
                {
                    totalProcessed++;
                }
                else
                {
                    _logger?.LogWarning("Không thể cập nhật eligibility cho mục {ItemId}: {Error}",
                        item.ItemId, upsertResult.Error.Message);
                }
            }

            Guid? lastItemId = batch.Items.Count > 0 ? batch.Items[^1].ItemId : checkpoint.LastItemId;
            checkpoint.Advance(batch.NextCursor, lastItemId, !hasMore, _timeProvider.GetUtcNow());
            await reconciliationRepo.UpdateAsync(checkpoint, cancellationToken);

            await tx.CommitAsync(cancellationToken);

            cursor = batch.NextCursor;
        }

        _logger?.LogInformation("Hoàn tất chu kỳ sweep {SweepId}. Tổng số mục đã xử lý: {Count}.",
            sweepId, totalProcessed);

        return totalProcessed;
    }
}
