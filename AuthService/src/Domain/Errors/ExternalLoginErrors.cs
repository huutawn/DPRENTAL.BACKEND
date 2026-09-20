using Dprental.AuthService.Common;

namespace Dprental.AuthService.Domain.Errors;

public static class ExternalLoginErrors
{
    public static readonly Error MissingEmail =
        Error.Validation("ExternalLogin.MissingEmail", "Tài khoản bên ngoài không cung cấp email.");

    public static readonly Error EmailNotVerified =
        Error.Forbidden("ExternalLogin.EmailNotVerified", "Email của tài khoản bên ngoài chưa được xác minh.");

    public static readonly Error AccountDisabled =
        Error.Forbidden("ExternalLogin.AccountDisabled", "Tài khoản không tồn tại hoặc đã bị khóa.");

    public static readonly Error AlreadyLinked =
        Error.Conflict("ExternalLogin.AlreadyLinked", "Tài khoản này đã liên kết với một tài khoản bên ngoài khác.");
}
