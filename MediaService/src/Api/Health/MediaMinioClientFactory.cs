using Dprental.MediaService.Api.Options;
using Minio;

namespace Dprental.MediaService.Api.Health;

/// <summary>
/// Khởi tạo IMinioClient từ cấu hình ObjectStorageOptions.
/// </summary>
public static class MediaMinioClientFactory
{
    /// <summary>
    /// Tạo instance IMinioClient theo cấu hình lưu trữ đối tượng.
    /// </summary>
    /// <param name="options">Cấu hình lưu trữ đối tượng.</param>
    /// <returns>Đối tượng IMinioClient đã được khởi tạo.</returns>
    public static IMinioClient Create(ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rawEndpoint = options.Endpoint.Trim();
        var withScheme = rawEndpoint.Contains("://", StringComparison.Ordinal)
            ? rawEndpoint
            : $"http://{rawEndpoint}";

        var uri = new Uri(withScheme);
        var endpointAuthority = uri.Authority;
        var useSsl = options.UseSsl ?? uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        return new MinioClient()
            .WithEndpoint(endpointAuthority)
            .WithCredentials(options.AccessKey, options.SecretKey)
            .WithSSL(useSsl)
            .Build();
    }
}
