namespace Dprental.AuthService.Domain.Exceptions;

/// <summary>
/// Database từ chối tạo user vì email đã tồn tại (unique index).
/// Chỉ xảy ra khi hai request đăng ký cùng email chạy song song.
/// </summary>
public sealed class DuplicateEmailException : Exception
{
    public DuplicateEmailException()
        : base("Email đã được sử dụng.")
    {
    }

    public DuplicateEmailException(string message)
        : base(message)
    {
    }

    public DuplicateEmailException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
