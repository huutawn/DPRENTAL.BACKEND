using System.Security.Cryptography;
using System.Text;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Triển khai dịch vụ chống trùng lặp yêu cầu API với mã băm SHA-256 fingerprint.
/// Quản lý phát hiện replay và ngăn ngừa reuse key sai khác (RFC 9110 / RFC 7807).
/// </summary>
public sealed class IdempotencyService : IIdempotencyService
{
    private static readonly TimeSpan DefaultRetentionPeriod = TimeSpan.FromDays(30);

    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly ILogger<IdempotencyService>? _logger;

    public IdempotencyService(
        IIdempotencyRepository idempotencyRepository,
        ILogger<IdempotencyService>? logger = null)
    {
        _idempotencyRepository = idempotencyRepository ?? throw new ArgumentNullException(nameof(idempotencyRepository));
        _logger = logger;
    }

    /// <inheritdoc />
    public string ComputeFingerprint(
        Guid actorId,
        string httpMethod,
        string requestPath,
        string? requestBody,
        string? ifMatch)
    {
        string method = httpMethod?.ToUpperInvariant() ?? string.Empty;
        string path = requestPath ?? string.Empty;
        string body = requestBody ?? string.Empty;
        string match = ifMatch ?? string.Empty;

        string raw = $"{actorId}:{method}:{path}:{body}:{match}";
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        byte[] hash = SHA256.HashData(bytes);

        return Convert.ToHexStringLower(hash);
    }

    /// <inheritdoc />
    public async Task<Result<IdempotencyRecord?>> CheckAsync(
        Guid actorId,
        string idempotencyKey,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<IdempotencyRecord?>(
                Error.Validation("IDEMPOTENCY_KEY_REQUIRED", "Khóa Idempotency-Key là bắt buộc đối với yêu cầu này."));
        }

        if (idempotencyKey.Length > 64)
        {
            return Result.Failure<IdempotencyRecord?>(
                Error.Validation("IDEMPOTENCY_KEY_INVALID", "Khóa Idempotency-Key không được vượt quá 64 ký tự."));
        }

        var existingRecord = await _idempotencyRepository.GetAsync(actorId, idempotencyKey, cancellationToken);
        if (existingRecord == null)
        {
            return Result.Success<IdempotencyRecord?>(null);
        }

        if (string.Equals(existingRecord.RequestFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation(
                "Phát hiện yêu cầu lặp lại hợp lệ (Key Replay) cho actor {ActorId}, key {IdempotencyKey}.",
                actorId, idempotencyKey);

            return Result.Success<IdempotencyRecord?>(existingRecord);
        }

        _logger?.LogWarning(
            "Phát hiện tái sử dụng khóa Idempotency-Key với nội dung khác biệt (Mismatch) cho actor {ActorId}, key {IdempotencyKey}.",
            actorId, idempotencyKey);

        return Result.Failure<IdempotencyRecord?>(
            Error.Conflict(
                "IDEMPOTENCY_KEY_REUSE_MISMATCH",
                "Khóa Idempotency-Key đã được sử dụng trước đó cho một yêu cầu có nội dung hoặc phiên bản khác."));
    }

    /// <inheritdoc />
    public async Task<Result<IdempotencyRecord?>> CheckAsync(
        Guid actorId,
        string idempotencyKey,
        string httpMethod,
        string requestPath,
        string? requestBody,
        string? ifMatch,
        CancellationToken cancellationToken = default)
    {
        string fingerprint = ComputeFingerprint(actorId, httpMethod, requestPath, requestBody, ifMatch);
        return await CheckAsync(actorId, idempotencyKey, fingerprint, cancellationToken);
    }

    /// <inheritdoc />
    public IdempotencyRecord CreateRecord(
        Guid actorId,
        string idempotencyKey,
        string requestFingerprint,
        string httpMethod,
        string requestPath,
        int responseStatusCode,
        string? responseHeaders,
        string responseBody,
        DateTimeOffset? createdAt = null,
        TimeSpan? retentionPeriod = null)
    {
        DateTimeOffset now = createdAt ?? DateTimeOffset.UtcNow;
        TimeSpan retention = retentionPeriod ?? DefaultRetentionPeriod;

        return new IdempotencyRecord(
            id: Guid.NewGuid(),
            actorId: actorId,
            idempotencyKey: idempotencyKey,
            requestFingerprint: requestFingerprint,
            httpMethod: httpMethod?.ToUpperInvariant() ?? string.Empty,
            requestPath: requestPath ?? string.Empty,
            responseStatusCode: responseStatusCode,
            responseHeaders: responseHeaders,
            responseBody: responseBody ?? string.Empty,
            createdAt: now,
            expiresAt: now.Add(retention));
    }

    /// <inheritdoc />
    public async Task SaveRecordAsync(
        IdempotencyRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _idempotencyRepository.SaveAsync(record, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IdempotencyRecord> RecordResponseAsync(
        Guid actorId,
        string idempotencyKey,
        string requestFingerprint,
        string httpMethod,
        string requestPath,
        int responseStatusCode,
        string? responseHeaders,
        string responseBody,
        DateTimeOffset? createdAt = null,
        TimeSpan? retentionPeriod = null,
        CancellationToken cancellationToken = default)
    {
        var record = CreateRecord(
            actorId,
            idempotencyKey,
            requestFingerprint,
            httpMethod,
            requestPath,
            responseStatusCode,
            responseHeaders,
            responseBody,
            createdAt,
            retentionPeriod);

        await SaveRecordAsync(record, cancellationToken);
        return record;
    }
}
