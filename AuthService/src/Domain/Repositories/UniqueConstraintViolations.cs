using Dprental.AuthService.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.AuthService.Domain.Repositories;

/// <summary>Dịch lỗi unique violation của Postgres sang exception domain dựa trên tên index bị vi phạm.</summary>
internal static class UniqueConstraintViolations
{
    internal const string UserCitizenIdIndex = "IX_Users_CitizenId";
    internal const string OrganizationSlugIndex = "IX_Organizations_Slug";
    internal const string OrganizationTaxCodeIndex = "IX_Organizations_TaxCode";

    internal static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Trả exception domain tương ứng; mặc định coi là trùng email vì đó là unique index đầu tiên của User.</summary>
    internal static Exception ToUserException(DbUpdateException exception) =>
        ConstraintName(exception) switch
        {
            UserCitizenIdIndex => new DuplicateCitizenIdException("Số CCCD đã được sử dụng.", exception),
            _ => new DuplicateEmailException("Email đã được sử dụng.", exception)
        };

    /// <summary>Dùng cho thao tác ghi cả User lẫn Organization trong cùng một lần SaveChanges.</summary>
    internal static Exception ToOrganizationException(DbUpdateException exception) =>
        ConstraintName(exception) switch
        {
            OrganizationTaxCodeIndex => new DuplicateTaxCodeException("Mã số thuế đã được đăng ký.", exception),
            OrganizationSlugIndex => new DuplicateSlugException("Slug của organization đã tồn tại.", exception),
            _ => ToUserException(exception)
        };

    private static string? ConstraintName(DbUpdateException exception) =>
        (exception.InnerException as PostgresException)?.ConstraintName;
}
