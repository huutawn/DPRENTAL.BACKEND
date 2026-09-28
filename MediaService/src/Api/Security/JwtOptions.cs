namespace Dprental.MediaService.Api.Security;

/// <summary>
/// Cấu hình xác thực JWT cho MediaService.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string JwksUri { get; set; } = string.Empty;
}
