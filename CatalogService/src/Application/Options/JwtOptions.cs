namespace Dprental.CatalogService.Application.Options;

/// <summary>Cấu hình JWT xác thực access token bằng public key tải từ JWKS của AuthService.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Auth:Jwt";

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>Endpoint JWKS của AuthService, ví dụ <c>http://auth-service:8080/.well-known/jwks.json</c>.</summary>
    public string JwksUri { get; set; } = string.Empty;

    /// <summary>Thời gian chờ tối đa cho một lần tải JWKS.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Thời gian dùng cache JWKS trước khi tải lại.</summary>
    public int CacheDurationMinutes { get; set; } = 5;

    /// <summary>Thời gian tối đa còn tin cache cũ khi AuthService tạm thời không truy cập được.</summary>
    public int MaxStaleMinutes { get; set; } = 10;
}
