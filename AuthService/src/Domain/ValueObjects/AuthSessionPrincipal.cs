using System;
using System.Collections.Generic;
using System.Text;

// Chỉ giữ lại 1 namespace chuẩn nhất của project
namespace Dprental.AuthService.Domain.ValueObjects;

/// <summary>Minimal authenticated principal data used by the Auth session flow.</summary>
/// <param name="IsProfileCompleted">Null cho principal không phải user (vd service token) — không phát claim <c>pc</c> khi null.</param>
public sealed record AuthSessionPrincipal(
    Guid UserId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string>? Permissions = null,
    IReadOnlyCollection<Guid>? OrganizationIds = null,
    bool? IsProfileCompleted = null,
    Guid? SessionId = null)
{
    public IReadOnlyCollection<string> Permissions { get; init; } = Permissions ?? [];
    public IReadOnlyCollection<Guid> OrganizationIds { get; init; } = OrganizationIds ?? [];
}