using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Api.Clients;

/// <summary>Xác thực cấu hình Media Service ngay khi ứng dụng khởi động.</summary>
internal sealed class MediaServiceOptionsValidator : IValidateOptions<MediaServiceOptions>
{
    internal const int MaxTimeoutSeconds = 60;

    /// <summary>
    /// Chặn cấu hình thiếu ngay lúc khởi động để lỗi triển khai không biến thành 503 cho mọi thao tác gắn media.
    /// </summary>
    public ValidateOptionsResult Validate(string? name, MediaServiceOptions options)
    {
        List<string> failures = [];
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("MediaService:BaseUrl phải là URL HTTP hoặc HTTPS tuyệt đối.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add("Thiếu cấu hình bắt buộc: MediaService:ApiKey.");
        }

        if (options.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            failures.Add($"MediaService:TimeoutSeconds phải nằm trong khoảng từ 1 đến {MaxTimeoutSeconds}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
