using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    private readonly RentalServiceDbContext _dbContext;

    public OutboxRepository(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RentalOutboxMessage?> ClaimNextPendingAsync(
        string claimToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        DateTimeOffset leaseUntil = utcNow.Add(leaseDuration);

        // SKIP LOCKED là ngoại lệ SQL đặc thù PostgreSQL: EF không có LINQ tương đương.
        // Chọn và khóa message trong cùng thao tác, tránh hai worker nhận cùng message.
        // NOT EXISTS giữ thứ tự theo topic/partition; message trước chưa Published sẽ chặn.
        if (_dbContext.Database.IsNpgsql())
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var messages = await _dbContext.RentalOutboxMessages
                .FromSqlInterpolated($@"
                    WITH candidate AS (
                        SELECT m.*
                        FROM rental.rental_outbox_messages m
                        WHERE m.""Status"" = 'Pending'
                          AND (m.""ScheduledAt"" IS NULL OR m.""ScheduledAt"" <= NOW())
                          AND (m.""LockedUntilUtc"" IS NULL OR m.""LockedUntilUtc"" <= NOW())
                          AND NOT EXISTS (
                              SELECT 1 FROM rental.rental_outbox_messages prev
                              WHERE prev.""Topic"" = m.""Topic""
                                AND prev.""PartitionKey"" = m.""PartitionKey""
                                AND prev.""Sequence"" < m.""Sequence""
                                AND prev.""Status"" <> 'Published'
                          )
                        ORDER BY m.""Sequence""
                        LIMIT 1
                        FOR UPDATE SKIP LOCKED
                    )
                    UPDATE rental.rental_outbox_messages u
                    SET ""LockedBy"" = {claimToken},
                        ""LockedUntilUtc"" = NOW() + ({leaseDuration.TotalSeconds} * interval '1 second')
                    FROM candidate
                    WHERE u.""Id"" = candidate.""Id""
                    RETURNING u.*;")
                .ToListAsync(cancellationToken);

            if (messages.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            var message = messages[0];
            message.Claim(claimToken, leaseUntil);
            await tx.CommitAsync(cancellationToken);
            return message;
        }
        else
        {
            // Provider InMemory của test không có row lock; nhánh này không cung cấp
            // bảo đảm tranh chấp như PostgreSQL và không thay thế nhánh production.
            var message = await _dbContext.RentalOutboxMessages
                .Where(m => m.Status == "Pending" &&
                            (m.ScheduledAt == null || m.ScheduledAt <= utcNow) &&
                            (m.LockedUntilUtc == null || m.LockedUntilUtc <= utcNow) &&
                            !_dbContext.RentalOutboxMessages.Any(prev =>
                                prev.Topic == m.Topic &&
                                prev.PartitionKey == m.PartitionKey &&
                                prev.Sequence < m.Sequence &&
                                prev.Status != "Published"))
                .OrderBy(m => m.Sequence)
                .FirstOrDefaultAsync(cancellationToken);
            if (message != null)
            {
                message.Claim(claimToken, leaseUntil);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return message;
        }
    }


    public async Task<Result<OutboxEnqueueResult>> EnqueueAtomicAsync(
        RentalOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_dbContext.Database.IsRelational())
        {
            // Outbox và thay đổi trạng thái phải commit/rollback cùng transaction của caller.
            // Nếu tự commit riêng, có thể gửi event cho thay đổi nghiệp vụ đã bị rollback.
            if (_dbContext.Database.CurrentTransaction is null)
            {
                return Error.Failure("OUTBOX_TRANSACTION_REQUIRED",
                    "Outbox message must be enqueued inside the state mutation transaction.");
            }

            return await ExecuteInsertOnConflictAsync(message, cancellationToken);
        }
        else
        {
            var existing = await _dbContext.RentalOutboxMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

            if (existing != null)
            {
                return new OutboxEnqueueResult(existing, IsReplay: true);
            }

            try
            {
                await _dbContext.RentalOutboxMessages.AddAsync(message, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return new OutboxEnqueueResult(message, IsReplay: false);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _dbContext.Entry(message).State = EntityState.Detached;
                var concurrent = await _dbContext.RentalOutboxMessages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

                if (concurrent != null)
                {
                    return new OutboxEnqueueResult(concurrent, IsReplay: true);
                }

                return Error.Failure("OUTBOX_ENQUEUE_FAILED", "Failed to enqueue outbox message concurrently.");
            }
        }
    }

    private async Task<Result<OutboxEnqueueResult>> ExecuteInsertOnConflictAsync(
        RentalOutboxMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            // EF chưa hỗ trợ INSERT ... ON CONFLICT DO NOTHING qua LINQ.
            // Giữ SQL ở đây để retry cùng ID không tạo event trùng và không làm hỏng
            // transaction của caller. SaveChanges thay thế sẽ lưu cả entity khác đang tracked.
            int rows = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO rental.rental_outbox_messages (
                    ""Id"",
                    ""Topic"",
                    ""PartitionKey"",
                    ""MessageType"",
                    ""Payload"",
                    ""Status"",
                    ""CreatedAt"",
                    ""ScheduledAt"",
                    ""PublishedAt"",
                    ""LastAttemptedAt"",
                    ""LockedBy"",
                    ""LockedUntilUtc"",
                    ""RetryCount"",
                    ""ErrorMessage""
                ) VALUES (
                    {message.Id},
                    {message.Topic},
                    {message.PartitionKey},
                    {message.MessageType},
                    {message.Payload},
                    {message.Status},
                    {message.CreatedAt},
                    {message.ScheduledAt},
                    {message.PublishedAt},
                    {message.LastAttemptedAt},
                    {message.LockedBy},
                    {message.LockedUntilUtc},
                    {message.RetryCount},
                    {message.ErrorMessage}
                )
                ON CONFLICT (""Id"") DO NOTHING;", cancellationToken);

            if (rows > 0)
            {
                return new OutboxEnqueueResult(message, IsReplay: false);
            }

            var existing = await _dbContext.RentalOutboxMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

            if (existing != null)
            {
                return new OutboxEnqueueResult(existing, IsReplay: true);
            }

            return Error.Failure("OUTBOX_NOT_FOUND_AFTER_CONFLICT", $"MessageId '{message.Id}' had conflict but could not be read.");
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            var existing = await _dbContext.RentalOutboxMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == message.Id, cancellationToken);

            if (existing != null)
            {
                return new OutboxEnqueueResult(existing, IsReplay: true);
            }

            return Error.Failure("OUTBOX_NOT_FOUND_AFTER_CONFLICT", $"MessageId '{message.Id}' had unique violation but could not be read.");
        }
    }

    public async Task AddAsync(RentalOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _dbContext.RentalOutboxMessages.AddAsync(message, cancellationToken);
    }

    public async Task<bool> MarkPublishedAsync(
        Guid id,
        string claimToken,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            // Kiểm tra token, trạng thái và lease ngay trong UPDATE (compare-and-set).
            // SELECT rồi SaveChanges sẽ có khoảng trống để worker khác giành lease.
            // UtcNow nằm trong expression để Npgsql dùng đồng hồ DB, như NOW() trước đây.
            int rows = await _dbContext.RentalOutboxMessages
                .Where(m => m.Id == id && m.Status == "Pending" && m.LockedBy == claimToken &&
                    (m.LockedUntilUtc == null || m.LockedUntilUtc > DateTimeOffset.UtcNow))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(m => m.Status, "Published")
                    .SetProperty(m => m.PublishedAt, publishedAt)
                    .SetProperty(m => m.LockedBy, (string?)null)
                    .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null), cancellationToken);

            return rows == 1;
        }
        else
        {
            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            var msg = await _dbContext.RentalOutboxMessages
                .FirstOrDefaultAsync(m => m.Id == id && m.LockedBy == claimToken && m.Status == "Pending" && (m.LockedUntilUtc == null || m.LockedUntilUtc >= utcNow), cancellationToken);

            if (msg == null)
            {
                return false;
            }

            msg.MarkPublished(publishedAt);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    public async Task<bool> RecordFailureAsync(
        Guid id,
        string claimToken,
        string errorMessage,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextScheduledAt = null,
        string? targetStatus = null,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            // Worker có thể yêu cầu Pending để tiếp tục retry quá ngưỡng mặc định.
            // Giữ contract cũ: chỉ Pending/DeadLettered là override; giá trị khác dùng ngưỡng 10.
            // Tăng retry, ghi lỗi, lên lịch và nhả lease trong một UPDATE nguyên tử.
            bool automaticStatus = targetStatus != "Pending" && targetStatus != "DeadLettered";
            int rows = await _dbContext.RentalOutboxMessages
                .Where(m => m.Id == id && m.Status == "Pending" && m.LockedBy == claimToken &&
                    (m.LockedUntilUtc == null || m.LockedUntilUtc > DateTimeOffset.UtcNow))
                .ExecuteUpdateAsync(setters =>
                {
                    setters.SetProperty(m => m.RetryCount, m => m.RetryCount + 1)
                        .SetProperty(m => m.Status, m =>
                            targetStatus == "DeadLettered" || (automaticStatus && m.RetryCount + 1 >= 10)
                                ? "DeadLettered" : "Pending")
                        .SetProperty(m => m.ErrorMessage, errorMessage)
                        .SetProperty(m => m.LastAttemptedAt, attemptedAt)
                        .SetProperty(m => m.LockedBy, (string?)null)
                        .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null);

                    // Tránh CASE chỉ có hai nhánh NULL: PostgreSQL sẽ suy ra kiểu text,
                    // không phải timestamp. Gán NULL trực tiếp để EF lấy kiểu từ cột đích.
                    if (nextScheduledAt is null)
                        setters.SetProperty(m => m.ScheduledAt, (DateTimeOffset?)null);
                    else
                        setters.SetProperty(m => m.ScheduledAt, m =>
                            targetStatus == "DeadLettered" || (automaticStatus && m.RetryCount + 1 >= 10)
                                ? null : nextScheduledAt);
                }, cancellationToken);

            return rows == 1;
        }
        else
        {
            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            var msg = await _dbContext.RentalOutboxMessages
                .FirstOrDefaultAsync(m => m.Id == id && m.LockedBy == claimToken && m.Status == "Pending" && (m.LockedUntilUtc == null || m.LockedUntilUtc >= utcNow), cancellationToken);

            if (msg == null)
            {
                return false;
            }

            msg.RecordFailure(errorMessage, attemptedAt, nextScheduledAt, targetStatus);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }


    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505";
    }
}
