namespace Dprental.AuthService.Common.Utils;

/// <summary>Xử lý chuỗi thuần tuý, không phụ thuộc nghiệp vụ — dùng chung cho mọi tầng.</summary>
public static class StringUtility
{
    /// <summary>Chuỗi rỗng hoặc chỉ có khoảng trắng coi như không nhập (null); còn lại thì trim.</summary>
    public static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Che CCCD, chỉ để lộ 4 số cuối.</summary>
    public static string? MaskCitizenId(string? citizenId) =>
        citizenId switch
        {
            null or "" => null,
            { Length: <= 4 } => new string('*', citizenId.Length),
            _ => new string('*', citizenId.Length - 4) + citizenId[^4..]
        };
}
