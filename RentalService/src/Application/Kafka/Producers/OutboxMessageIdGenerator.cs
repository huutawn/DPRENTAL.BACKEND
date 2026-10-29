using System.Security.Cryptography;
using System.Text;

namespace Dprental.RentalService.Application.Kafka.Producers;

public static class OutboxMessageIdGenerator
{
    /// <summary>
    /// Tạo MessageId ổn định từ producer, loại bản tin và định danh tác vụ bằng 16 byte đầu của SHA-256.
    /// Cùng đầu vào cho cùng ID để Outbox nhận diện replay; khác nội dung vẫn phải được writer kiểm tra.
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

    public static Guid Generate() => Guid.NewGuid();
}
