using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Hợp đồng dịch vụ quản lý chống trùng lặp yêu cầu API (Idempotency).
/// Chịu trách nhiệm băm fingerprint SHA-256, phát hiện replay và ngăn ngừa tái sử dụng key sai khác.
/// </summary>
public interface IIdempotencyService
{
    /// <summary>
    /// Tính toán mã băm SHA-256 fingerprint từ các thành phần yêu cầu:
    /// (actorId + ':' + method + ':' + path + ':' + body + ':' + ifMatch).
    /// </summary>
    /// <param name="actorId">Mã người dùng thực hiện yêu cầu.</param>
    /// <param name="httpMethod">Phương thức HTTP (POST, PUT, ...).</param>
    /// <param name="requestPath">Đường dẫn API của yêu cầu.</param>
    /// <param name="requestBody">Nội dung body của yêu cầu.</param>
    /// <param name="ifMatch">Giá trị header If-Match (nếu có).</param>
    /// <returns>Chuỗi hex 64 ký tự chữ thường biểu diễn mã băm SHA-256.</returns>
    string ComputeFingerprint(
        Guid actorId,
        string httpMethod,
        string requestPath,
        string? requestBody,
        string? ifMatch);

    /// <summary>
    /// Kiểm tra trạng thái của khóa Idempotency-Key với fingerprint đã tính.
    /// - Chưa có: trả về Success(null) để tiếp tục xử lý nghiệp vụ.
    /// - Đã có và trùng fingerprint: trả về Success(record) (Key Replay).
    /// - Đã có nhưng khác fingerprint: trả về Failure(409 Conflict IDEMPOTENCY_KEY_REUSE_MISMATCH).
    /// </summary>
    /// <param name="actorId">Mã người dùng thực hiện.</param>
    /// <param name="idempotencyKey">Khóa idempotency từ header.</param>
    /// <param name="fingerprint">Mã băm fingerprint của yêu cầu hiện tại.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa IdempotencyRecord hoặc null nếu mới, hoặc lỗi 409 Conflict.</returns>
    Task<Result<IdempotencyRecord?>> CheckAsync(
        Guid actorId,
        string idempotencyKey,
        string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra trạng thái của khóa Idempotency-Key và tự động tính toán fingerprint.
    /// </summary>
    /// <param name="actorId">Mã người dùng thực hiện.</param>
    /// <param name="idempotencyKey">Khóa idempotency từ header.</param>
    /// <param name="httpMethod">Phương thức HTTP.</param>
    /// <param name="requestPath">Đường dẫn API.</param>
    /// <param name="requestBody">Nội dung body.</param>
    /// <param name="ifMatch">Header If-Match.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa IdempotencyRecord hoặc null nếu mới, hoặc lỗi 409 Conflict.</returns>
    Task<Result<IdempotencyRecord?>> CheckAsync(
        Guid actorId,
        string idempotencyKey,
        string httpMethod,
        string requestPath,
        string? requestBody,
        string? ifMatch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Khởi tạo thực thể IdempotencyRecord với thời hạn lưu trữ mặc định 30 ngày.
    /// </summary>
    /// <param name="actorId">Mã người dùng thực hiện.</param>
    /// <param name="idempotencyKey">Khóa idempotency.</param>
    /// <param name="requestFingerprint">Mã băm fingerprint.</param>
    /// <param name="httpMethod">Phương thức HTTP.</param>
    /// <param name="requestPath">Đường dẫn API.</param>
    /// <param name="responseStatusCode">Mã trạng thái HTTP phản hồi.</param>
    /// <param name="responseHeaders">Headers phản hồi cần lưu trữ.</param>
    /// <param name="responseBody">Nội dung JSON phản hồi.</param>
    /// <param name="createdAt">Thời điểm tạo (mặc định UtcNow).</param>
    /// <param name="retentionPeriod">Thời gian lưu trữ (mặc định 30 ngày).</param>
    /// <returns>Thực thể IdempotencyRecord.</returns>
    IdempotencyRecord CreateRecord(
        Guid actorId,
        string idempotencyKey,
        string requestFingerprint,
        string httpMethod,
        string requestPath,
        int responseStatusCode,
        string? responseHeaders,
        string responseBody,
        DateTimeOffset? createdAt = null,
        TimeSpan? retentionPeriod = null);

    /// <summary>
    /// Lưu bản ghi IdempotencyRecord vào repository.
    /// </summary>
    /// <param name="record">Bản ghi cần lưu.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    Task SaveRecordAsync(
        IdempotencyRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Khởi tạo và lưu ngay bản ghi phản hồi Idempotency vào repository.
    /// </summary>
    Task<IdempotencyRecord> RecordResponseAsync(
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
        CancellationToken cancellationToken = default);
}
