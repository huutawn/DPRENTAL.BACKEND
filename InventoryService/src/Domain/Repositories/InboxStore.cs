using System.Data;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai Transactional Inbox store và execution boundary trên PostgreSQL.
/// Đảm bảo tính duy nhất theo (Handler, MessageId) và commit atomic với Outbox/Saga.
/// </summary>
public sealed class InboxStore : IInboxStore
{
    private readonly InventoryServiceDbContext _context;
    private readonly IServiceProvider? _serviceProvider;

    public InboxStore(InventoryServiceDbContext context, IServiceProvider? serviceProvider = null)
    {
        _context = context;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public async Task<bool> HasBeenReceivedAsync(
        string handler,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        return await _context.InboxMessages
            .AnyAsync(m => m.Handler == handler && m.MessageId == messageId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HasBeenProcessedAsync(
        string handler,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        return await _context.InboxMessages
            .AnyAsync(m => m.Handler == handler && m.MessageId == messageId && m.Status == InboxMessageStatus.Processed, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InboxMessage?> GetAsync(
        string handler,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        return await _context.InboxMessages
            .FirstOrDefaultAsync(m => m.Handler == handler && m.MessageId == messageId, cancellationToken);
    }

    /// <inheritdoc />
    public void Add(InboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _context.InboxMessages.Add(message);
    }

    /// <inheritdoc />
    public void Update(InboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _context.InboxMessages.Update(message);
    }

    /// <inheritdoc />
    public async Task<InboxExecutionResult<T>> ExecuteInTransactionAsync<T>(
        string handler,
        string messageId,
        DateTimeOffset now,
        Func<CancellationToken, Task<Result<T>>> operation,
        Func<int, string, OutboxMessage>? dlqMessageFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(operation);

        var isOwner = _context.Database.CurrentTransaction is null;
        var transaction = isOwner
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        using var ambientTx = _serviceProvider is not null
            ? InventoryWriteTransaction.EnterAmbientTransaction(_context, _serviceProvider)
            : null;
        try
        {
            InboxMessage? message;
            if (_context.Database.IsNpgsql())
            {
                // Khóa receipt bản ghi Inbox bằng FOR UPDATE nếu đã tồn tại
                message = await _context.InboxMessages
                    .FromSqlInterpolated($"SELECT * FROM inventory.inbox_messages WHERE handler = {handler} AND message_id = {messageId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                message = await _context.InboxMessages
                    .FirstOrDefaultAsync(m => m.Handler == handler && m.MessageId == messageId, cancellationToken);
            }

            // 1. Kiểm tra trạng thái đã hoàn tất thành công (Idempotent skip)
            if (message is not null && message.Status == InboxMessageStatus.Processed)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return new InboxExecutionResult<T>(InboxProcessingStatus.AlreadyProcessed, RetryCount: message.RetryCount);
            }

            // 2. Kiểm tra trạng thái đã DeadLettered trước đó
            if (message is not null && message.Status == InboxMessageStatus.DeadLettered)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return new InboxExecutionResult<T>(
                    InboxProcessingStatus.DeadLettered,
                    Error: Error.Failure("ALREADY_DEAD_LETTERED", message.Error ?? "Thông điệp đã chuyển vào DLQ."),
                    RetryCount: message.RetryCount);
            }

            // 3. Khởi tạo hoặc cập nhật trạng thái Processing
            if (message is null)
            {
                message = InboxMessage.Create(Guid.NewGuid(), handler, messageId, now);
                message.MarkAsProcessing(now, now.AddMinutes(5));
                _context.InboxMessages.Add(message);
            }
            else
            {
                message.MarkAsProcessing(now, now.AddMinutes(5));
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                _context.ChangeTracker.Clear();

                var existing = await _context.InboxMessages.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Handler == handler && m.MessageId == messageId, cancellationToken);

                if (existing?.Status == InboxMessageStatus.Processed)
                {
                    return new InboxExecutionResult<T>(
                        InboxProcessingStatus.AlreadyProcessed,
                        RetryCount: existing.RetryCount);
                }

                if (existing?.Status == InboxMessageStatus.DeadLettered)
                {
                    return new InboxExecutionResult<T>(
                        InboxProcessingStatus.DeadLettered,
                        Error: Error.Failure("ALREADY_DEAD_LETTERED", existing.Error ?? "Thông điệp đã chuyển vào DLQ."),
                        RetryCount: existing.RetryCount);
                }

                // Chưa hoàn tất thành công (đang Processing hoặc Failed): không commit offset, trả TransientFailure để retry
                return new InboxExecutionResult<T>(
                    InboxProcessingStatus.TransientFailure,
                    Error: Error.Conflict("CONCURRENT_INBOX_RACE", "Phát hiện xử lý đồng thời receipt Inbox; không commit offset."),
                    RetryCount: existing?.RetryCount ?? 0);
            }

            // 4. Thực thi logic nghiệp vụ của handler
            Result<T> opResult;
            try
            {
                opResult = await operation(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                opResult = Error.Failure("HANDLER_EXECUTION_THREW", ex.Message);
            }

            // 5. Nếu nghiệp vụ thành công -> Ghi ProcessedAt và commit transaction
            if (opResult.IsSuccess)
            {
                message.MarkAsProcessed(DateTimeOffset.UtcNow);

                if (isOwner && transaction is not null)
                {
                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }

                return new InboxExecutionResult<T>(
                    InboxProcessingStatus.Success,
                    opResult.Value,
                    RetryCount: message.RetryCount);
            }

            // 6. Nếu nghiệp vụ thất bại -> Rollback transaction
            if (isOwner && transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            _context.ChangeTracker.Clear();

            // 7. Ghi nhận failure hoặc chuyển DLQ trong transaction độc lập
            return await RecordFailureOrDeadLetterAsync<T>(
                handler,
                messageId,
                message.RetryCount,
                opResult.Error,
                now,
                dlqMessageFactory,
                cancellationToken);
        }
        catch
        {
            if (isOwner && transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            _context.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<InboxExecutionResult<T>> RecordFailureOrDeadLetterAsync<T>(
        string handler,
        string messageId,
        int currentRetryCount,
        Error error,
        DateTimeOffset now,
        Func<int, string, OutboxMessage>? dlqMessageFactory,
        CancellationToken cancellationToken)
    {
        await using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            InboxMessage? msg;
            if (_context.Database.IsNpgsql())
            {
                msg = await _context.InboxMessages
                    .FromSqlInterpolated($"SELECT * FROM inventory.inbox_messages WHERE handler = {handler} AND message_id = {messageId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                msg = await _context.InboxMessages
                    .FirstOrDefaultAsync(m => m.Handler == handler && m.MessageId == messageId, cancellationToken);
            }

            if (msg is null)
            {
                msg = InboxMessage.Create(Guid.NewGuid(), handler, messageId, now);
            }

            // Bảo vệ không đảo trạng thái: nếu đã Processed hoặc DeadLettered thì không ghi đè và không stage lại DLQ
            if (msg.Status == InboxMessageStatus.Processed)
            {
                await tx.CommitAsync(cancellationToken);
                return new InboxExecutionResult<T>(
                    InboxProcessingStatus.AlreadyProcessed,
                    RetryCount: msg.RetryCount);
            }

            if (msg.Status == InboxMessageStatus.DeadLettered)
            {
                await tx.CommitAsync(cancellationToken);
                return new InboxExecutionResult<T>(
                    InboxProcessingStatus.DeadLettered,
                    Error: Error.Failure("ALREADY_DEAD_LETTERED", msg.Error ?? "Thông điệp đã vào DLQ."),
                    RetryCount: msg.RetryCount);
            }
            if (msg.RetryCount >= 4)
            {
                // Cạn budget -> Terminal state DeadLettered
                msg.MarkAsDeadLettered(error.Message, DateTimeOffset.UtcNow);

                if (_context.Entry(msg).State == EntityState.Detached)
                {
                    _context.InboxMessages.Add(msg);
                }

                // Stage Outbox DLQ atomically
                if (dlqMessageFactory is not null)
                {
                    var dlqOutbox = dlqMessageFactory(msg.RetryCount, error.Code);
                    _context.OutboxMessages.Add(dlqOutbox);
                }

                await _context.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return new InboxExecutionResult<T>(
                    InboxProcessingStatus.DeadLettered,
                    Error: error,
                    RetryCount: msg.RetryCount);
            }

            // Còn budget -> Lên lịch retry theo exponential backoff
            var nextAttemptAt = RetryBackoffCalculator.CalculateNextAttemptAt(DateTimeOffset.UtcNow, msg.RetryCount + 1);
            msg.RecordFailure(error.Message, nextAttemptAt);

            if (_context.Entry(msg).State == EntityState.Detached)
            {
                _context.InboxMessages.Add(msg);
            }

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new InboxExecutionResult<T>(
                InboxProcessingStatus.TransientFailure,
                Error: error,
                RetryCount: msg.RetryCount);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
