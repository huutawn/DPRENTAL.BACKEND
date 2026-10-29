namespace Dprental.Gateway.Authentication;

/// <summary>Cấu hình nguồn tin cậy để Gateway xác thực JWT.</summary>
internal sealed class GatewayJwtOptions
{
    internal const string SectionName = "Gateway:Authentication:Jwt";

    /// <summary>Giá trị issuer bắt buộc của access token do AuthService phát hành.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Audience của token người dùng mà Gateway chấp nhận.</summary>
    public string Audience { get; init; } = string.Empty;

    /// <summary>Audience của token service dùng cho các route InventoryService.</summary>
    public string InventoryAudience { get; init; } = "inventory-api";

    /// <summary>Địa chỉ JWKS của AuthService, chỉ công bố khóa công khai để kiểm tra chữ ký.</summary>
    public string JwksUri { get; init; } = string.Empty;
}
