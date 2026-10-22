namespace Dprental.InventoryService.Application.Security;

/// <summary>
/// Đại diện ngữ cảnh người gọi ẩn danh (không xác thực danh tính).
/// </summary>
public sealed class AnonymousCallerContext : IInventoryCallerContext
{
    public static readonly AnonymousCallerContext Instance = new();

    public bool IsAuthenticated => false;
    public bool IsService => false;
    public bool IsHuman => false;
    public bool IsAdmin => false;
    public Guid? UserId => null;
    public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
    public IReadOnlySet<string> Scopes { get; } = new HashSet<string>();
    public IReadOnlySet<string> Roles { get; } = new HashSet<string>();
    public Guid? OrganizationId => null;
}
