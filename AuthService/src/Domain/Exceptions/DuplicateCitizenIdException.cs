namespace Dprental.AuthService.Domain.Exceptions;

/// <summary>
/// Database từ chối tạo user vì số CCCD đã tồn tại (unique index).
/// </summary>
public sealed class DuplicateCitizenIdException : Exception
{
    public DuplicateCitizenIdException()
        : base("Số CCCD đã được sử dụng.")
    {
    }

    public DuplicateCitizenIdException(string message)
        : base(message)
    {
    }

    public DuplicateCitizenIdException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
