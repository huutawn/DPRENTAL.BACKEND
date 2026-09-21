using Dprental.AuthService.Common;

namespace Dprental.AuthService.Domain.Errors;

public static class UserErrors
{
    public static readonly Error EmailAlreadyExists =
        Error.Conflict("User.EmailAlreadyExists", "Email đã được sử dụng.");

    public static readonly Error UserNotFound =
        Error.NotFound("User.NotFound", "Người dùng không tồn tại hoặc tài khoản đã bị khóa.");
}
