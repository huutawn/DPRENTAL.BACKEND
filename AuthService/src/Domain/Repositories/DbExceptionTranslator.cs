using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.AuthService.Domain.Repositories;

/// <summary>Dịch DbUpdateException thành StorageConstraintViolationException với tên constraint chính xác.</summary>
internal static class DbExceptionTranslator
{
    internal static bool TryTranslate(DbUpdateException exception, out StorageConstraintViolationException constraintException)
    {
        if (exception.InnerException is PostgresException pgEx)
        {
            StorageConstraintViolationType type = pgEx.SqlState switch
            {
                PostgresErrorCodes.UniqueViolation => StorageConstraintViolationType.Unique,
                PostgresErrorCodes.ForeignKeyViolation => StorageConstraintViolationType.ForeignKey,
                PostgresErrorCodes.CheckViolation => StorageConstraintViolationType.Check,
                PostgresErrorCodes.NotNullViolation => StorageConstraintViolationType.NotNull,
                _ => StorageConstraintViolationType.Other
            };

            constraintException = new StorageConstraintViolationException(
                type,
                pgEx.ConstraintName,
                pgEx.Message,
                exception);
            return true;
        }

        constraintException = null!;
        return false;
    }
}
