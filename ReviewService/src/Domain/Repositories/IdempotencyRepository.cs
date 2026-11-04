using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.ReviewService.Domain.Repositories;

/// <summary>
/// Triển khai EF Core cho IIdempotencyRepository với PostgreSQL advisory xact lock và xử lý đồng thời.
/// </summary>
public sealed class IdempotencyRepository : IIdempotencyRepository
{
    private readonly ReviewServiceDbContext _dbContext;

    public IdempotencyRepository(ReviewServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IdempotencyRecord?> GetAsync(
        Guid actorId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        return await _dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.ActorId == actorId && r.IdempotencyKey == idempotencyKey,
                cancellationToken);
    }

    public async Task<Result<IdempotencyRecord>> SaveAsync(
        IdempotencyRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var lockKey = $"{record.ActorId:N}:{record.IdempotencyKey}";

        if (!_dbContext.Database.IsRelational())
        {
            return await ExecuteSaveLogicAsync(record, cancellationToken);
        }

        if (_dbContext.Database.CurrentTransaction != null)
        {
            var param = new NpgsqlParameter("lockKey", lockKey);
            await _dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext(@lockKey))",
                [param],
                cancellationToken);

            return await ExecuteSaveLogicAsync(record, cancellationToken);
        }

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var param = new NpgsqlParameter("lockKey", lockKey);
            await _dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext(@lockKey))",
                [param],
                cancellationToken);

            var result = await ExecuteSaveLogicAsync(record, cancellationToken);
            if (result.IsSuccess)
            {
                await tx.CommitAsync(cancellationToken);
            }
            else
            {
                await tx.RollbackAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<Result<IdempotencyRecord>> ExecuteSaveLogicAsync(
        IdempotencyRecord record,
        CancellationToken cancellationToken)
    {
        // Re-read receipt after acquiring lock
        var existing = await _dbContext.IdempotencyRecords
            .FirstOrDefaultAsync(
                r => r.ActorId == record.ActorId && r.IdempotencyKey == record.IdempotencyKey,
                cancellationToken);

        if (existing != null)
        {
            var now = DateTimeOffset.UtcNow;
            bool isExpired = existing.ExpiresAt <= now || existing.ExpiresAt <= record.CreatedAt;

            if (isExpired)
            {
                // Atomically replace under the same lock
                existing.RequestFingerprint = record.RequestFingerprint;
                existing.HttpMethod = record.HttpMethod;
                existing.RequestPath = record.RequestPath;
                existing.ResponseStatusCode = record.ResponseStatusCode;
                existing.ResponseHeaders = record.ResponseHeaders;
                existing.ResponseBody = record.ResponseBody;
                existing.CreatedAt = record.CreatedAt;
                existing.ExpiresAt = record.ExpiresAt;

                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return Result.Success(existing);
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                {
                    return await HandleUniqueViolationAsync(record, cancellationToken);
                }
            }

            // Same fingerprint: return existing receipt
            if (string.Equals(existing.RequestFingerprint, record.RequestFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success(existing);
            }

            // Different fingerprint: return a Conflict result (indicate payload mismatch, 409)
            return Result.Failure<IdempotencyRecord>(
                Error.Conflict(
                    "IDEMPOTENCY_KEY_REUSE_MISMATCH",
                    "Khóa Idempotency-Key đã được sử dụng trước đó cho một yêu cầu có nội dung khác."));
        }

        try
        {
            await _dbContext.IdempotencyRecords.AddAsync(record, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success(record);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            return await HandleUniqueViolationAsync(record, cancellationToken);
        }
    }

    private async Task<Result<IdempotencyRecord>> HandleUniqueViolationAsync(
        IdempotencyRecord record,
        CancellationToken cancellationToken)
    {
        var reRead = await _dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.ActorId == record.ActorId && r.IdempotencyKey == record.IdempotencyKey,
                cancellationToken);

        if (reRead != null)
        {
            if (string.Equals(reRead.RequestFingerprint, record.RequestFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Success(reRead);
            }

            return Result.Failure<IdempotencyRecord>(
                Error.Conflict(
                    "IDEMPOTENCY_KEY_REUSE_MISMATCH",
                    "Khóa Idempotency-Key đã được sử dụng trước đó cho một yêu cầu có nội dung khác."));
        }

        throw new DbUpdateException("Unique constraint violation occurred on idempotency record, but record could not be re-read.");
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (ex.InnerException is PostgresException pgEx)
        {
            if (pgEx.SqlState == "23505")
            {
                return pgEx.ConstraintName is "uq_idempotency_records_actor_key" or "uq_idempotency_actor_key";
            }

            return false;
        }

        var message = ex.Message + (ex.InnerException?.Message ?? string.Empty);
        return message.Contains("uq_idempotency_records_actor_key", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("uq_idempotency_actor_key", StringComparison.OrdinalIgnoreCase);
    }
}
