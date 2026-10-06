using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Dprental.InventoryService.Domain.Kafka.Contracts;
using Dprental.InventoryService.Domain.Kafka.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Domain.Kafka.Transport;

/// <summary>
/// Quản lý khởi tạo và kiểm tra tính hợp lệ của các baseline topics và DLQ trên Kafka.
/// Trong Development: Idempotent provision topics (mặc định 3 partitions, RF 1).
/// Trong Production: Chỉ kiểm tra danh sách topics đã được deployment tạo trước.
/// Khi broker outage: Ghi nhận log cảnh báo và không ném lỗi làm sập ứng dụng API.
/// </summary>
public sealed class KafkaTopicProvisioner
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaTopicProvisioner>? _logger;

    public KafkaTopicProvisioner(IOptions<KafkaOptions> options, ILogger<KafkaTopicProvisioner>? logger = null)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Thực hiện provision hoặc xác thực topics khi khởi động dịch vụ.
    /// </summary>
    public async Task EnsureTopicsAsync(CancellationToken cancellationToken = default)
    {
        var adminConfig = new AdminClientConfig
        {
            BootstrapServers = _options.BootstrapServers,
            ClientId = $"{_options.ClientId}-admin",
            SocketTimeoutMs = 5000
        };

        try
        {
            using var adminClient = new AdminClientBuilder(adminConfig).Build();
            var requiredTopics = KafkaTopics.AllBaselineAndDlqTopics;

            Metadata metadata;
            try
            {
                // Thử kết nối lấy danh sách metadata topics hiện có
                metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Kafka broker tại {BootstrapServers} hiện không phản hồi. Bỏ qua khởi tạo topics lúc này để đảm bảo API sẵn sàng nhận transaction PostgreSQL.",
                    _options.BootstrapServers);
                return;
            }

            var existingTopicNames = new HashSet<string>(metadata.Topics.Select(t => t.Topic), StringComparer.Ordinal);
            var missingTopics = requiredTopics.Where(t => !existingTopicNames.Contains(t)).ToList();

            if (missingTopics.Count == 0)
            {
                _logger?.LogInformation("Toàn bộ {Count} topics baseline và DLQ đã sẵn sàng trên broker.", requiredTopics.Count);
                return;
            }

            if (_options.AutoProvisionTopics)
            {
                _logger?.LogInformation(
                    "Đang provision {Count} topics còn thiếu: [{Topics}] với {Partitions} partitions và replication factor {Rf}...",
                    missingTopics.Count,
                    string.Join(", ", missingTopics),
                    _options.DefaultPartitions,
                    _options.DefaultReplicationFactor);

                var topicSpecs = missingTopics.Select(topic => new TopicSpecification
                {
                    Name = topic,
                    NumPartitions = _options.DefaultPartitions,
                    ReplicationFactor = _options.DefaultReplicationFactor
                }).ToList();

                try
                {
                    await adminClient.CreateTopicsAsync(topicSpecs);
                    _logger?.LogInformation("Đã provision thành công {Count} topics trên Kafka.", missingTopics.Count);
                }
                catch (CreateTopicsException ex)
                {
                    // Xử lý idempotent: nếu topic đã được tạo bởi tiến trình/replica khác
                    foreach (var result in ex.Results)
                    {
                        if (result.Error.Code == ErrorCode.TopicAlreadyExists)
                        {
                            _logger?.LogDebug("Topic {Topic} đã tồn tại trên broker.", result.Topic);
                        }
                        else
                        {
                            _logger?.LogWarning(
                                "Lỗi khi tạo topic {Topic}: {ErrorCode} - {Reason}",
                                result.Topic,
                                result.Error.Code,
                                result.Error.Reason);
                        }
                    }
                }
            }
            else
            {
                _logger?.LogWarning(
                    "CẢNH BÁO PRODUCTION: Phát hiện {Count} topics chưa được deployment provision trên broker: [{Topics}].",
                    missingTopics.Count,
                    string.Join(", ", missingTopics));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Không thể kết nối hoặc khởi tạo Kafka topics tại {BootstrapServers}. Dịch vụ vẫn khởi động và lưu dữ liệu vào PostgreSQL bình thường.",
                _options.BootstrapServers);
        }
    }
}
