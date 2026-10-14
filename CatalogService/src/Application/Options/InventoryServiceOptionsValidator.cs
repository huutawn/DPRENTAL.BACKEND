using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Application.Options;

/// <summary>
/// Xác thực cấu hình InventoryService khi khởi động ứng dụng (startup validation).
/// </summary>
public sealed class InventoryServiceOptionsValidator : IValidateOptions<InventoryServiceOptions>
{
    public ValidateOptionsResult Validate(string? name, InventoryServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add("InventoryService:BaseUrl không được để trống.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("InventoryService:BaseUrl phải là URL HTTP hoặc HTTPS tuyệt đối hợp lệ.");
        }

        if (options.TimeoutSeconds <= 0)
        {
            failures.Add("InventoryService:TimeoutSeconds phải lớn hơn 0.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
