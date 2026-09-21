namespace Dprental.AuthService.Domain.Exceptions;

/// <summary>
/// Database từ chối tạo membership vì user đã là thành viên của organization (unique index).
/// </summary>
public sealed class DuplicateMembershipException : Exception
{
    public DuplicateMembershipException()
        : base("User đã là thành viên của organization này.")
    {
    }

    public DuplicateMembershipException(string message)
        : base(message)
    {
    }

    public DuplicateMembershipException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
