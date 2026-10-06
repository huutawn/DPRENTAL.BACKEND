namespace Dprental.InventoryService.Domain.Kafka.Contracts;

/// <summary>
/// Định nghĩa các topics baseline của Inventory Service theo target domain:
/// inventory.allocation.commands.v1, inventory.allocation.events.v1, inventory.asset.events.v1.
/// Mỗi topic đều có một Dead Letter Queue (DLQ) tương ứng dạng ${topic}.dlq.
/// </summary>
public static class KafkaTopics
{
    public const string InventoryAllocationCommandsV1 = "inventory.allocation.commands.v1";
    public const string InventoryAllocationEventsV1 = "inventory.allocation.events.v1";
    public const string InventoryAssetEventsV1 = "inventory.asset.events.v1";

    public static readonly IReadOnlyList<string> BaselineTopics =
    [
        InventoryAllocationCommandsV1,
        InventoryAllocationEventsV1,
        InventoryAssetEventsV1
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
    /// Danh sách tất cả topic baseline và DLQ tương ứng (6 topics).
    /// </summary>
    public static IReadOnlyList<string> AllBaselineAndDlqTopics =>
        BaselineTopics.Concat(BaselineTopics.Select(GetDlqTopic)).ToArray();
}
