using System.Security.Claims;
using Dprental.InventoryService.Application.Security;
using Microsoft.AspNetCore.Http;

namespace Dprental.InventoryService.Api.Security;

/// <summary>
/// Triển khai IInventoryCallerContext trích xuất danh tính và phân quyền từ ClaimsPrincipal của HTTP request hiện tại.
/// Tuyệt đối không đọc từ các header X-User-Id, X-Role, X-Organization-Id.
/// </summary>
public sealed class InventoryCallerContext : IInventoryCallerContext
{
    private readonly ClaimsPrincipal? _user;

    public InventoryCallerContext(IHttpContextAccessor httpContextAccessor)
    {
        _user = httpContextAccessor.HttpContext?.User;
    }

    // Constructor phục vụ testing trực tiếp với ClaimsPrincipal
    public InventoryCallerContext(ClaimsPrincipal? user)
    {
        _user = user;
    }

    /// <inheritdoc />
    public bool IsAuthenticated => _user?.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public bool IsService => IsAuthenticated && _user?.FindFirst("u")?.Value == "svc";

    /// <inheritdoc />
    public bool IsHuman => IsAuthenticated && !IsService;

    /// <inheritdoc />
    public bool IsAdmin => IsHuman && Roles.Contains("adm");

    /// <inheritdoc />
    public Guid? UserId
    {
        get
        {
            if (!IsAuthenticated) return null;
            string? sub = _user?.FindFirst("sub")?.Value;
            return Guid.TryParse(sub, out Guid id) && id != Guid.Empty ? id : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> Permissions
    {
        get
        {
            if (!IsAuthenticated) return new HashSet<string>();
            return _user?.FindAll("p")
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> Scopes
    {
        get
        {
            if (!IsAuthenticated) return new HashSet<string>();
            var scopes = new HashSet<string>(StringComparer.Ordinal);
            var scopeClaims = _user?.FindAll("s") ?? [];
            foreach (var claim in scopeClaims)
            {
                if (string.IsNullOrWhiteSpace(claim.Value)) continue;
                foreach (var part in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    scopes.Add(part);
                }
            }
            return scopes;
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> Roles
    {
        get
        {
            if (!IsAuthenticated) return new HashSet<string>();
            return _user?.FindAll("r")
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        }
    }

    /// <inheritdoc />
    public Guid? OrganizationId
    {
        get
        {
            if (!IsAuthenticated) return null;
            string? org = _user?.FindFirst("o")?.Value;
            return Guid.TryParse(org, out Guid id) && id != Guid.Empty ? id : null;
        }
    }
}
