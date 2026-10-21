using System.Text.Json;

namespace Dprental.PaymentService.Common.Cryptography;

/// <summary>Durable publication evidence; unlike a write receipt it has no consumer-time TTL.</summary>
public sealed record PublicationProof(string Payload, string Signature)
{
    public const string DomainSeparator = "DPRENTAL:PUBLICATION:V1:";
    public string SigningMessage => DomainSeparator + Payload;
}

public sealed record PublicationProofBody(
    int Schema, string Issuer, string Audience, string Environment, string KeyId,
    Guid MessageId, Guid EventId, Guid OperationId, string Topic, string MessageKey,
    string MessageType, string PayloadDigest, DateTimeOffset IssuedAt)
{
    public string Serialize() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);
}
