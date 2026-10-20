namespace Dprental.Gateway.Security;

/// <summary>
/// Khóa định danh partition cho rate limiter của Gateway:
/// Loại danh tính ("user" hoặc "ip"), giá trị danh tính (user ID hoặc địa chỉ IP), và nhóm request ("read" hoặc "write").
/// Tuyệt đối không dùng cặp IP + user.
/// </summary>
public readonly record struct RateLimitPartitionKey(string IdentityType, string IdentityValue, string RequestGroup)
{
    public const string UserType = "user";
    public const string IpType = "ip";
    public const string ReadGroup = "read";
    public const string WriteGroup = "write";
    public const string UnknownIp = "unknown";
}
