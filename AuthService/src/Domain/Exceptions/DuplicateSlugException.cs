namespace Dprental.AuthService.Domain.Exceptions;

/// <summary>
/// Database từ chối tạo organization vì slug đã tồn tại (unique index).
/// </summary>
public sealed class DuplicateSlugException : Exception
{
    public DuplicateSlugException()
        : base("Slug của organization đã tồn tại.")
    {
    }

    public DuplicateSlugException(string message)
        : base(message)
    {
    }

    public DuplicateSlugException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
