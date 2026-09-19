namespace Dprental.AuthService.Domain.Exceptions;

/// <summary>
/// Database từ chối tạo liên kết đăng nhập ngoài vì đã tồn tại (unique index).
/// Xảy ra khi hai request cùng liên kết một tài khoản, hoặc user đã liên kết một tài khoản khác của cùng nhà cung cấp.
/// </summary>
public sealed class DuplicateExternalLoginException : Exception
{
    public DuplicateExternalLoginException()
        : base("Liên kết đăng nhập bên ngoài đã tồn tại.")
    {
    }

    public DuplicateExternalLoginException(string message)
        : base(message)
    {
    }

    public DuplicateExternalLoginException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
