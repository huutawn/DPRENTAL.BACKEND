namespace Dprental.AuthService.Application.Options;

/// <summary>Tập hợp cấu hình cần thiết để phát hành và xác thực JWT RS256.</summary>
public sealed class JwtOptions
{
    /// <summary>Đường dẫn cấu hình gốc của toàn bộ thiết lập JWT.</summary>
    public const string SectionName = "Auth:Jwt";

    /// <summary>Giá trị claim <c>iss</c> mà token phát hành và token nhận vào phải khớp.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Giá trị claim <c>aud</c> biểu thị nhóm client được phép dùng access token.</summary>
    public string Audience { get; init; } = string.Empty;

    /// <summary>Thời hạn của access token tính bằng phút.</summary>
    public int AccessTokenLifetimeMinutes { get; init; }

    /// <summary><c>kid</c> của key pair hiện được dùng để ký token mới.</summary>
    public string ActiveKeyId { get; init; } = string.Empty;

    /// <summary>Toàn bộ key ring, gồm key active và public key của các token cũ còn hợp lệ.</summary>
    public IReadOnlyList<JwtKeyOptions> Keys { get; init; } = [];
}

/// <summary>Cấu hình một entry trong JWT key ring.</summary>
public sealed class JwtKeyOptions
{
    /// <summary>Key ID duy nhất, được phát trong JWT header và công bố qua JWKS.</summary>
    public string KeyId { get; init; } = string.Empty;

    /// <summary>Public RSA key ở định dạng PEM, dùng để xác thực token và tạo JWKS.</summary>
    public string PublicKeyPem { get; init; } = string.Empty;

    /// <summary>Private RSA key ở định dạng PEM; chỉ bắt buộc với entry có <c>KeyId</c> active.</summary>
    public string? PrivateKeyPem { get; init; }
}
