using System.Text.RegularExpressions;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Validation;

/// <summary>
/// Chính sách hợp nhất về điều kiện hợp lệ và các trường còn thiếu của hồ sơ tổ chức.
/// Áp dụng thống nhất cho nộp nháp (SubmitDraft), đăng ký trực tiếp (Register), nộp lại (Resubmit/UpdateProfile)
/// và hiển thị MissingRequirements (ToResponse).
/// </summary>
public static class OrganizationProfilePolicy
{
    private static readonly Regex TaxCodeRegex = new(RegistrationPatterns.TaxCode, RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(RegistrationPatterns.PhoneNumber, RegexOptions.Compiled);
    private static readonly Regex CitizenIdRegex = new(RegistrationPatterns.CitizenId, RegexOptions.Compiled);

    public static IReadOnlyList<string> GetMissingRequirements(Organization org, string? fallbackCitizenId = null)
    {
        ArgumentNullException.ThrowIfNull(org);
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(org.TaxCode))
        {
            missing.Add("Mã số thuế");
        }
        if (string.IsNullOrWhiteSpace(org.LegalRepresentativeName))
        {
            missing.Add("Tên người đại diện pháp luật");
        }
        string? citizenId = StringUtility.NullIfBlank(org.LegalRepresentativeCitizenId) ?? fallbackCitizenId;
        if (string.IsNullOrWhiteSpace(citizenId))
        {
            missing.Add("Số CCCD người đại diện pháp luật");
        }
        if (string.IsNullOrWhiteSpace(org.ContactEmail))
        {
            missing.Add("Email liên hệ");
        }
        if (string.IsNullOrWhiteSpace(org.ContactPhone))
        {
            missing.Add("Số điện thoại liên hệ");
        }
        if (string.IsNullOrWhiteSpace(org.Address))
        {
            missing.Add("Địa chỉ trụ sở");
        }
        if (string.IsNullOrWhiteSpace(org.Province))
        {
            missing.Add("Tỉnh / Thành phố");
        }
        if (string.IsNullOrWhiteSpace(org.Ward))
        {
            missing.Add("Phường / Xã");
        }
        if (!org.BusinessLicenseDocumentMediaId.HasValue || org.BusinessLicenseDocumentMediaId == Guid.Empty)
        {
            missing.Add("Giấy phép kinh doanh");
        }

        return missing;
    }

    public static Result ValidateForSubmission(Organization org, string? fallbackCitizenId = null)
    {
        ArgumentNullException.ThrowIfNull(org);

        var missing = GetMissingRequirements(org, fallbackCitizenId);
        if (missing.Count > 0)
        {
            return Result.Failure(Error.Validation("MISSING_REQUIREMENTS", $"Hồ sơ chưa đủ điều kiện nộp duyệt. Còn thiếu: {string.Join(", ", missing)}."));
        }

        if (!TaxCodeRegex.IsMatch(org.TaxCode!))
        {
            return Result.Failure(Error.Validation("INVALID_TAX_CODE", "Mã số thuế phải gồm 10 chữ số hoặc dạng 10-3 chữ số."));
        }

        if (!PhoneRegex.IsMatch(org.ContactPhone!))
        {
            return Result.Failure(Error.Validation("INVALID_PHONE", "Số điện thoại không hợp lệ."));
        }

        string citizenId = (StringUtility.NullIfBlank(org.LegalRepresentativeCitizenId) ?? fallbackCitizenId)!;
        if (!CitizenIdRegex.IsMatch(citizenId))
        {
            return Result.Failure(Error.Validation("INVALID_CITIZEN_ID", "Số CCCD phải gồm đúng 12 chữ số."));
        }

        return Result.Success();
    }
}
