using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.User;

namespace Dprental.AuthService.Application.IServices;

/// <summary>Dịch vụ tự quản lý tài khoản người dùng: hồ sơ cá nhân, bảo mật và liên kết.</summary>
public interface IUserAccountService
{
    /// <summary>Hoàn thiện hồ sơ lần đầu với CCCD, số điện thoại và ngày sinh.</summary>
    Task<Result<CurrentUserResponse>> CompleteProfileAsync(Guid userId, CompleteProfileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cập nhật thông tin hồ sơ cá nhân sau khi đã hoàn thiện.</summary>
    Task<Result<CurrentUserResponse>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cập nhật hồ sơ người dùng hiện tại qua endpoint PUT /me.</summary>
    Task<Result<CurrentUserResponse>> UpdateUserProfileAsync(Guid userId, Dprental.AuthService.Domain.DTOs.User.UpdateUserProfileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lấy CCCD đầy đủ của chính người dùng; mỗi lần xem được ghi audit log.</summary>
    Task<Result<CitizenIdResponse>> GetCitizenIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Lấy danh sách các liên kết đăng nhập bên ngoài của người dùng.</summary>
    Task<Result<IReadOnlyList<ExternalLoginResponse>>> GetExternalLoginsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Liên kết đăng nhập bên ngoài (Google,...) vào tài khoản hiện tại.</summary>
    Task<Result> LinkExternalLoginAsync(Guid userId, string provider, string? idToken, CancellationToken cancellationToken = default);

    /// <summary>Huỷ liên kết đăng nhập bên ngoài (Google,...) nếu tài khoản còn phương thức đăng nhập khác.</summary>
    Task<Result> UnlinkExternalLoginAsync(Guid userId, string provider, CancellationToken cancellationToken = default);

    /// <summary>Tự vô hiệu hoá tài khoản (xoá mềm), chặn nếu là Owner duy nhất của một tổ chức.</summary>
    Task<Result> DeactivateAsync(Guid userId, CancellationToken cancellationToken = default);
}
