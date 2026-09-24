using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class UniqueConstraintViolationsTests
{
    [Theory]
    [InlineData(UniqueConstraintViolations.UserEmailIndex, typeof(DuplicateEmailException))]
    [InlineData(UniqueConstraintViolations.UserCitizenIdIndex, typeof(DuplicateCitizenIdException))]
    public void ToUserExceptionMapsKnownIndex(string index, Type expected)
    {
        Exception? mapped = UniqueConstraintViolations.ToUserException(DbError(PostgresErrorCodes.UniqueViolation, index));

        Assert.IsType(expected, mapped);
    }

    [Theory]
    [InlineData(UniqueConstraintViolations.OrganizationTaxCodeIndex, typeof(DuplicateTaxCodeException))]
    [InlineData(UniqueConstraintViolations.OrganizationSlugIndex, typeof(DuplicateSlugException))]
    [InlineData(UniqueConstraintViolations.UserEmailIndex, typeof(DuplicateEmailException))]
    public void ToOrganizationExceptionMapsKnownIndex(string index, Type expected)
    {
        Exception? mapped = UniqueConstraintViolations.ToOrganizationException(DbError(PostgresErrorCodes.UniqueViolation, index));

        Assert.IsType(expected, mapped);
    }

    [Fact]
    public void ToMembershipExceptionMapsKnownIndex()
    {
        Exception? mapped = UniqueConstraintViolations.ToMembershipException(
            DbError(PostgresErrorCodes.UniqueViolation, UniqueConstraintViolations.MembershipUserOrganizationIndex));

        Assert.IsType<DuplicateMembershipException>(mapped);
    }

    [Fact]
    public void UnknownUniqueIndexIsNotGuessedAsDuplicateEmail()
    {
        DbUpdateException error = DbError(PostgresErrorCodes.UniqueViolation, "IX_Users_PhoneNumber");

        Assert.Null(UniqueConstraintViolations.ToUserException(error));
        Assert.Null(UniqueConstraintViolations.ToOrganizationException(error));
        Assert.Null(UniqueConstraintViolations.ToMembershipException(error));
    }

    [Fact]
    public void NonUniqueErrorOnKnownConstraintIsNotMapped()
    {
        // Lỗi FK/check cũng mang ConstraintName — chỉ unique violation mới được dịch.
        DbUpdateException error = DbError(PostgresErrorCodes.ForeignKeyViolation, UniqueConstraintViolations.UserEmailIndex);

        Assert.Null(UniqueConstraintViolations.ToUserException(error));
    }

    [Fact]
    public void ErrorWithoutPostgresInnerExceptionIsNotMapped()
    {
        DbUpdateException error = new("Không có inner exception của Postgres.");

        Assert.Null(UniqueConstraintViolations.ToUserException(error));
    }

    private static DbUpdateException DbError(string sqlState, string constraintName) =>
        new("SaveChanges thất bại.", new PostgresException(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            constraintName: constraintName));
}
