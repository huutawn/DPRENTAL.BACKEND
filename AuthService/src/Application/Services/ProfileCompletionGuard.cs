using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Services;

/// <summary>Kiểm tra điều kiện hoàn thiện hồ sơ định danh trước khi thực hiện các nghiệp vụ yêu cầu định danh.</summary>
internal static class ProfileCompletionGuard
{
    internal static Error? Check(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsProfileCompleted ? null : AuthErrors.Profile.NotCompleted;
    }
}
