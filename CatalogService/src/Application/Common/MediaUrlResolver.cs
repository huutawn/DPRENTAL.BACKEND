namespace Dprental.CatalogService.Application.Common;

/// <summary>
/// Trình phân giải URL public cho các media assets của hệ thống.
/// URL public ổn định duy nhất có định dạng: {PublicGatewayUrl}/api/v1/media/{mediaId:D}/content.
/// ID null trả về URL null.
/// </summary>
public static class MediaUrlResolver
{
    private static volatile string _publicGatewayUrl = string.Empty;

    public static string PublicGatewayUrl => _publicGatewayUrl;

    public static void Configure(string? publicGatewayUrl)
    {
        _publicGatewayUrl = (publicGatewayUrl ?? string.Empty).TrimEnd('/');
    }

    public static string? ResolvePublicUrl(Guid? mediaId)
    {
        if (!mediaId.HasValue)
        {
            return null;
        }

        return $"{_publicGatewayUrl}/api/v1/media/{mediaId.Value:D}/content";
    }
}
