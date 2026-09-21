namespace Dprental.AuthService.Common;

/// <summary>Catalog mã lỗi tập trung của AuthService.</summary>
public static class AuthErrorCode
{
    public static class Organization
    {
        public const string NotFound = "Organization.NotFound";
        public const string Inactive = "Organization.Inactive";
        public const string CrossOrganizationAccessDenied = "Organization.CrossOrganizationAccessDenied";
        public const string Forbidden = "Organization.Forbidden";
        public const string SlugAlreadyExists = "Organization.SlugAlreadyExists";
        public const string MembershipAlreadyExists = "Organization.MembershipAlreadyExists";
        public const string MembershipNotFound = "Organization.MembershipNotFound";
        public const string UserNotFound = "Organization.UserNotFound";
        public const string UserInactive = "Organization.UserInactive";
        public const string RoleNotFound = "Organization.RoleNotFound";
        public const string InvalidRoleScope = "Organization.InvalidRoleScope";
        public const string PrivilegeEscalation = "Organization.PrivilegeEscalation";
        public const string LastOwner = "Organization.LastOwner";
        public const string InvalidStateTransition = "Organization.InvalidStateTransition";
        public const string CannotActivateInvitation = "Organization.CannotActivateInvitation";
        public const string EmptyPatch = "Organization.EmptyPatch";
        public const string NoOpPatch = "Organization.NoOpPatch";
        public const string InvalidSlugFormat = "Organization.InvalidSlugFormat";
        public const string InvalidName = "Organization.InvalidName";
        public const string PlatformAdminRequired = "Organization.PlatformAdminRequired";
        public const string CreationNotAllowed = "Organization.CreationNotAllowed";
    }

    public static class Role
    {
        public const string NotFound = "Role.NotFound";
        public const string CannotSelfAssign = "Role.CannotSelfAssign";
        public const string InsufficientPermission = "Role.InsufficientPermission";
        public const string AlreadyAssigned = "Role.AlreadyAssigned";
        public const string NotAssigned = "Role.NotAssigned";
        public const string AssignmentFailed = "Role.AssignmentFailed";
        public const string InvalidScope = "Role.InvalidScope";
    }

    public static class User
    {
        public const string EmailAlreadyExists = "User.EmailAlreadyExists";
        public const string NotFound = "User.NotFound";
    }

    public static class ServicePrincipal
    {
        public const string InvalidClient = "ServicePrincipal.InvalidClient";
        public const string InvalidScope = "ServicePrincipal.InvalidScope";
    }

    public static class ExternalLogin
    {
        public const string MissingEmail = "ExternalLogin.MissingEmail";
        public const string EmailNotVerified = "ExternalLogin.EmailNotVerified";
        public const string AccountDisabled = "ExternalLogin.AccountDisabled";
        public const string AlreadyLinked = "ExternalLogin.AlreadyLinked";
    }

    public static class Auth
    {
        public const string InvalidCredentials = "Auth.InvalidCredentials";
        public const string InactiveAccount = "Auth.InactiveAccount";
        public const string MissingCookie = "Auth.MissingCookie";
        public const string InvalidRefreshToken = "Auth.InvalidRefreshToken";
    }

    public static class Paging
    {
        public const string InvalidPage = "Paging.InvalidPage";
        public const string InvalidPageSize = "Paging.InvalidPageSize";
    }

    public static class AdminBootstrap
    {
        public const string InvalidKey = "AdminBootstrap.InvalidKey";
    }
}
