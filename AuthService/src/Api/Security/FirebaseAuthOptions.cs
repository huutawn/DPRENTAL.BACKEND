namespace Dprental.AuthService.Api.Security;

/// <summary>Firebase Authentication configuration used to verify Firebase ID tokens.</summary>
internal sealed class FirebaseAuthOptions
{
    public const string SectionName = "Auth:Firebase";

    public bool Enabled { get; init; }

    public string? ProjectId { get; init; }

    /// <summary>Raw service account JSON supplied from a secret store.</summary>
    public string? CredentialsJson { get; init; }

    /// <summary>Absolute path to a service account JSON file, useful for local development.</summary>
    public string? CredentialsPath { get; init; }

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(ProjectId) &&
        (!string.IsNullOrWhiteSpace(CredentialsJson) || !string.IsNullOrWhiteSpace(CredentialsPath));
}
