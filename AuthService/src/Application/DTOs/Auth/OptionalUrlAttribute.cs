using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>
/// Giống <see cref="UrlAttribute"/> nhưng coi chuỗi rỗng/khoảng trắng là hợp lệ (bỏ qua), thay vì báo lỗi.
/// <see cref="UrlAttribute"/> gốc chỉ bỏ qua giá trị <c>null</c>, không bỏ qua <c>""</c> — gây khó chịu cho field
/// URL không bắt buộc (client gửi "" thay vì omit field vẫn phải qua được). Cùng quy ước với
/// <see cref="RegularExpressionAttribute"/>, vốn coi cả null lẫn rỗng là hợp lệ.
/// </summary>
public sealed class OptionalUrlAttribute : ValidationAttribute
{
    public OptionalUrlAttribute()
        : base("The {0} field is not a valid fully-qualified http, https, or ftp URL.")
    {
    }

    public override bool IsValid(object? value)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp);
    }
}
