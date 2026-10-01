using System.Data;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Kafka.Saga;

/// <summary>
/// Triển khai điều phối Saga phân tán.
/// Khóa và đọc lại saga bằng FOR UPDATE; state transition, timeout và outgoing Outbox commit cùng transaction với Inbox.
/// Đảm bảo boundary transaction rõ ràng, xử lý tranh chấp tạo Saga ban đầu và dọn sạch ChangeTracker khi rollback.
/// </summary>
public sealed class SagaOrchestrator : ISagaOrchestrator
{
    private readonly InventoryServiceDbContext _context;
    private readonly ISagaStore _sagaStore;
    private readonly IOutboxStore _outboxStore;
    private readonly IEnumerable<ISaga> _sagas;
    private readonly ILogger<SagaOrchestrator>? _logger;

    public SagaOrchestrator(
        InventoryServiceDbContext context,
        ISagaStore sagaStore,
        IOutboxStore outboxStore,
        IEnumerable<ISaga> sagas,
        ILogger<SagaOrchestrator>? logger = null)
    {
        _context = context;
        _sagaStore = sagaStore;
        _outboxStore = outboxStore;
        _sagas = sagas;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<SagaInstance>> ProcessMessageAsync(
        string sagaType,
        Guid organizationId,
        string correlationId,
        string messageType,
        string payloadJson,
        Func<SagaInstance>? initialFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        var saga = _sagas.FirstOrDefault(s => string.Equals(s.SagaType, sagaType, StringComparison.OrdinalIgnoreCase));
        if (saga is null)
        {
            return Error.NotFound("SAGA_TYPE_NOT_FOUND", $"Không tìm thấy định nghĩa Saga cho loại '{sagaType}'.");
        }

        var isOwner = _context.Database.CurrentTransaction is null;
        var tx = isOwner
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        try
        {
            // Đọc và khóa phiên saga hiện tại với FOR UPDATE
            var instance = await _sagaStore.GetForUpdateAsync(sagaType, organizationId, correlationId, cancellationToken);
            if (instance is null)
            {
                instance = initialFactory is not null
                    ? initialFactory()
                    : SagaInstance.Create(
                        Guid.NewGuid(),
                        sagaType,
                        organizationId,
                        correlationId,
                        SagaState.Started,
                        "Started",
                        "{}",
                        DateTimeOffset.UtcNow);

                _sagaStore.Add(instance);

                // Lưu thử để bắt race condition nếu có 2 luồng cùng tạo Saga cùng lúc
                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
                {
                    // Luồng khác đã tạo saga trước: dọn tracker và đọc lại instance có lock
                    _context.ChangeTracker.Clear();
                    instance = await _sagaStore.GetForUpdateAsync(sagaType, organizationId, correlationId, cancellationToken);
                    if (instance is null) throw;
                }
            }

            // Thực thi bước saga
            var transition = await saga.HandleMessageAsync(instance, messageType, payloadJson, cancellationToken);

            // Cập nhật trạng thái saga
            instance.TransitionTo(
                transition.State,
                transition.Step.Name,
                transition.DataJson,
                DateTimeOffset.UtcNow,
                transition.NextTimeout?.TimeoutId,
                transition.NextTimeout?.Deadline);

            // Đưa các thông điệp outgoing vào Outbox
            if (transition.OutgoingMessages is not null)
            {
                foreach (var message in transition.OutgoingMessages)
                {
                    _outboxStore.Add(message);
                }
            }

            if (isOwner && tx is not null)
            {
                await _context.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }

            return Result.Success(instance);
        }
        catch (OperationCanceledException)
        {
            if (isOwner && tx is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            _context.ChangeTracker.Clear();
            throw;
        }
        catch (Exception ex)
        {
            if (isOwner && tx is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            _context.ChangeTracker.Clear();
            return Error.Failure("SAGA_MESSAGE_FAILED", ex.Message);
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
        }
    }

    /// <inheritdoc />
    public async Task<Result<SagaInstance>> ProcessTimeoutAsync(
        Guid sagaId,
        Guid expectedTimeoutId,
        Guid leaseToken,
        CancellationToken cancellationToken = default)
    {
        var sagaInstance = await _context.SagaInstances.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sagaId, cancellationToken);
        if (sagaInstance is null)
        {
            return Error.NotFound("SAGA_NOT_FOUND", $"Không tìm thấy phiên saga với Id {sagaId}.");
        }

        var saga = _sagas.FirstOrDefault(s => string.Equals(s.SagaType, sagaInstance.SagaType, StringComparison.OrdinalIgnoreCase));
        if (saga is null)
        {
            return Error.NotFound("SAGA_TYPE_NOT_FOUND", $"Không tìm thấy định nghĩa Saga cho loại '{sagaInstance.SagaType}'.");
        }

        // Khởi tạo transaction độc lập cho Timeout callback
        await using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            _context.ChangeTracker.Clear();
            // Khóa bản ghi saga với FOR UPDATE
            SagaInstance? lockedInstance;
            if (_context.Database.IsNpgsql())
            {
                lockedInstance = await _context.SagaInstances
                    .FromSqlInterpolated($"SELECT *, xmin FROM inventory.saga_instances WHERE id = {sagaId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                lockedInstance = await _context.SagaInstances.FindAsync([sagaId], cancellationToken);
            }

            if (lockedInstance is null)
            {
                return Error.NotFound("SAGA_NOT_FOUND", "Saga không tồn tại.");
            }

            // Saga terminal không tiếp tục timeout
            if (lockedInstance.State is SagaState.Completed or SagaState.Failed)
            {
                lockedInstance.ClearTimeout();
                await _context.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return Result.Success(lockedInstance);
            }

            // Đối chiếu TimeoutId dưới lock: nếu timeout đã bị hủy hoặc thay thế bởi message khác
            if (lockedInstance.TimeoutId != expectedTimeoutId)
            {
                _logger?.LogInformation(
                    "Timeout {Expected} của Saga {Id} đã được thay thế hoặc hủy bỏ ({Current}). Bỏ qua timeout callback.",
                    expectedTimeoutId, sagaId, lockedInstance.TimeoutId);

                await tx.CommitAsync(cancellationToken);
                return Result.Success(lockedInstance);
            }

            // Đối chiếu lease token dưới lock: nếu worker mất lease thì không ghi đè
            if (lockedInstance.TimeoutLeaseToken != leaseToken)
            {
                _logger?.LogWarning("Worker mất lease timeout cho Saga {Id}. Worker khác đã claim lại.", sagaId);
                await tx.CommitAsync(cancellationToken);
                return Error.Conflict("SAGA_LEASE_LOST", "Worker mất lease timeout.");
            }

            // Kiểm tra deadline dưới lock
            if (lockedInstance.TimeoutDeadline is null || lockedInstance.TimeoutDeadline > DateTimeOffset.UtcNow)
            {
                await tx.CommitAsync(cancellationToken);
                return Result.Success(lockedInstance);
            }

            var timeoutObj = new SagaTimeout(
                lockedInstance.TimeoutId.Value,
                lockedInstance.CurrentStep,
                lockedInstance.TimeoutDeadline.Value);

            // Thực thi timeout callback
            var transition = await saga.HandleTimeoutAsync(lockedInstance, timeoutObj, cancellationToken);

            // Cập nhật trạng thái chuyển đổi
            lockedInstance.TransitionTo(
                transition.State,
                transition.Step.Name,
                transition.DataJson,
                DateTimeOffset.UtcNow,
                transition.NextTimeout?.TimeoutId,
                transition.NextTimeout?.Deadline);

            // Stage các Outbox messages mới
            if (transition.OutgoingMessages is not null)
            {
                foreach (var message in transition.OutgoingMessages)
                {
                    _outboxStore.Add(message);
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Result.Success(lockedInstance);
        }
        catch (OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();
            throw;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();

            // Ghi nhận lỗi và tăng bộ đếm timeout retry trong transaction riêng sạch
            await RecordTimeoutFailureAsync(sagaId, expectedTimeoutId, leaseToken, ex.Message, cancellationToken);

            return Error.Failure("SAGA_TIMEOUT_FAILED", ex.Message);
        }
    }

    private async Task RecordTimeoutFailureAsync(
        Guid sagaId,
        Guid expectedTimeoutId,
        Guid leaseToken,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var retryTx = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            SagaInstance? instance;
            if (_context.Database.IsNpgsql())
            {
                instance = await _context.SagaInstances
                    .FromSqlInterpolated($"SELECT *, xmin FROM inventory.saga_instances WHERE id = {sagaId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                instance = await _context.SagaInstances.FindAsync([sagaId], cancellationToken);
            }

            if (instance is not null && instance.TimeoutId == expectedTimeoutId && instance.TimeoutLeaseToken == leaseToken)
            {
                if (instance.TimeoutRetryCount >= 4) // Lần thứ 5 cạn budget
                {
                    _logger?.LogError(
                        "Saga {Id} ({Type}) đã cạn budget 5 lần thử timeout. Lỗi cuối: {Error}",
                        instance.Id, instance.SagaType, errorMessage);

                    instance.RecordTimeoutFailure(errorMessage, DateTimeOffset.UtcNow.AddYears(10));
                }
                else
                {
                    var nextDeadline = RetryBackoffCalculator.CalculateNextAttemptAt(
                        DateTimeOffset.UtcNow,
                        instance.TimeoutRetryCount + 1);

                    _logger?.LogWarning(
                        "Timeout Saga {Id} thất bại lần {Attempt}/5: {Error}. Lên lịch thử lại lúc {Deadline}",
                        instance.Id, instance.TimeoutRetryCount + 1, errorMessage, nextDeadline);

                    instance.RecordTimeoutFailure(errorMessage, nextDeadline);
                }

                await _context.SaveChangesAsync(cancellationToken);
                await retryTx.CommitAsync(cancellationToken);
            }
            _context.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Không thể ghi nhận failure cho Saga timeout {Id}", sagaId);
            _context.ChangeTracker.Clear();
        }
    }
}
