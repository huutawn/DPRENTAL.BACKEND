namespace Dprental.InventoryService.Application.Mapping;

/// <summary>
/// Trình tạo URL nội dung media ổn định thông qua PublicGatewayUrl.
/// </summary>
public static class MediaContentUrlBuilder
{
    /// <summary>
    /// Xây dựng URL truy cập nội dung media công khai từ gatewayBaseUrl và mediaId.
    /// Nếu mediaId là null hoặc gatewayBaseUrl rỗng, trả về null.
    /// </summary>
    public static string? Build(string? gatewayBaseUrl, Guid? mediaId)
    {
        if (mediaId is null || mediaId.Value == Guid.Empty || string.IsNullOrWhiteSpace(gatewayBaseUrl))
        {
            return null;
        }

        var baseUrl = gatewayBaseUrl.TrimEnd('/');
        return $"{baseUrl}/api/v1/media/{mediaId.Value:D}/content";
    }
}
