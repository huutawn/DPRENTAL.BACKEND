namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Specification metadata and wire constants for Rental–Media Contract v1.
/// NOTE: Uses X-Api-Key internal header reflecting active gateway &amp; media service deployment.
/// </summary>
public static class MediaContractSpec
{
    public const string Version = "v1";
    public const string SourceSystem = "rental";
    public const string AuthHeaderName = "X-Api-Key";
    public const int MaxEvidenceMediaCount = 10;

    public static class Routes
    {
        public static string VerifyMedia(Guid mediaId) => $"/api/v1/media/{mediaId}/verify";
        public static string GetMetadata(Guid mediaId) => $"/api/v1/media/{mediaId}";
        public static string CreatePrivateAccess(Guid mediaId) => $"/api/v1/media/{mediaId}/access";
    }

    public static class Statuses
    {
        public const string PendingUpload = "PendingUpload";
        public const string Ready = "Ready";
        public const string Rejected = "Rejected";
        public const string Deleted = "Deleted";
    }

    public static class Visibilities
    {
        public const string Private = "Private";
        public const string Public = "Public";
    }

    public static class ErrorCodes
    {
        public const string MediaNotFound = "MEDIA_NOT_FOUND";
        public const string MediaNotReady = "MEDIA_NOT_READY";
        public const string MediaMustBePrivate = "MEDIA_MUST_BE_PRIVATE";
        public const string MediaUploaderMismatch = "MEDIA_UPLOADER_MISMATCH";
        public const string MediaLimitExceeded = "MEDIA_LIMIT_EXCEEDED";
        public const string DuplicateMediaId = "DUPLICATE_MEDIA_ID";
        public const string InvalidMediaId = "INVALID_MEDIA_ID";
        public const string MediaIdMismatch = "MEDIA_ID_MISMATCH";
        public const string UnauthorizedMediaAccess = "UNAUTHORIZED_MEDIA_ACCESS";
    }
}
