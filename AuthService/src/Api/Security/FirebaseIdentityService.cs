using System.Globalization;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Api.Security;

internal sealed partial class FirebaseIdentityService : IFirebaseIdentityService
{
    private readonly FirebaseAuthOptions _options;
    private readonly ILogger<FirebaseIdentityService> _logger;
    private readonly Lazy<FirebaseAuth> _firebaseAuth;

    public FirebaseIdentityService(
        IOptions<FirebaseAuthOptions> options,
        ILogger<FirebaseIdentityService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _firebaseAuth = new Lazy<FirebaseAuth>(CreateFirebaseAuth, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<Result<FirebaseIdentity>> VerifyIdTokenAsync(
        string? idToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return Result.Failure<FirebaseIdentity>(AuthErrors.Auth.FirebaseTokenMissing);
        }

        if (!_options.IsConfigured)
        {
            return Result.Failure<FirebaseIdentity>(AuthErrors.Auth.FirebaseNotConfigured);
        }

        FirebaseToken token;
        try
        {
            token = await _firebaseAuth.Value.VerifyIdTokenAsync(idToken.Trim(), cancellationToken);
        }
        catch (FirebaseAuthException exception)
        {
            LogTokenRejected(_logger, exception.AuthErrorCode?.ToString() ?? "Unknown", null);
            return Result.Failure<FirebaseIdentity>(AuthErrors.Auth.FirebaseTokenInvalid);
        }
        catch (InvalidOperationException exception)
        {
            LogConfigurationFailure(_logger, exception.GetType().Name, null);
            return Result.Failure<FirebaseIdentity>(AuthErrors.Auth.FirebaseNotConfigured);
        }
        catch (ArgumentException exception)
        {
            LogConfigurationFailure(_logger, exception.GetType().Name, null);
            return Result.Failure<FirebaseIdentity>(AuthErrors.Auth.FirebaseNotConfigured);
        }

        string? email = ClaimAsString(token, "email");
        if (string.IsNullOrWhiteSpace(email))
        {
            return Result.Failure<FirebaseIdentity>(AuthErrors.Auth.FirebaseEmailMissing);
        }

        if (!ClaimAsBoolean(token, "email_verified"))
        {
            return Result.Failure<FirebaseIdentity>(AuthErrors.Auth.FirebaseEmailNotVerified);
        }

        return Result.Success(new FirebaseIdentity(
            token.Uid,
            ResolveProviderSubject(token),
            ResolveSignInProvider(token),
            email.Trim(),
            true,
            ClaimAsString(token, "name"),
            ClaimAsString(token, "picture")));
    }

    private FirebaseAuth CreateFirebaseAuth()
    {
        GoogleCredential credential = !string.IsNullOrWhiteSpace(_options.CredentialsJson)
            ? CredentialFactory.FromJson(
                _options.CredentialsJson,
                JsonCredentialParameters.ServiceAccountCredentialType)
            : CredentialFactory.FromFile(
                _options.CredentialsPath!,
                JsonCredentialParameters.ServiceAccountCredentialType);

        FirebaseApp app = FirebaseApp.Create(
            new AppOptions
            {
                Credential = credential,
                ProjectId = _options.ProjectId
            },
            $"auth-firebase-{Guid.NewGuid():N}");

        return FirebaseAuth.GetAuth(app);
    }

    private static string? ClaimAsString(FirebaseToken token, string key)
    {
        return token.Claims.TryGetValue(key, out object? value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;
    }

    private static bool ClaimAsBoolean(FirebaseToken token, string key)
    {
        if (!token.Claims.TryGetValue(key, out object? value))
        {
            return false;
        }

        return value switch
        {
            bool boolean => boolean,
            string text => bool.TryParse(text, out bool parsed) && parsed,
            _ => bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool parsed) && parsed
        };
    }

    private static string ResolveProviderSubject(FirebaseToken token) =>
        TryGetFirebaseIdentity(token, "google.com") ?? token.Uid;

    private static string? ResolveSignInProvider(FirebaseToken token)
    {
        if (!TryGetFirebaseClaim(token, "sign_in_provider", out object? provider))
        {
            return null;
        }

        return Convert.ToString(provider, CultureInfo.InvariantCulture);
    }

    private static string? TryGetFirebaseIdentity(FirebaseToken token, string provider)
    {
        if (!TryGetFirebaseClaim(token, "identities", out object? identities))
        {
            return null;
        }

        if (!TryGetObjectValue(identities, provider, out object? providerValue))
        {
            return null;
        }

        return FirstString(providerValue);
    }

    private static bool TryGetFirebaseClaim(FirebaseToken token, string key, out object? value)
    {
        value = null;
        if (!token.Claims.TryGetValue("firebase", out object? firebase))
        {
            return false;
        }

        return TryGetObjectValue(firebase, key, out value);
    }

    private static bool TryGetObjectValue(object? source, string key, out object? value)
    {
        value = null;
        switch (source)
        {
            case IReadOnlyDictionary<string, object> readOnlyDictionary:
                return readOnlyDictionary.TryGetValue(key, out value);
            case IDictionary<string, object> dictionary:
                return dictionary.TryGetValue(key, out value);
            case JsonElement { ValueKind: JsonValueKind.Object } json:
                if (json.TryGetProperty(key, out JsonElement property))
                {
                    value = property.Clone();
                    return true;
                }

                return false;
            default:
                return TryGetJsonObjectValue(source, key, out value);
        }
    }

    private static string? FirstString(object? value) =>
        value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement { ValueKind: JsonValueKind.Array } json => json.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            IEnumerable<object> values => values
                .Select(item => Convert.ToString(item, CultureInfo.InvariantCulture))
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            _ => FirstStringFromJson(value)
        };

    private static bool TryGetJsonObjectValue(object? source, string key, out object? value)
    {
        value = null;
        string? json = Convert.ToString(source, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(json) || json.TrimStart()[0] != '{')
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(key, out JsonElement property))
            {
                return false;
            }

            value = property.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FirstStringFromJson(object? value)
    {
        string? json = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        char first = json.TrimStart()[0];
        if (first is not ('[' or '"' or '{'))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => document.RootElement.GetString(),
                JsonValueKind.Array => document.RootElement.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                    .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Firebase ID token rejected: {FirebaseErrorCode}")]
    private static partial void LogTokenRejected(
        ILogger logger,
        string firebaseErrorCode,
        Exception? exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "Firebase Authentication configuration failure: {ErrorType}")]
    private static partial void LogConfigurationFailure(
        ILogger logger,
        string errorType,
        Exception? exception);
}
