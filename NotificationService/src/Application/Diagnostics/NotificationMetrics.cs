using System.Diagnostics.Metrics;

namespace Dprental.NotificationService.Application.Diagnostics;

public static class NotificationMetrics
{
    public const string MeterName = "Dprental.NotificationService";
    private static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> IngestionCounter =
        Meter.CreateCounter<long>("notification_ingestion_total", "messages", "Số lượng tin nhắn Kafka đã tiếp nhận");

    public static readonly Counter<long> DeliveryRetryCounter =
        Meter.CreateCounter<long>("notification_delivery_retry_total", "jobs", "Số lượng email delivery job được lên lịch retry");

    public static readonly Counter<long> UncertainDeliveryCounter =
        Meter.CreateCounter<long>("notification_delivery_uncertain_total", "jobs", "Số lượng email delivery job bị rơi vào trạng thái Uncertain");

    public static readonly Counter<long> LeaseRecoveryCounter =
        Meter.CreateCounter<long>("notification_delivery_lease_recovered_total", "jobs", "Số lượng email delivery job được khôi phục sau khi hết hạn lease");

    public static readonly Counter<long> RealtimeFailureCounter =
        Meter.CreateCounter<long>("notification_realtime_failure_total", "events", "Số lượng thông báo realtime SignalR dispatch thất bại");
}
