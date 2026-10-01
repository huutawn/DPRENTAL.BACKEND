namespace Dprental.InventoryService.Application.Kafka.Contracts;

/// <summary>
/// Định nghĩa 5 topics baseline và các hằng số liên quan của Inventory Service.
/// Mỗi topic đều có một Dead Letter Queue (DLQ) tương ứng dạng ${topic}.dlq.
/// </summary>
public static class KafkaTopics
{
    public const string CatalogEquipmentEventsV1 = "catalog.equipment.events.v1";
    public const string InventoryReservationCommandsV1 = "inventory.reservation.commands.v1";
    public const string InventoryReservationEventsV1 = "inventory.reservation.events.v1";
    public const string InventoryEquipmentEventsV1 = "inventory.equipment.events.v1";
    public const string InventoryDamageEventsV1 = "inventory.damage.events.v1";

    public static readonly IReadOnlyList<string> BaselineTopics =
    [
        CatalogEquipmentEventsV1,
        InventoryReservationCommandsV1,
        InventoryReservationEventsV1,
        InventoryEquipmentEventsV1,
        InventoryDamageEventsV1
    ];

    /// <summary>
    /// Tạo tên topic DLQ tương ứng theo quy ước ${topic}.dlq.
    /// </summary>
    public static string GetDlqTopic(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        return $"{topic}.dlq";
    }

    /// <summary>
    /// Danh sách tất cả topic baseline và DLQ tương ứng (10 topics).
    /// </summary>
    public static IReadOnlyList<string> AllBaselineAndDlqTopics =>
        BaselineTopics.Concat(BaselineTopics.Select(GetDlqTopic)).ToArray();
}
