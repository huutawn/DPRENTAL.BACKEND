using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.Validation;

/// <summary>
/// Chính sách mật khẩu theo NIST SP 800-63B / OWASP ASVS: đủ dài, không nằm trong danh sách mật khẩu phổ biến,
/// không chứa email hay tên của chính người dùng. Cố ý KHÔNG bắt buộc chữ hoa/số/ký tự đặc biệt — NIST khuyến
/// nghị không áp quy tắc thành phần vì người dùng sẽ đặt mật khẩu kiểu "Password1!" vừa dễ đoán vừa khó nhớ.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    /// <summary>Phần định danh ngắn hơn mức này (vd email "ab@x.com") quá dễ trùng ngẫu nhiên nên bỏ qua.</summary>
    private const int MinPersonalTokenLength = 4;

    public static bool IsCommon(string password) =>
        CommonPasswords.Contains(password.Trim());

    /// <summary>Mật khẩu chứa phần trước @ của email hoặc họ tên (bỏ khoảng trắng), không phân biệt hoa thường.</summary>
    public static Error? CheckPersonalInfo(string password, string? email, string? fullName)
    {
        ArgumentNullException.ThrowIfNull(password);

        string? localPart = email?.Split('@')[0];
        string? compactName = fullName is null ? null : string.Concat(fullName.Where(c => !char.IsWhiteSpace(c)));

        foreach (string? token in new[] { localPart, compactName })
        {
            if (token is { Length: >= MinPersonalTokenLength } &&
                password.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return AuthErrors.Auth.PasswordContainsPersonalInfo;
            }
        }

        return null;
    }
}

/// <summary>
/// Chặn mật khẩu nằm trong danh sách phổ biến/đã lộ. Độ dài vẫn kiểm tra bằng <see cref="StringLengthAttribute"/>;
/// việc so với email/tên người dùng cần ngữ cảnh nên nằm ở service (<see cref="PasswordPolicy.CheckPersonalInfo"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class NotCommonPasswordAttribute : ValidationAttribute
{
    public NotCommonPasswordAttribute() : base("Mật khẩu quá phổ biến, dễ bị đoán. Hãy chọn mật khẩu khác.") { }

    public override bool IsValid(object? value) => value is not string password || !PasswordPolicy.IsCommon(password);
}
