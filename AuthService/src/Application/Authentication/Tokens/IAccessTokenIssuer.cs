namespace Dprental.AuthService.Application.Authentication.Tokens;

/// <summary>
/// Phát hành access token có thời hạn ngắn cho một chủ thể đã được xác thực.
/// Việc xác thực chủ thể thuộc trách nhiệm của use case gọi interface này.
/// </summary>
public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(AccessTokenRequest request);
}

/// <summary>
/// Dữ liệu claim tối thiểu để phát access token mà không phụ thuộc vào entity User.
/// </summary>
public sealed record AccessTokenRequest(
    /// <summary>Mã định danh duy nhất của chủ thể sẽ xuất hiện trong claim <c>sub</c>.</summary>
    Guid SubjectId,
    /// <summary>Các vai trò sẽ xuất hiện trong các claim <c>role</c>; có thể để trống.</summary>
    IReadOnlyCollection<string>? Roles = null);

/// <summary>
/// Access token đã ký và thời điểm hết hạn UTC để trả cho client sau khi xác thực.
/// </summary>
public sealed record IssuedAccessToken(
    /// <summary>Chuỗi JWT đã được ký.</summary>
    string Value,
    /// <summary>Thời điểm JWT hết hạn theo UTC.</summary>
    DateTimeOffset ExpiresAtUtc);
