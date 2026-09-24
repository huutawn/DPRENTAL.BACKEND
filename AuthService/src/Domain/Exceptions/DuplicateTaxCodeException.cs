namespace Dprental.AuthService.Domain.Exceptions;

/// <summary>
/// Database từ chối tạo organization vì mã số thuế đã tồn tại (unique index).
/// </summary>
public sealed class DuplicateTaxCodeException : Exception
{
    public DuplicateTaxCodeException()
        : base("Mã số thuế đã được đăng ký.")
    {
    }

    public DuplicateTaxCodeException(string message)
        : base(message)
    {
    }

    public DuplicateTaxCodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
