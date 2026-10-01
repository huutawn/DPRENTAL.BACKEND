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
/// Atomic write boundary với pg_advisory_xact_lock trên mức thiết bị.
/// Hỗ trợ cả chế độ độc lập (Transaction Owner) và tham gia transaction của Inbox trên cùng scoped DbContext qua Ambient Context.
/// </summary>
public sealed class InventoryWriteTransaction : IInventoryWriteTransaction
{
    private static readonly Action<ILogger, Guid, Exception?> LogFailure = LoggerMessage.Define<Guid>(
        LogLevel.Error, new EventId(1, nameof(ExecuteAsync)), "Inventory transaction failed for equipment {EquipmentId}.");

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
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Thiết lập ambient context cho scoped DbContext trong execution boundary của Inbox hoặc Saga.
    /// Cho phép InventoryWriteTransaction tham gia vào transaction hiện hữu mà không tạo scope mới.
    /// </summary>
    public static IDisposable EnterAmbientTransaction(InventoryServiceDbContext dbContext, IServiceProvider services)
    {
        var prev = AmbientContextHolder.Value;
        AmbientContextHolder.Value = new AmbientContext(dbContext, services);
        return new ActionDisposable(() => AmbientContextHolder.Value = prev);
    }

    public async Task<Result<T>> ExecuteAsync<T>(
        Guid equipmentId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (equipmentId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_ID", "Equipment ID không được rỗng.");
        }

        if (_executing.Value)
        {
            return Error.Conflict("NESTED_INVENTORY_TRANSACTION", "Không được lồng transaction Inventory.");
        }

        _executing.Value = true;
        try
        {
            var ambient = AmbientContextHolder.Value;

            if (ambient is not null && ambient.DbContext.Database.CurrentTransaction is not null)
            {
                // Tham gia transaction của Inbox trên cùng scoped DbContext (Participant mode)
                return await ExecuteAsParticipantAsync(equipmentId, ambient.DbContext, ambient.Services, operation, cancellationToken);
            }

            // Chế độ độc lập (Transaction Owner mode): tạo scope mới và quản lý transaction lifecycle
            return await ExecuteAsOwnerAsync(equipmentId, operation, cancellationToken);
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
                LogFailure(_logger, equipmentId, ex);
            }
            return Error.Failure("TRANSACTION_FAILED", "Không thể lưu thay đổi Inventory.");
        }
        finally
        {
            _executing.Value = false;
        }
    }

    private static async Task<Result<T>> ExecuteAsParticipantAsync<T>(
        Guid equipmentId,
        InventoryServiceDbContext db,
        IServiceProvider services,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
        {
            return Error.Failure("POSTGRES_REQUIRED", "Write transaction cần PostgreSQL.");
        }

        // Khóa độc quyền thiết bị trên transaction hiện hữu
        await AcquireEquipmentLockAsync(db, equipmentId, cancellationToken);

        var context = CreateWriteContext(db, equipmentId, services);
        var result = await operation(context, cancellationToken);

        if (result.IsSuccess)
        {
            var scopeCheck = await ValidateWriteScopeAsync(db, equipmentId, cancellationToken);
            if (scopeCheck.IsFailure)
            {
                result = scopeCheck.Error;
            }
        }

        // Participant không save/commit; nếu thất bại caller/inbox transaction boundary sẽ rollback toàn bộ
        return result;
    }

    private async Task<Result<T>> ExecuteAsOwnerAsync<T>(
        Guid equipmentId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<InventoryServiceDbContext>();

        if (!db.Database.IsNpgsql())
        {
            return Error.Failure("POSTGRES_REQUIRED", "Write transaction cần PostgreSQL.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Khóa độc quyền thiết bị
        await AcquireEquipmentLockAsync(db, equipmentId, cancellationToken);

        var context = CreateWriteContext(db, equipmentId, services);
        var result = await operation(context, cancellationToken);

        if (result.IsSuccess)
        {
            var scopeCheck = await ValidateWriteScopeAsync(db, equipmentId, cancellationToken);
            if (scopeCheck.IsFailure)
            {
                result = scopeCheck.Error;
            }
        }

        if (result.IsFailure)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return result;
        }

        // Chỉ owner mới save và commit
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task AcquireEquipmentLockAsync(InventoryServiceDbContext db, Guid equipmentId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0});",
            [EquipmentLockKey.FromEquipmentId(equipmentId)],
            cancellationToken);
    }

    private static InventoryWriteContext CreateWriteContext(InventoryServiceDbContext db, Guid equipmentId, IServiceProvider services)
    {
        return new InventoryWriteContext(
            db,
            equipmentId,
            services.GetRequiredService<IInventoryEquipmentRepository>(),
            services.GetRequiredService<IReservationRepository>(),
            services.GetRequiredService<ICalendarBlockRepository>(),
            services.GetRequiredService<IDamageReportRepository>(),
            services.GetRequiredService<INotificationRepository>());
    }

    private static async Task<Result> ValidateWriteScopeAsync(InventoryServiceDbContext db, Guid equipmentId, CancellationToken ct)
    {
        var entries = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        foreach (var entry in entries)
        {
            Guid? target = entry.Entity switch
            {
                InventoryEquipment equipment => equipment.Id,
                Reservation reservation => reservation.EquipmentId,
                CalendarBlock block => block.EquipmentId,
                _ => null
            };

            Guid? reservationId = entry.Entity switch
            {
                ReservationTransition transition => transition.ReservationId,
                DamageReport report => report.ReservationId,
                Notification notification => notification.ReservationId,
                _ => null
            };

            if (reservationId is not null)
            {
                target = db.Reservations.Local.FirstOrDefault(r => r.Id == reservationId)?.EquipmentId
                    ?? await db.Reservations.Where(r => r.Id == reservationId).Select(r => (Guid?)r.EquipmentId).SingleOrDefaultAsync(ct);

                if (target is null)
                {
                    return Error.Validation("RESERVATION_NOT_FOUND", "Lượt đặt liên quan không tồn tại.");
                }
            }

            if (target is not null && target != equipmentId)
            {
                return Error.Conflict("EQUIPMENT_LOCK_MISMATCH", "Dữ liệu ghi không thuộc thiết bị đang giữ khóa.");
            }

            if (entry.Entity is ReservationTransition && entry.State != EntityState.Added)
            {
                return Error.Conflict("TRANSITION_IMMUTABLE", "Không được sửa hoặc xóa lịch sử chuyển trạng thái.");
            }

            if (entry.Entity is DamageReport && entry.State == EntityState.Deleted)
            {
                return Error.Conflict("DAMAGE_REPORT_IMMUTABLE", "Không được xóa báo hỏng.");
            }

            if (entry.Entity is DamageReport && entry.State == EntityState.Modified && entry.Properties.Any(p => p.IsModified &&
                p.Metadata.Name is not nameof(DamageReport.ReviewedAt) and not nameof(DamageReport.ReviewedBy)))
            {
                return Error.Conflict("DAMAGE_REPORT_IMMUTABLE", "Nội dung báo hỏng đã gửi không được sửa.");
            }
        }

        return Result.Success();
    }
}
