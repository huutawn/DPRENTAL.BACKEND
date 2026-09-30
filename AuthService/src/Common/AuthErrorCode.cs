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
        public const string PendingApproval = "Organization.PendingApproval";
        public const string TaxCodeAlreadyExists = "Organization.TaxCodeAlreadyExists";
        public const string SelfRegistrationTypeNotAllowed = "Organization.SelfRegistrationTypeNotAllowed";
        public const string NotPendingReview = "Organization.NotPendingReview";
        public const string SelfReviewNotAllowed = "Organization.SelfReviewNotAllowed";
        public const string LegalInfoLocked = "Organization.LegalInfoLocked";
        public const string ConcurrentModification = "Organization.ConcurrentModification";
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
        public const string OrganizationRequired = "Role.OrganizationRequired";
        public const string OrganizationRevokeNotSupported = "Role.OrganizationRevokeNotSupported";
    }

    public static class User
    {
        public const string EmailAlreadyExists = "User.EmailAlreadyExists";
        public const string NotFound = "User.NotFound";
        public const string CitizenIdAlreadyExists = "User.CitizenIdAlreadyExists";
        public const string Underage = "User.Underage";
        public const string LastOwnerOrganization = "User.LastOwnerOrganization";
    }

    public static class Profile
    {
        public const string NotCompleted = "Profile.NotCompleted";
        public const string AlreadyCompleted = "Profile.AlreadyCompleted";
        public const string EmptyPatch = "Profile.EmptyPatch";
        public const string InvalidFullName = "Profile.InvalidFullName";
    }

    public static class Media
    {
        public const string NotFound = "Media.NotFound";
        public const string NotReady = "Media.NotReady";
        public const string InvalidType = "Media.InvalidType";
        public const string Forbidden = "Media.Forbidden";
        public const string ServiceUnavailable = "Media.ServiceUnavailable";
        public const string ConflictingFields = "Media.ConflictingFields";
    }

    public static class Avatar
    {
        public const string ConflictingFields = "Avatar.ConflictingFields";
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
        public const string NotLinked = "ExternalLogin.NotLinked";
        public const string CannotUnlinkLastCredential = "ExternalLogin.CannotUnlinkLastCredential";
    }

    public static class Auth
    {
        public const string InvalidCredentials = "Auth.InvalidCredentials";
        public const string InactiveAccount = "Auth.InactiveAccount";
        public const string MissingCookie = "Auth.MissingCookie";
        public const string InvalidRefreshToken = "Auth.InvalidRefreshToken";
        public const string SessionNotFound = "Auth.SessionNotFound";
        public const string FirebaseNotConfigured = "Auth.FirebaseNotConfigured";
        public const string FirebaseTokenMissing = "Auth.FirebaseTokenMissing";
        public const string FirebaseTokenInvalid = "Auth.FirebaseTokenInvalid";
        public const string FirebaseEmailMissing = "Auth.FirebaseEmailMissing";
        public const string FirebaseEmailNotVerified = "Auth.FirebaseEmailNotVerified";
        public const string FirebaseProviderUnsupported = "Auth.FirebaseProviderUnsupported";
        public const string Uncategorized = "Auth.Uncategorized";
    }

    public static class Paging
    {
        public const string InvalidPage = "Paging.InvalidPage";
        public const string InvalidPageSize = "Paging.InvalidPageSize";
        public const string InvalidSearch = "Paging.InvalidSearch";
    }

    public static class AdminBootstrap
    {
        public const string InvalidKey = "AdminBootstrap.InvalidKey";
    }
}
