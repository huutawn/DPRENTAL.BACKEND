namespace Dprental.AuthService.Domain.Models;

/// <summary>Cấu hình đăng nhập Google (OAuth 2.0 / OpenID Connect) lấy từ section <c>Auth:Google</c>.</summary>
public sealed class GoogleAuthOptions
{
    /// <summary>Đường dẫn cấu hình gốc của thiết lập Google.</summary>
    public const string SectionName = "Auth:Google";

    /// <summary>Client ID của OAuth client tạo trên Google Cloud Console.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Client secret tương ứng; chỉ cấp qua user-secrets hoặc biến môi trường, không đặt trong appsettings.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>Các origin frontend được phép nhận redirect sau khi đăng nhập, ví dụ <c>http://localhost:3000</c>.</summary>
    public IReadOnlyList<string> AllowedReturnUrls { get; init; } = [];

    /// <summary>
    /// Các endpoint callback của Next BFF. Mỗi origin frontend được phép phải có đúng một callback
    /// để backend chọn theo origin của <c>returnUrl</c>, không nhận callback URL do client chỉ định.
    /// </summary>
    public IReadOnlyList<string> BffCallbackUrls { get; init; } = [];

    /// <summary>Chỉ bật đăng nhập Google khi cả ClientId và ClientSecret đều có giá trị.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
