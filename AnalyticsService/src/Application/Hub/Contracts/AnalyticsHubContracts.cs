namespace Dprental.AnalyticsService.Application.Hub.Contracts;

/// <summary>Tên event SignalR mà FE lắng nghe.</summary>
public static class AnalyticsHubEventNames
{
    public const string DataChanged = "AnalyticsDataChanged";
}

/// <summary>Phạm vi số liệu vừa thay đổi, để FE chỉ tải lại phần liên quan.</summary>
public static class AnalyticsDataScopes
{
    public const string Rental = "Rental";   // Đơn, dòng thuê, thiết bị, NCC
    public const string Finance = "Finance"; // Quyết toán, hoàn tiền
}

/// <summary>
/// Vỏ chung của mọi tin SignalR (cùng dạng PaymentHubEnvelope): EventId để FE chống trùng.
/// </summary>
public sealed record AnalyticsHubEnvelope<TPayload>(Guid EventId, int Version, DateTimeOffset OccurredAt, TPayload Payload);

/// <summary>
/// Chỉ là tín hiệu "số liệu đã đổi". Không chứa con số: FE gọi lại API dashboard (có phân quyền) để lấy số.
/// </summary>
public sealed record AnalyticsDataChangedPayload(string Scope);
