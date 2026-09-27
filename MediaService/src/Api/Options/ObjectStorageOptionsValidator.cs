using Microsoft.Extensions.Options;

namespace Dprental.MediaService.Api.Options;

/// <summary>
/// Kiểm tra tính hợp lệ của cấu hình ObjectStorageOptions khi ứng dụng khởi động.
/// </summary>
public sealed class ObjectStorageOptionsValidator : IValidateOptions<ObjectStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, ObjectStorageOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            failures.Add("ObjectStorage:Endpoint không được để trống.");
        }
        else if (!IsStorageEndpoint(options.Endpoint))
        {
            failures.Add("ObjectStorage:Endpoint phải là địa chỉ HTTP(S) hợp lệ, không có path hoặc thông tin đăng nhập.");
        }

        if (string.IsNullOrWhiteSpace(options.Bucket))
        {
            failures.Add("ObjectStorage:Bucket không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey))
        {
            failures.Add("ObjectStorage:AccessKey không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(options.SecretKey))
        {
            failures.Add("ObjectStorage:SecretKey không được để trống.");
        }

        if (options.ReadUrlExpirySeconds is < 1 or > 900)
        {
            failures.Add("ObjectStorage:ReadUrlExpirySeconds phải nằm trong khoảng từ 1 đến 900 giây.");
        }

        if (!string.IsNullOrWhiteSpace(options.BrowserEndpoint) && !IsStorageEndpoint(options.BrowserEndpoint))
        {
            failures.Add("ObjectStorage:BrowserEndpoint phải là URL HTTP(S) hợp lệ, không có path hoặc thông tin đăng nhập.");
        }

        foreach (string origin in options.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.AbsolutePath != "/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                failures.Add($"ObjectStorage:AllowedOrigins chứa origin không hợp lệ: {origin}.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static bool IsStorageEndpoint(string value)
    {
        string candidate = value.Contains("://", StringComparison.Ordinal) ? value : $"http://{value}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.Host.Length > 0 && uri.AbsolutePath == "/" &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }
}
