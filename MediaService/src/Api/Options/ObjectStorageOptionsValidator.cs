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

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
