using Dprental.AuthService.Domain.Models;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Domain.Validators;

/// <summary>Kiểm tra bắt buộc cấu hình Auth:AdminBootstrap:CreationKey khi ứng dụng khởi động.</summary>
public sealed class AdminBootstrapOptionsValidator : IValidateOptions<AdminBootstrapOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.CreationKey))
        {
            return ValidateOptionsResult.Fail("Auth:AdminBootstrap:CreationKey must be configured.");
        }

        return ValidateOptionsResult.Success;
    }
}
