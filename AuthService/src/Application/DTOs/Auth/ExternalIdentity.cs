namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>Danh tính do nhà cung cấp bên ngoài (Google) xác nhận sau khi user đăng nhập ở đó.</summary>
/// <param name="Provider">Tên nhà cung cấp, ví dụ <c>Google</c>.</param>
/// <param name="Subject">Id ổn định và duy nhất của tài khoản ở nhà cung cấp (claim <c>sub</c>).</param>
/// <param name="Email">Email nhà cung cấp trả về; có thể null.</param>
/// <param name="EmailVerified">Nhà cung cấp đã xác minh user sở hữu email này hay chưa.</param>
/// <param name="FullName">Họ tên hiển thị; có thể null.</param>
public sealed record ExternalIdentity(
    string Provider,
    string Subject,
    string? Email,
    bool EmailVerified,
    string? FullName);
