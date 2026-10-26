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
    private readonly bool _hasValidApiKey;

    public InventoryCallerContext(IHttpContextAccessor httpContextAccessor)
    {
        var context = httpContextAccessor.HttpContext;
        _user = context?.User;
        _hasValidApiKey = context?.Items.ContainsKey("InternalApiKeyVerified") == true;

        if (context != null)
        {
            var headers = context.Request.Headers;
            string? corrId = headers["X-Correlation-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(corrId))
            {
                corrId = context.TraceIdentifier;
            }
            CorrelationId = corrId;

            IsPrefetch = CheckPrefetch(headers);
        }
    }

    // Constructor phục vụ testing trực tiếp với ClaimsPrincipal
    public InventoryCallerContext(
        ClaimsPrincipal? user,
        bool hasValidApiKey = false,
        string? correlationId = null,
        bool isPrefetch = false)
    {
        _user = user;
        _hasValidApiKey = hasValidApiKey || user?.FindFirst("u")?.Value == "svc";
        CorrelationId = correlationId;
        IsPrefetch = isPrefetch;
    }

    /// <inheritdoc />
    public bool HasServiceTokenClaim => _user?.Identity?.IsAuthenticated == true && _user?.FindFirst("u")?.Value == "svc";

    /// <inheritdoc />
    public string? CorrelationId { get; }

    /// <inheritdoc />
    public bool IsPrefetch { get; }

    private static bool CheckPrefetch(IHeaderDictionary? headers)
    {
        if (headers == null) return false;
        string[] prefetchHeaderNames = ["Purpose", "Sec-Purpose", "X-Purpose"];
        char[] separators = [',', ';', ' ', '\t'];

        foreach (var headerName in prefetchHeaderNames)
        {
            if (headers.TryGetValue(headerName, out var values))
            {
                foreach (var val in values)
                {
                    if (string.IsNullOrWhiteSpace(val)) continue;
                    var tokens = val.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var token in tokens)
                    {
                        if (string.Equals(token, "prefetch", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    /// <inheritdoc />
    public bool IsAuthenticated => (_user?.Identity?.IsAuthenticated == true) || _hasValidApiKey;

    /// <inheritdoc />
    public bool IsService => (_hasValidApiKey && (_user?.Identity?.IsAuthenticated != true)) || _user?.FindFirst("u")?.Value == "svc";

    /// <inheritdoc />
    public bool IsHuman => _user?.Identity?.IsAuthenticated == true && !IsService;

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
