namespace Dprental.Gateway.Security;

/// <summary>Danh tính đã xác thực cần chuyển tiếp tới downstream service.</summary>
public sealed record IdentityContext(string? UserId, string? PartyId, IReadOnlyList<string> Roles, bool IsAuthenticated)
{
    public static readonly IdentityContext Anonymous = new(null, null, Array.Empty<string>(), false);
}
