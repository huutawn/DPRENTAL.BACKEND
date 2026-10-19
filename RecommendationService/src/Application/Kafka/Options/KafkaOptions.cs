namespace Dprental.RecommendationService.Application.Kafka.Options;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "127.0.0.1:9092";

    public string InventoryTopic { get; set; } = "inventory.behavior.events.v1";
    public string InventoryConsumerGroup { get; set; } = "recommendation-inventory-behavior-v1";
    public string InventoryDlqTopic { get; set; } = "inventory.behavior.events.v1.recommendation.dlq";

    public string RentalTopic { get; set; } = "rental.events.v1";
    public string RentalConsumerGroup { get; set; } = "recommendation-rental-behavior-v1";
    public string RentalDlqTopic { get; set; } = "rental.events.v1.recommendation.dlq";

    public bool Enabled { get; set; } = true;
    public string ClientId { get; set; } = "recommendation-service";
    public int SessionTimeoutMs { get; set; } = 45000;
    public int MaxPollIntervalMs { get; set; } = 300000;
    public int MaxRetryAttemptsPerRound { get; set; } = 5;
    public int InitialRetryDelayMs { get; set; } = 1000;
    public int MaxRetryDelayMs { get; set; } = 30000;
}
