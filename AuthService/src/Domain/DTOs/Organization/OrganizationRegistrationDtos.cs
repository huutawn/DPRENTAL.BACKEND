using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.DTOs.Organization;

/// <summary>
/// Hồ sơ tổ chức tự đăng ký, gửi bởi user đang đăng nhập (trở thành Owner). Slug bỏ trống sẽ được sinh từ Name.
/// </summary>
/// <param name="LegalRepresentativeCitizenId">
/// Bỏ trống nếu người đại diện pháp luật chính là actor đăng ký — hệ thống tự lấy CCCD đã có trong hồ sơ actor.
/// Chỉ cần nhập khi người đại diện pháp luật là người khác.
/// </param>
public sealed record OrganizationProfileRequest(
    [Required, StringLength(150)] string Name,
    OrganizationType Type,
    [Required, RegularExpression(RegistrationPatterns.TaxCode, ErrorMessage = "Mã số thuế phải gồm 10 chữ số hoặc dạng 10-3 chữ số.")] string TaxCode,
    [Required, StringLength(100)] string LegalRepresentativeName,
    [Required, EmailAddress, StringLength(254)] string ContactEmail,
    [Required, RegularExpression(RegistrationPatterns.PhoneNumber, ErrorMessage = "Số điện thoại không hợp lệ.")] string ContactPhone,
    [Required, StringLength(250)] string Address,
    [Required, StringLength(100)] string Province,
    [Required, StringLength(100)] string Ward,
    [Required, Url, StringLength(500)] string BusinessLicenseDocumentUrl,
    [StringLength(100)] string? Slug = null,
    [StringLength(50)] string? BusinessLicenseNumber = null,
    DateOnly? BusinessLicenseIssuedDate = null,
    [StringLength(200)] string? BusinessLicenseIssuedPlace = null,
    DateOnly? EstablishedDate = null,
    [RegularExpression(RegistrationPatterns.CitizenId, ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số.")] string? LegalRepresentativeCitizenId = null,
    [OptionalUrl, StringLength(500)] string? Website = null,
    [OptionalUrl, StringLength(500)] string? LogoUrl = null,
    [StringLength(2000)] string? Description = null);

public sealed record OrganizationRegisteredResponse(
    Guid AccountId,
    Guid OrganizationId,
    string Slug,
    OrganizationStatus Status);
