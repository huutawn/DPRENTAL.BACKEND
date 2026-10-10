namespace Dprental.RentalService.Application.Security;

/// <summary>
/// Frozen caller identity context for RentalService operations.
/// Decouples use cases and controllers from ASP.NET Core HttpContext / ClaimsPrincipal,
/// enabling independent unit and contract testing across services.
/// </summary>
public interface IRentalCallerContext
{
    /// <summary>
    /// Indicates whether the current caller has a verified, valid authenticated identity.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Indicates whether the caller is a background service / worker rather than an interactive user.
    /// In DPRental Auth, service tokens carry a UUID in 'sub' and 'u=svc'.
    /// </summary>
    bool IsService { get; }

    /// <summary>
    /// Unique interactive user ID (from JWT 'sub' claim).
    /// Strictly null when unauthenticated or when the caller is a service token ('u=svc').
    /// </summary>
    Guid? UserId { get; }

    /// <summary>
    /// Preferred username or service identifier.
    /// </summary>
    string? Username { get; }

    /// <summary>
    /// Profile completion flag (from JWT 'pc' claim).
    /// Per DPRental Auth contract, must be boolean true to be eligible for checkout.
    /// Always false for service tokens.
    /// </summary>
    bool IsProfileCompleted { get; }

    /// <summary>
    /// Assigned immutable roles (from JWT 'r' claim), including normalized aliases.
    /// </summary>
    IReadOnlySet<string> Roles { get; }

    /// <summary>
    /// Assigned immutable permissions (from JWT 'p' claim).
    /// </summary>
    IReadOnlySet<string> Permissions { get; }

    /// <summary>
    /// Checks whether the caller has the specified role (matching canonical name or alias).
    /// </summary>
    bool HasRole(string role);

    /// <summary>
    /// Checks whether the caller has the specified permission.
    /// </summary>
    bool HasPermission(string permission);

    /// <summary>
    /// Checks whether the caller has platform or system admin privileges (admin, adm, admin_platform, adp).
    /// </summary>
    bool IsAdmin();
}
