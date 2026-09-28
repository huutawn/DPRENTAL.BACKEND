using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Application.Traffic;

/// <summary>
/// Thẩm định tính hợp lệ của toàn bộ cấu hình điều phối lưu lượng (GatewayTrafficOptions)
/// khi ứng dụng khởi động.
/// </summary>
public sealed class GatewayTrafficOptionsValidator : IValidateOptions<GatewayTrafficOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GatewayTrafficOptions? options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Cấu hình Gateway:Traffic không được để trống.");
        }

        List<string> failures = new();

        // Kiểm tra cấu hình Rate Limiting
        ValidateTier("Anonymous", options.RateLimiting.Anonymous, failures);
        ValidateTier("Authenticated", options.RateLimiting.Authenticated, failures);
        ValidateTier("Sensitive", options.RateLimiting.Sensitive, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateTier(string tierName, RateLimitTierOptions tier, List<string> failures)
    {
        if (tier.PermitLimit <= 0)
        {
            failures.Add($"Gateway:Traffic:RateLimiting:{tierName}:PermitLimit phải lớn hơn 0.");
        }

        if (tier.WindowSeconds <= 0)
        {
            failures.Add($"Gateway:Traffic:RateLimiting:{tierName}:WindowSeconds phải lớn hơn 0.");
        }

        if (tier.QueueLimit < 0)
        {
            failures.Add($"Gateway:Traffic:RateLimiting:{tierName}:QueueLimit không được là số âm.");
        }
    }
}
