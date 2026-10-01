namespace Dprental.CatalogService.Application.Options;

/// <summary>Cấu hình JWT xác thực token dựa trên key ring public key.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Auth:Jwt";

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public List<JwtKeyOptions> Keys { get; set; } = [];
}

/// <summary>Một public RSA key trong key ring.</summary>
public sealed class JwtKeyOptions
{
    public string KeyId { get; set; } = string.Empty;

    public string PublicKeyPem { get; set; } = string.Empty;
}
