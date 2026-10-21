namespace Dprental.PaymentService.Domain.Entities;

public sealed class OperationHeader
{
    public Guid OperationId { get; private set; }
    public string OperationType { get; private set; } = string.Empty;
    public int MemberCount { get; private set; }
    public string MemberDigest { get; private set; } = string.Empty;
    public string EntityClaimsDigest { get; private set; } = string.Empty;
    public string Actor { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    private OperationHeader() { }

    public static OperationHeader Create(
        Guid operationId,
        string operationType,
        int memberCount,
        string memberDigest,
        string entityClaimsDigest,
        string actor,
        DateTimeOffset createdAt)
    {
        return new OperationHeader
        {
            OperationId = operationId,
            OperationType = operationType.Trim(),
            MemberCount = memberCount,
            MemberDigest = memberDigest.Trim().ToLowerInvariant(),
            EntityClaimsDigest = entityClaimsDigest.Trim().ToLowerInvariant(),
            Actor = actor.Trim(),
            CreatedAt = createdAt.ToUniversalTime()
        };
    }
}
