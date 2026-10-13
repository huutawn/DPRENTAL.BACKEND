using Dprental.InventoryService.Application.DTOs.Media;

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

    /// <summary>
    /// Xây dựng danh sách tham chiếu media công khai giữ nguyên thứ tự từ danh sách mediaIds.
    /// </summary>
    public static IReadOnlyList<MediaReferenceResponse> BuildList(string? gatewayBaseUrl, IReadOnlyList<Guid>? mediaIds)
    {
        if (mediaIds is null || mediaIds.Count == 0)
        {
            return Array.Empty<MediaReferenceResponse>();
        }

        var list = new List<MediaReferenceResponse>(mediaIds.Count);
        foreach (var mediaId in mediaIds)
        {
            if (mediaId == Guid.Empty)
            {
                continue;
            }

            var url = Build(gatewayBaseUrl, mediaId) ?? $"/api/v1/media/{mediaId:D}/content";
            list.Add(new MediaReferenceResponse(mediaId, url));
        }

        return list;
    }
}
