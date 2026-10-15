namespace Dprental.InventoryService.Application.Security;

public sealed class KafkaServiceCallerContext : IInventoryCallerContext
{
    public static readonly KafkaServiceCallerContext Instance = new();

    public bool IsAuthenticated => true;
    public bool IsService => true;
    public bool IsAdmin => false;
    public Guid? UserId => null;
    public IReadOnlySet<string> Permissions => new HashSet<string>();
    public IReadOnlySet<string> Scopes => new HashSet<string> { InventoryAccessEvaluator.ScopeAllocationWrite };
    public IReadOnlySet<string> Roles => new HashSet<string>();
    public Guid? OrganizationId => null;
}
