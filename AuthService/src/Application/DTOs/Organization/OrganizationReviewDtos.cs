using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Application.DTOs.Auth;

namespace Dprental.AuthService.Application.DTOs.Organization;

/// <summary>Platform từ chối hồ sơ organization đang chờ duyệt; lý do được trả lại cho Owner để sửa và gửi lại.</summary>
public sealed record RejectOrganizationRequest(
    [Required, StringLength(500)] string Reason);

/// <summary>
/// Owner gửi lại toàn bộ hồ sơ organization (PUT). Không có Type và Slug vì hai trường này cố định sau khi đăng ký.
/// Khi organization đã Active, các trường pháp lý phải giữ nguyên — chỉ liên hệ và hiển thị được đổi.
/// </summary>
/// <param name="LegalRepresentativeCitizenId">Bỏ trống thì lấy CCCD trong hồ sơ của Owner, giống lúc đăng ký.</param>
public sealed record UpdateOrganizationProfileRequest(
    [Required, StringLength(150)] string Name,
    [Required, RegularExpression(RegistrationPatterns.TaxCode, ErrorMessage = "Mã số thuế phải gồm 10 chữ số hoặc dạng 10-3 chữ số.")] string TaxCode,
    [Required, StringLength(100)] string LegalRepresentativeName,
    [Required, EmailAddress, StringLength(254)] string ContactEmail,
    [Required, RegularExpression(RegistrationPatterns.PhoneNumber, ErrorMessage = "Số điện thoại không hợp lệ.")] string ContactPhone,
    [Required, StringLength(250)] string Address,
    [Required, StringLength(100)] string Province,
    [Required, StringLength(100)] string Ward,
    [Required, Url, StringLength(500)] string BusinessLicenseDocumentUrl,
    [StringLength(50)] string? BusinessLicenseNumber = null,
    DateOnly? BusinessLicenseIssuedDate = null,
    [StringLength(200)] string? BusinessLicenseIssuedPlace = null,
    DateOnly? EstablishedDate = null,
    [RegularExpression(RegistrationPatterns.CitizenId, ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số.")] string? LegalRepresentativeCitizenId = null,
    [OptionalUrl, StringLength(500)] string? Website = null,
    [OptionalUrl, StringLength(500)] string? LogoUrl = null,
    [StringLength(2000)] string? Description = null);
