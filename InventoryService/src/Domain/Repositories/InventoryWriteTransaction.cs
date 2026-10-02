using System.Data;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Atomic write boundary với pg_advisory_xact_lock trên mức tài sản.
/// Hỗ trợ cả chế độ độc lập (Transaction Owner) và tham gia transaction của Inbox trên cùng scoped DbContext qua Ambient Context.
/// </summary>
public sealed class InventoryWriteTransaction : IInventoryWriteTransaction
{
    private static readonly Action<ILogger, Guid, Exception?> LogFailure = LoggerMessage.Define<Guid>(
        LogLevel.Error, new EventId(1, nameof(ExecuteAsync)), "Inventory transaction failed for asset {AssetId}.");

    private static readonly AsyncLocal<AmbientContext?> AmbientContextHolder = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InventoryWriteTransaction>? _logger;
    private readonly AsyncLocal<bool> _executing = new();

    private sealed record AmbientContext(InventoryServiceDbContext DbContext, IServiceProvider Services);

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _onDispose;
        public ActionDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }

    public InventoryWriteTransaction(
        IServiceScopeFactory scopeFactory,
        ILogger<InventoryWriteTransaction>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger;
    }

    /// <summary>
    /// Thiết lập ambient context cho scoped DbContext trong execution boundary của Inbox.
    /// Cho phép InventoryWriteTransaction tham gia vào transaction hiện hữu mà không tạo scope mới.
    /// </summary>
    public static IDisposable EnterAmbientTransaction(InventoryServiceDbContext dbContext, IServiceProvider services)
    {
        AmbientContextHolder.Value = new AmbientContext(dbContext, services);
        return new ActionDisposable(() => AmbientContextHolder.Value = null);
    }

    public async Task<Result<T>> ExecuteAsync<T>(
        Guid assetId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return Error.Validation("INVALID_ASSET_ID", "Asset ID không được rỗng khi thực thi transaction.");
        }

        ArgumentNullException.ThrowIfNull(operation);

        if (_executing.Value)
        {
            return Error.Conflict("NESTED_INVENTORY_TRANSACTION", "Không được lồng các lần gọi IInventoryWriteTransaction.ExecuteAsync. Một transaction boundary chỉ xử lý một asset.");
        }

        _executing.Value = true;
        try
        {
            var ambient = AmbientContextHolder.Value;
            if (ambient is not null)
            {
                return await ExecuteAsParticipantAsync(assetId, ambient.DbContext, ambient.Services, operation, cancellationToken);
            }

            return await ExecuteAsOwnerAsync(assetId, operation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogFailure(_logger, assetId, ex);
            }

            return Error.Failure("TRANSACTION_FAILED", $"Thao tác ghi thất bại trên tài sản {assetId}: {ex.Message}");
        }
        finally
        {
            _executing.Value = false;
        }
    }

    private static async Task<Result<T>> ExecuteAsParticipantAsync<T>(
        Guid assetId,
        InventoryServiceDbContext db,
        IServiceProvider services,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        await AcquireAssetLockAsync(db, assetId, cancellationToken);

        var context = CreateWriteContext(db, assetId, services);
        var result = await operation(context, cancellationToken);

        if (result.IsFailure)
        {
            return result;
        }

        var validationResult = await ValidateWriteScopeAsync(db, assetId, cancellationToken);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<Result<T>> ExecuteAsOwnerAsync<T>(
        Guid assetId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        await AcquireAssetLockAsync(db, assetId, cancellationToken);

        var context = CreateWriteContext(db, assetId, scope.ServiceProvider);
        var result = await operation(context, cancellationToken);

        if (result.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        var validationResult = await ValidateWriteScopeAsync(db, assetId, cancellationToken);
        if (validationResult.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return validationResult.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    private static async Task AcquireAssetLockAsync(InventoryServiceDbContext db, Guid assetId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0});",
            [AssetLockKey.FromAssetId(assetId)],
            cancellationToken);
    }

    private static InventoryWriteContext CreateWriteContext(InventoryServiceDbContext db, Guid assetId, IServiceProvider services)
    {
        return new InventoryWriteContext(
            db,
            assetId,
            services.GetRequiredService<IInventoryAssetRepository>(),
            services.GetRequiredService<IAssetAllocationRepository>(),
            services.GetRequiredService<ICalendarBlockRepository>());
    }

    private static async Task<Result> ValidateWriteScopeAsync(InventoryServiceDbContext db, Guid assetId, CancellationToken ct)
    {
        var entries = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        foreach (var entry in entries)
        {
            Guid? target = entry.Entity switch
            {
                InventoryAsset asset => asset.Id,
                AssetAllocation allocation => allocation.AssetId,
                CalendarBlock block => block.AssetId,
                _ => null
            };

            Guid? allocationId = entry.Entity switch
            {
                AllocationTransition transition => transition.AllocationId,
                _ => null
            };

            if (allocationId is not null)
            {
                target = db.AssetAllocations.Local.FirstOrDefault(a => a.Id == allocationId)?.AssetId
                    ?? await db.AssetAllocations.Where(a => a.Id == allocationId).Select(a => (Guid?)a.AssetId).SingleOrDefaultAsync(ct);

                if (target is null)
                {
                    return Error.Validation("ALLOCATION_NOT_FOUND", "Lượt phân bổ liên quan không tồn tại.");
                }
            }

            if (target is not null && target != assetId)
            {
                return Error.Conflict("ASSET_LOCK_MISMATCH", "Dữ liệu ghi không thuộc tài sản đang giữ khóa.");
            }

            if (entry.Entity is AllocationTransition && entry.State != EntityState.Added)
            {
                return Error.Conflict("TRANSITION_IMMUTABLE", "Không được sửa hoặc xóa lịch sử chuyển trạng thái.");
            }
        }

        return Result.Success();
    }
}
