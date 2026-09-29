using System.Globalization;
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
