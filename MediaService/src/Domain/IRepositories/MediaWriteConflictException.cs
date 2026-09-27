namespace Dprental.MediaService.Domain.IRepositories;

/// <summary>Persistence conflict from a concurrent update or a unique constraint.</summary>
public sealed class MediaWriteConflictException(string message, Exception innerException)
    : Exception(message, innerException);
