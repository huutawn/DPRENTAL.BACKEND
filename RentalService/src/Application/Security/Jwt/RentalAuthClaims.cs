namespace Dprental.RentalService.Application.Security.Jwt;

/// <summary>
/// Frozen JWT claim identifiers and standard role names matching AuthService catalog.
/// </summary>
public static class RentalAuthClaims
{
    /// <summary>
    /// Subject claim representing the authenticated user UUID or service client UUID.
    /// </summary>
    public const string Subject = "sub";

    /// <summary>
    /// Profile completed claim (boolean true/false). Required for renter checkout.
    /// </summary>
    public const string ProfileCompleted = "pc";

    /// <summary>
    /// Roles claim (string array or comma-separated list).
    /// </summary>
    public const string Roles = "r";

    /// <summary>
    /// Permissions claim (string array or comma-separated list).
    /// </summary>
    public const string Permissions = "p";

    /// <summary>
    /// User identifier / discriminator claim.
    /// In DPRental Auth, service tokens carry a UUID in 'sub' and the literal value 'svc' in 'u'.
    /// </summary>
    public const string UserTypeOrAlias = "u";

    public const string ServiceIndicator = "svc";

    public const string Username = "preferred_username";
    public static class StandardRoles
    {
        public const string Admin = "admin";
        public const string AdminPlatform = "admin_platform";
        public const string Customer = "customer";
        public const string Owner = "owner";
        public const string Member = "member";
    }

    public static class RoleAliases
    {
        public const string Admin = "adm";
        public const string AdminPlatform = "adp";
        public const string Customer = "cus";
        public const string Owner = "own";
        public const string Member = "mem";
    }
}
