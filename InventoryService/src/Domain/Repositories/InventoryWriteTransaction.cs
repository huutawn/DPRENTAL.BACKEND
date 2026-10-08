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
/// Atomic write boundary với pg_advisory_xact_lock phân cấp (Group -> Listing -> Asset).
/// Hỗ trợ cả chế độ độc lập (Transaction Owner) và tham gia transaction của Inbox trên cùng scoped DbContext qua Ambient Context.
/// </summary>
public sealed class InventoryWriteTransaction : IInventoryWriteTransaction
{
    private static readonly Action<ILogger, string, Exception?> LogFailure = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(1, nameof(InventoryWriteTransaction)), "Inventory transaction failed for operation {Operation}.");

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

    /// <inheritdoc />
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

        return await ExecuteWithinBoundaryAsync(
            $"AssetWrite:{assetId}",
            async (db, services, ct) =>
            {
                // Thứ tự khóa: Listing cha (nếu tồn tại) -> AssetId
                var listingId = await db.Assets
                    .Where(a => a.Id == assetId)
                    .Select(a => (Guid?)a.EquipmentListingId)
                    .FirstOrDefaultAsync(ct);

                if (listingId.HasValue)
                {
                    await AcquireListingLockAsync(db, listingId.Value, ct);
                }

                await AcquireAssetLockAsync(db, assetId, ct);

                var context = CreateWriteContext(db, assetId, listingId, services, initiallyLockedAssets: [assetId]);
                var result = await operation(context, ct);

                if (result.IsFailure)
                {
                    return result;
                }

                var validationResult = await ValidateWriteScopeAsync(db, listingId, context.LockedAssetIds, ct);
                if (validationResult.IsFailure)
                {
                    return validationResult.Error;
                }

                return result;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<T>> ExecuteListingWriteAsync<T>(
        Guid listingId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        if (listingId == Guid.Empty)
        {
            return Error.Validation("INVALID_LISTING_ID", "Listing ID không được rỗng khi thực thi transaction.");
        }

        ArgumentNullException.ThrowIfNull(operation);

        return await ExecuteWithinBoundaryAsync(
            $"ListingWrite:{listingId}",
            async (db, services, ct) =>
            {
                await AcquireListingLockAsync(db, listingId, ct);

                var context = CreateWriteContext(db, Guid.Empty, listingId, services);
                var result = await operation(context, ct);

                if (result.IsFailure)
                {
                    return result;
                }

                var validationResult = await ValidateWriteScopeAsync(db, listingId, context.LockedAssetIds, ct);
                if (validationResult.IsFailure)
                {
                    return validationResult.Error;
                }

                return result;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<T>> ExecuteGroupHoldAsync<T>(
        string sourceSystem,
        string groupReference,
        Guid listingId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceSystem))
        {
            return Error.Validation("INVALID_SOURCE_SYSTEM", "SourceSystem không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(groupReference))
        {
            return Error.Validation("INVALID_GROUP_REFERENCE", "GroupReference không được để trống.");
        }

        if (listingId == Guid.Empty)
        {
            return Error.Validation("INVALID_LISTING_ID", "Listing ID không được rỗng khi thực thi transaction.");
        }

        ArgumentNullException.ThrowIfNull(operation);

        return await ExecuteWithinBoundaryAsync(
            $"GroupHold:{sourceSystem}:{groupReference}",
            async (db, services, ct) =>
            {
                // Thứ tự khóa: Group -> Listing -> Assets (assets được khóa trong operation callback)
                await AcquireGroupLockAsync(db, sourceSystem, groupReference, ct);
                await AcquireListingLockAsync(db, listingId, ct);

                var context = CreateWriteContext(db, Guid.Empty, listingId, services);
                var result = await operation(context, ct);

                if (result.IsFailure)
                {
                    return result;
                }

                var validationResult = await ValidateWriteScopeAsync(db, listingId, context.LockedAssetIds, ct);
                if (validationResult.IsFailure)
                {
                    return validationResult.Error;
                }

                return result;
            },
            cancellationToken);
    }

    private async Task<Result<T>> ExecuteWithinBoundaryAsync<T>(
        string operationName,
        Func<InventoryServiceDbContext, IServiceProvider, CancellationToken, Task<Result<T>>> coreOperation,
        CancellationToken cancellationToken)
    {
        if (_executing.Value)
        {
            return Error.Conflict("NESTED_INVENTORY_TRANSACTION", "Không được lồng các lần gọi IInventoryWriteTransaction. Một transaction boundary chỉ xử lý một context.");
        }

        _executing.Value = true;
        try
        {
            var ambient = AmbientContextHolder.Value;
            if (ambient is not null)
            {
                return await ExecuteAsParticipantAsync(ambient.DbContext, ambient.Services, coreOperation, cancellationToken);
            }

            return await ExecuteAsOwnerAsync(coreOperation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("STALE_INVENTORY_WRITE", "Dữ liệu đã được thay đổi; đọc lại trước khi thử lại.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Error.Conflict("INVENTORY_DUPLICATE_KEY", "Khóa nghiệp vụ đã tồn tại; đọc lại và đối chiếu payload.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg &&
            pg.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.NotNullViolation)
        {
            return Error.Validation("INVENTORY_CONSTRAINT_VIOLATION", "Dữ liệu không thỏa ràng buộc Inventory.");
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogFailure(_logger, operationName, ex);
            }

            return Error.Failure("TRANSACTION_FAILED", "Không thể lưu thay đổi Inventory.");
        }
        finally
        {
            _executing.Value = false;
        }
    }

    private static async Task<Result<T>> ExecuteAsParticipantAsync<T>(
        InventoryServiceDbContext db,
        IServiceProvider services,
        Func<InventoryServiceDbContext, IServiceProvider, CancellationToken, Task<Result<T>>> coreOperation,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return Error.Failure("POSTGRES_REQUIRED", "Write transaction cần PostgreSQL.");
        }

        return await coreOperation(db, services, cancellationToken);
    }

    private async Task<Result<T>> ExecuteAsOwnerAsync<T>(
        Func<InventoryServiceDbContext, IServiceProvider, CancellationToken, Task<Result<T>>> coreOperation,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        if (!db.Database.IsNpgsql())
        {
            return Error.Failure("POSTGRES_REQUIRED", "Write transaction cần PostgreSQL.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        var result = await coreOperation(db, scope.ServiceProvider, cancellationToken);

        if (result.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
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

    private static async Task AcquireListingLockAsync(InventoryServiceDbContext db, Guid listingId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0});",
            [AssetLockKey.FromListingId(listingId)],
            cancellationToken);
    }

    private static async Task AcquireGroupLockAsync(InventoryServiceDbContext db, string sourceSystem, string groupReference, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0});",
            [AssetLockKey.FromGroup(sourceSystem, groupReference)],
            cancellationToken);
    }

    private static InventoryWriteContext CreateWriteContext(
        InventoryServiceDbContext db,
        Guid assetId,
        Guid? listingId,
        IServiceProvider services,
        IEnumerable<Guid>? initiallyLockedAssets = null)
    {
        return new InventoryWriteContext(
            db,
            assetId,
            listingId,
            services.GetRequiredService<IEquipmentListingRepository>(),
            services.GetRequiredService<IInventoryAssetRepository>(),
            services.GetRequiredService<IAssetAllocationRepository>(),
            services.GetRequiredService<ICalendarBlockRepository>(),
            initiallyLockedAssets);
    }

    private static async Task<Result> ValidateWriteScopeAsync(
        InventoryServiceDbContext db,
        Guid? expectedListingId,
        HashSet<Guid> lockedAssetIds,
        CancellationToken ct)
    {
        var entries = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        foreach (var entry in entries)
        {
            if (entry.Entity is EquipmentListing listing)
            {
                if (expectedListingId.HasValue && listing.Id != expectedListingId.Value)
                {
                    return Error.Conflict("ASSET_LOCK_MISMATCH", "Dữ liệu ghi không thuộc listing đang giữ khóa.");
                }

                if (entry.State == EntityState.Modified)
                {
                    if (entry.Properties.Any(p => p.IsModified && p.OriginalValue is Guid orig && p.CurrentValue is Guid curr && orig != curr &&
                        (p.Metadata.Name is nameof(EquipmentListing.OwnerUserId) or nameof(EquipmentListing.EquipmentTypeId))))
                    {
                        return Error.Conflict("ASSET_IDENTITY_IMMUTABLE", "Không được đổi ownership hoặc identity của listing.");
                    }
                }
            }
            else if (entry.Entity is InventoryAsset asset)
            {
                if (expectedListingId.HasValue && asset.EquipmentListingId != expectedListingId.Value)
                {
                    return Error.Conflict("ASSET_LOCK_MISMATCH", "Dữ liệu ghi không thuộc listing đang giữ khóa.");
                }

                if (entry.State is EntityState.Modified or EntityState.Deleted && lockedAssetIds.Count > 0 && !lockedAssetIds.Contains(asset.Id))
                {
                    return Error.Conflict("ASSET_LOCK_MISMATCH", "Dữ liệu ghi không thuộc tài sản đang giữ khóa.");
                }

                if (entry.State == EntityState.Modified)
                {
                    if (entry.Properties.Any(p => p.IsModified && p.OriginalValue is not null && p.CurrentValue is not null && !p.OriginalValue.Equals(p.CurrentValue) &&
                        (p.Metadata.Name is nameof(InventoryAsset.AssetCode) or nameof(InventoryAsset.EquipmentListingId))))
                    {
                        return Error.Conflict("ASSET_IDENTITY_IMMUTABLE", "Không được đổi AssetCode hoặc EquipmentListingId của tài sản.");
                    }
                }
            }
            else if (entry.Entity is AssetAllocation allocation)
            {
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                {
                    if (entry.Property(nameof(AssetAllocation.AssetId)).OriginalValue is Guid originalAssetId &&
                        lockedAssetIds.Count > 0 && !lockedAssetIds.Contains(originalAssetId))
                    {
                        return Error.Conflict("ASSET_LOCK_MISMATCH", "Dữ liệu ghi không thuộc tài sản đang giữ khóa.");
                    }
                }

                if (lockedAssetIds.Count > 0 && !lockedAssetIds.Contains(allocation.AssetId))
                {
                    return Error.Conflict("ASSET_LOCK_MISMATCH", "Dữ liệu ghi không thuộc tài sản đang giữ khóa.");
                }
            }
            else if (entry.Entity is CalendarBlock block)
            {
                if (entry.State is EntityState.Modified or EntityState.Deleted)
                {
                    if (entry.Property(nameof(CalendarBlock.AssetId)).OriginalValue is Guid originalAssetId &&
                        lockedAssetIds.Count > 0 && !lockedAssetIds.Contains(originalAssetId))
                    {
                        return Error.Conflict("ASSET_LOCK_MISMATCH", "Dữ liệu ghi không thuộc tài sản đang giữ khóa.");
                    }
                }

                if (lockedAssetIds.Count > 0 && !lockedAssetIds.Contains(block.AssetId))
                {
                    return Error.Conflict("ASSET_LOCK_MISMATCH", "Dữ liệu ghi không thuộc tài sản đang giữ khóa.");
                }
            }
            else if (entry.Entity is AllocationTransition transition)
            {
                if (entry.State != EntityState.Added)
                {
                    return Error.Conflict("TRANSITION_IMMUTABLE", "Không được sửa hoặc xóa lịch sử chuyển trạng thái.");
                }

                var allocId = transition.AllocationId;
                var targetAssetId = db.AssetAllocations.Local.FirstOrDefault(a => a.Id == allocId)?.AssetId
                    ?? await db.AssetAllocations.Where(a => a.Id == allocId).Select(a => (Guid?)a.AssetId).SingleOrDefaultAsync(ct);

                if (targetAssetId is null)
                {
                    return Error.Validation("ALLOCATION_NOT_FOUND", "Lượt phân bổ liên quan không tồn tại.");
                }

                if (lockedAssetIds.Count > 0 && !lockedAssetIds.Contains(targetAssetId.Value))
                {
                    return Error.Conflict("ASSET_LOCK_MISMATCH", "Dữ liệu ghi không thuộc tài sản đang giữ khóa.");
                }
            }
        }

        return Result.Success();
    }
}
