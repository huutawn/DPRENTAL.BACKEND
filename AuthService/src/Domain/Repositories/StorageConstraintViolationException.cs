namespace Dprental.AuthService.Domain.Repositories;

/// <summary>Loại vi phạm ràng buộc lưu trữ DB.</summary>
public enum StorageConstraintViolationType
{
    Unique,
    ForeignKey,
    Check,
    NotNull,
    Other
}

/// <summary>
/// Exception duy nhất đại diện cho vi phạm ràng buộc lưu trữ (constraint violation),
/// thay thế các exception Duplicate* riêng lẻ.
/// </summary>
public class StorageConstraintViolationException : Exception
{
    public StorageConstraintViolationType ViolationType { get; }
    public string? ConstraintName { get; }

    public StorageConstraintViolationException(
        StorageConstraintViolationType violationType,
        string? constraintName = null,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"Storage constraint violation: {violationType} ({constraintName})", innerException)
    {
        ViolationType = violationType;
        ConstraintName = constraintName;
    }
}

/// <summary>Tên các constraint / index duy nhất trong DB schema của AuthService.</summary>
public static class StorageConstraints
{
    public const string UserEmail = "IX_Users_Email";
    public const string UserCitizenId = "IX_Users_CitizenId";
    public const string UserRolePrimaryKey = "PK_UserRoles";
    public const string OrganizationSlug = "IX_Organizations_Slug";
    public const string OrganizationTaxCode = "IX_Organizations_TaxCode";
    public const string MembershipUserOrganization = "IX_OrganizationMemberships_UserId_OrganizationId";
    public const string ExternalLoginProviderSubject = "IX_ExternalLogins_Provider_ProviderSubject";
    public const string ExternalLoginUserProvider = "IX_ExternalLogins_UserId_Provider";
    public const string GoogleLoginTicketHash = "IX_GoogleLoginTickets_TicketHash";
    public const string RefreshTokenHash = "IX_RefreshTokens_TokenHash";
    public const string RecoveryTokenHash = "IX_RecoveryTokens_TokenHash";
    public const string RoleName = "IX_Roles_Name";
    public const string ServicePrincipalClientId = "IX_ServicePrincipals_ClientId";
}
