using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.Validation;

/// <summary>
/// Thực thi cùng quy tắc validation DTO trong Service để lời gọi trực tiếp được kiểm tra như lời gọi HTTP.
/// Bổ sung validation cho token, mật khẩu, service credential, ID và trạng thái.
/// Hỗ trợ cả property DataAnnotations lẫn positional record constructor parameter attributes.
/// </summary>
public static class DtoValidator
{
    public static bool TryValidate(object? dto, [NotNullWhen(false)] out Error? error)
    {
        if (dto is null)
        {
            error = Error.Validation("Validation.Required", "Dữ liệu yêu cầu không được để trống.");
            return false;
        }

        List<ValidationResult> results = [];
        ValidationContext context = new(dto);
        Validator.TryValidateObject(dto, context, results, validateAllProperties: true);

        // Hỗ trợ positional record parameters (nơi C# mặc định gắn attribute vào parameter thay vì property)
        ConstructorInfo? ctor = dto.GetType().GetConstructors().FirstOrDefault();
        if (ctor is not null)
        {
            foreach (ParameterInfo param in ctor.GetParameters())
            {
                PropertyInfo? prop = dto.GetType().GetProperty(param.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                object? value = prop?.GetValue(dto);

                foreach (ValidationAttribute attr in param.GetCustomAttributes<ValidationAttribute>(true))
                {
                    // Tránh duplicate lỗi nếu property đã được Validator.TryValidateObject ghi nhận
                    if (results.Any(r => r.MemberNames.Contains(param.Name!, StringComparer.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (attr is RequiredAttribute && value is string s && string.IsNullOrWhiteSpace(s))
                    {
                        results.Add(new ValidationResult(attr.FormatErrorMessage(param.Name!), [param.Name!]));
                        continue;
                    }

                    if (!attr.IsValid(value))
                    {
                        results.Add(new ValidationResult(attr.FormatErrorMessage(param.Name!), [param.Name!]));
                    }
                }
            }
        }

        if (results.Count > 0)
        {
            ValidationResult first = results[0];
            string member = first.MemberNames.FirstOrDefault() ?? "Input";
            string message = first.ErrorMessage ?? "Dữ liệu không hợp lệ.";

            error = member switch
            {
                "FullName" when message.Contains("khoảng trắng", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(message) =>
                    AuthErrors.Profile.InvalidFullName,
                "Slug" => AuthErrors.Organization.InvalidSlugFormat,
                "Name" => AuthErrors.Organization.InvalidName,
                _ => Error.Validation($"Validation.{member}", message)
            };
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateId(Guid id, string paramName, [NotNullWhen(false)] out Error? error)
    {
        if (id == Guid.Empty)
        {
            error = Error.Validation($"Validation.{paramName}", $"{paramName} không hợp lệ (không được là Guid rỗng).");
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateEnum<TEnum>(TEnum value, string paramName, [NotNullWhen(false)] out Error? error)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            error = Error.Validation($"Validation.{paramName}", $"Giá trị trạng thái {paramName} không hợp lệ.");
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateRequiredString(string? value, string paramName, [NotNullWhen(false)] out Error? error, int minLength = 1, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = Error.Validation($"Validation.{paramName}", $"{paramName} không được để trống.");
            return false;
        }

        if (value.Length < minLength || value.Length > maxLength)
        {
            error = Error.Validation($"Validation.{paramName}", $"{paramName} phải có độ dài từ {minLength} đến {maxLength} ký tự.");
            return false;
        }

        error = null;
        return true;
    }
}
