using Dprental.AuthService.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.AuthService.Domain.Repositories;

/// <summary>
/// Dịch lỗi unique violation của Postgres sang exception domain dựa trên đúng tên index bị vi phạm.
/// Index không có trong danh sách thì trả <c>null</c> để caller ném lại lỗi gốc — không đoán.
/// </summary>
internal static class UniqueConstraintViolations
{
    internal const string UserEmailIndex = "IX_Users_Email";
    internal const string UserCitizenIdIndex = "IX_Users_CitizenId";
    internal const string OrganizationSlugIndex = "IX_Organizations_Slug";
    internal const string OrganizationTaxCodeIndex = "IX_Organizations_TaxCode";
    internal const string MembershipUserOrganizationIndex = "IX_OrganizationMemberships_UserId_OrganizationId";

    internal static Exception? ToUserException(DbUpdateException exception) =>
        ViolatedIndex(exception) switch
        {
            UserEmailIndex => new DuplicateEmailException("Email đã được sử dụng.", exception),
            UserCitizenIdIndex => new DuplicateCitizenIdException("Số CCCD đã được sử dụng.", exception),
            _ => null
        };

    /// <summary>Dùng cho thao tác ghi cả User lẫn Organization trong cùng một lần SaveChanges.</summary>
    internal static Exception? ToOrganizationException(DbUpdateException exception) =>
        ViolatedIndex(exception) switch
        {
            OrganizationTaxCodeIndex => new DuplicateTaxCodeException("Mã số thuế đã được đăng ký.", exception),
            OrganizationSlugIndex => new DuplicateSlugException("Slug của organization đã tồn tại.", exception),
            _ => ToUserException(exception)
        };

    internal static Exception? ToMembershipException(DbUpdateException exception) =>
        ViolatedIndex(exception) switch
        {
            MembershipUserOrganizationIndex => new DuplicateMembershipException("User đã là thành viên của organization này.", exception),
            _ => null
        };

    /// <summary>Tên index chỉ có nghĩa khi đúng là unique violation; lỗi FK/check cũng mang ConstraintName nên phải lọc SqlState.</summary>
    private static string? ViolatedIndex(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            ? postgres.ConstraintName
            : null;
}
