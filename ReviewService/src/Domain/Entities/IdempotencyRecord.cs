using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.ReviewService.Domain.Entities;

/// <summary>
/// Thực thể lưu trữ bản ghi chống trùng lặp yêu cầu API (Idempotency Record).
/// </summary>
public sealed class IdempotencyRecord
{
    /// <summary>
    /// Khởi tạo mặc định cho EF Core.
    /// </summary>
    public IdempotencyRecord()
    {
    }

    /// <summary>
    /// Khởi tạo đầy đủ bản ghi chống trùng lặp yêu cầu API.
    /// </summary>
    public IdempotencyRecord(
        Guid id,
        Guid actorId,
        string idempotencyKey,
        string requestFingerprint,
        string httpMethod,
        string requestPath,
        int responseStatusCode,
        string? responseHeaders,
        string responseBody,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        ActorId = actorId;
        IdempotencyKey = idempotencyKey ?? string.Empty;
        RequestFingerprint = requestFingerprint ?? string.Empty;
        HttpMethod = httpMethod ?? string.Empty;
        RequestPath = requestPath ?? string.Empty;
        ResponseStatusCode = responseStatusCode;
        ResponseHeaders = responseHeaders;
        ResponseBody = responseBody ?? string.Empty;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>Mã định danh bản ghi (UUID).</summary>
    public Guid Id { get; set; }

    /// <summary>Mã người thực hiện từ JWT.</summary>
    public Guid ActorId { get; set; }

    /// <summary>Khóa chống trùng lặp từ header Idempotency-Key.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Mã băm SHA-256 fingerprint của yêu cầu.</summary>
    public string RequestFingerprint { get; set; } = string.Empty;

    /// <summary>Phương thức HTTP (POST, PUT, ...).</summary>
    public string HttpMethod { get; set; } = string.Empty;

    /// <summary>Đường dẫn API của yêu cầu.</summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>Mã trạng thái HTTP phản hồi (200, 201, ...).</summary>
    public int ResponseStatusCode { get; set; }

    /// <summary>Các header phản hồi được lưu trữ kèm (JSON hoặc chuỗi định dạng).</summary>
    public string? ResponseHeaders { get; set; }

    /// <summary>Nội dung phản hồi được lưu trữ đệm (JSON).</summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>Thời điểm ghi nhận yêu cầu (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Thời điểm hết hạn hiệu lực chống trùng lặp (UTC).</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Bí danh cho IdempotencyKey theo quy ước ERD.</summary>
    [NotMapped]
    public string Key
    {
        get => IdempotencyKey;
        set => IdempotencyKey = value;
    }

    /// <summary>Bí danh cho RequestFingerprint theo quy ước ERD.</summary>
    [NotMapped]
    public string RequestHash
    {
        get => RequestFingerprint;
        set => RequestFingerprint = value;
    }

    /// <summary>Bí danh cho ResponseStatusCode theo quy ước ERD.</summary>
    [NotMapped]
    public int ResponseStatus
    {
        get => ResponseStatusCode;
        set => ResponseStatusCode = value;
    }
}
