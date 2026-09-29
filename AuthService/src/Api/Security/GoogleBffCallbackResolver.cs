using Dprental.AuthService.Application.Options;

namespace Dprental.AuthService.Api.Security;

/// <summary>Chọn callback Next BFF đã cấu hình theo origin của returnUrl, không tin callback URL từ request.</summary>
internal static class GoogleBffCallbackResolver
{
    internal static bool TryResolve(string returnUrl, GoogleAuthOptions options, out string callbackUrl)
    {
        callbackUrl = string.Empty;
        if (!ReturnUrlValidator.TryGetOrigin(returnUrl, out string? returnOrigin))
        {
            return false;
        }

        foreach (string candidate in options.BffCallbackUrls)
        {
            if (ReturnUrlValidator.TryGetOrigin(candidate, out string? callbackOrigin)
                && string.Equals(returnOrigin, callbackOrigin, StringComparison.OrdinalIgnoreCase))
            {
                callbackUrl = candidate;
                return true;
            }
        }

        return false;
    }

    internal static bool IsValidConfiguration(GoogleAuthOptions options)
    {
        if (!options.IsConfigured)
        {
            return true;
        }

        HashSet<string> allowedOrigins = new(StringComparer.OrdinalIgnoreCase);
        foreach (string allowed in options.AllowedReturnUrls)
        {
            if (!ReturnUrlValidator.TryGetOrigin(allowed, out string? origin))
            {
                return false;
            }

            allowedOrigins.Add(origin!);
        }

        HashSet<string> callbackOrigins = new(StringComparer.OrdinalIgnoreCase);
        foreach (string callback in options.BffCallbackUrls)
        {
            if (!ReturnUrlValidator.TryGetOrigin(callback, out string? origin)
                || !allowedOrigins.Contains(origin!)
                || !callbackOrigins.Add(origin!))
            {
                return false;
            }
        }

        return callbackOrigins.SetEquals(allowedOrigins);
    }
}
