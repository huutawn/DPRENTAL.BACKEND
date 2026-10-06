using System.Collections.Concurrent;

namespace Dprental.InventoryService.Domain.Kafka.Contracts;

/// <summary>
/// Quản lý đăng ký handlers cho các topics và consumer groups.
/// </summary>
public sealed class KafkaHandlerRegistry
{
    private readonly ConcurrentDictionary<string, HandlerRegistration> _registrations = new(StringComparer.Ordinal);

    /// <summary>
    /// Đăng ký một message handler cho topic, type, version, handler name và group ID.
    /// </summary>
    public KafkaHandlerRegistry Register<THandler, TPayload>(
        string topic,
        string messageType,
        int version,
        string handlerName,
        string groupId)
        where THandler : class, IMessageHandler<TPayload>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version), "Version phải lớn hơn 0.");
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var reg = new HandlerRegistration
        {
            Topic = topic,
            MessageType = messageType,
            Version = version,
            HandlerName = handlerName,
            GroupId = groupId,
            HandlerType = typeof(THandler),
            PayloadType = typeof(TPayload)
        };

        if (!_registrations.TryAdd(reg.RouteKey, reg))
        {
            throw new InvalidOperationException(
                $"Duplicate handler registration: Group '{groupId}', Topic '{topic}', MessageType '{messageType}', Version '{version}' already registered.");
        }
        return this;
    }

    /// <summary>
    /// Tìm đăng ký handler theo consumer group ID, topic, message type và version.
    /// </summary>
    public HandlerRegistration? Find(string groupId, string topic, string messageType, int version)
    {
        var key = $"{groupId}::{topic}::{messageType}::v{version}";
        return _registrations.TryGetValue(key, out var reg) ? reg : null;
    }

    /// <summary>
    /// Tìm đăng ký handler đầu tiên khớp với topic, message type và version (nếu không xét groupId).
    /// </summary>
    public HandlerRegistration? Find(string topic, string messageType, int version)
    {
        return _registrations.Values.FirstOrDefault(r =>
            string.Equals(r.Topic, topic, StringComparison.Ordinal) &&
            string.Equals(r.MessageType, messageType, StringComparison.Ordinal) &&
            r.Version == version);
    }

    /// <summary>
    /// Lấy danh sách toàn bộ handlers đã đăng ký.
    /// </summary>
    public IReadOnlyList<HandlerRegistration> GetAll() => _registrations.Values.ToList();

    /// <summary>
    /// Lấy danh sách tất cả các GroupId độc lập có handlers đăng ký.
    /// </summary>
    public IReadOnlyList<string> GetDistinctGroupIds() =>
        _registrations.Values.Select(r => r.GroupId).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Lấy danh sách handlers thuộc về một GroupId cụ thể.
    /// </summary>
    public IReadOnlyList<HandlerRegistration> GetByGroupId(string groupId) =>
        _registrations.Values.Where(r => r.GroupId == groupId).ToList();
}
