namespace Dprental.CatalogService.Common.Security;

/// <summary>
/// Wire contract của access token do AuthService phát hành (khớp <c>JwtClaimCatalog</c> bên AuthService).
/// Catalog chỉ đọc đúng các tên rút gọn này, không tự đặt quy ước claim riêng.
/// </summary>
public static class CatalogClaims
{
    public static class Types
    {
        /// <summary>User ID (hoặc service principal ID với service token).</summary>
        public const string Subject = "sub";

        /// <summary>Rút gọn từ "role".</summary>
        public const string Role = "r";

        /// <summary>Rút gọn từ "perm".</summary>
        public const string Permission = "p";

        /// <summary>Rút gọn từ "org"; mỗi organization user thuộc về là một claim riêng.</summary>
        public const string Organization = "o";

        /// <summary>Rút gọn từ "token_use".</summary>
        public const string TokenUse = "u";

        public const string SessionId = "sid";
    }

    public static class TokenUses
    {
        public const string Service = "svc";
    }

    public static class Roles
    {
        public const string Admin = "adm";
        public const string AdminPlatform = "adp";
        public const string Owner = "own";
        public const string Member = "mem";
        public const string Customer = "cus";
    }
}
