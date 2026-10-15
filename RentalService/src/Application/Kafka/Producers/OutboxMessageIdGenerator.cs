using System.Security.Cryptography;
using System.Text;

namespace Dprental.RentalService.Application.Kafka.Producers;

public static class OutboxMessageIdGenerator
{
    /// <summary>
    /// Computes a deterministic MessageId from producer, messageType, and logical operation identity.
    /// Ensures replay safety and atomic outbox deduplication without additional DB tables.
    /// </summary>
    public static Guid ComputeDeterministicId(string producer, string messageType, string logicalOperationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalOperationIdentity);

        string raw = $"{producer.Trim()}:{messageType.Trim()}:{logicalOperationIdentity.Trim()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
