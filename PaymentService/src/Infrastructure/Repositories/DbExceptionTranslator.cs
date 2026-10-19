using Microsoft.EntityFrameworkCore;
using Npgsql;
using Dprental.PaymentService.Domain.Repository;

namespace Dprental.PaymentService.Infrastructure.Repositories;

internal static class DbExceptionTranslator
{
    internal static bool TryTranslate(DbUpdateException exception, out StorageConstraintViolationException constraintException)
    {
        if (exception.InnerException is PostgresException pgEx)
        {
            if (pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                var type = pgEx.ConstraintName switch
                {
                    StorageConstraints.OperationKey => StorageConstraintViolationType.OperationKey,
                    StorageConstraints.RentalOrderId => StorageConstraintViolationType.RentalOrderId,
                    StorageConstraints.PaymentCode => StorageConstraintViolationType.PaymentCode,
                    StorageConstraints.PaymentRef => StorageConstraintViolationType.PaymentRef,
                    StorageConstraints.PaymentTransaction => StorageConstraintViolationType.PaymentTransaction,
                    _ => StorageConstraintViolationType.Other
                };

                constraintException = new StorageConstraintViolationException(
                    type,
                    pgEx.ConstraintName,
                    pgEx.Message,
                    exception);
                return true;
            }
        }

        string msg = exception.InnerException?.Message ?? exception.Message;
        if (msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("unique", StringComparison.OrdinalIgnoreCase))
        {
            StorageConstraintViolationType type = StorageConstraintViolationType.Other;
            string? constraintName = null;

            if (msg.Contains(StorageConstraints.OperationKey, StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("OperationKey", StringComparison.OrdinalIgnoreCase))
            {
                type = StorageConstraintViolationType.OperationKey;
                constraintName = StorageConstraints.OperationKey;
            }
            else if (msg.Contains(StorageConstraints.RentalOrderId, StringComparison.OrdinalIgnoreCase) ||
                     msg.Contains("RentalOrderId", StringComparison.OrdinalIgnoreCase))
            {
                type = StorageConstraintViolationType.RentalOrderId;
                constraintName = StorageConstraints.RentalOrderId;
            }
            else if (msg.Contains(StorageConstraints.PaymentCode, StringComparison.OrdinalIgnoreCase) ||
                     msg.Contains("PaymentCode", StringComparison.OrdinalIgnoreCase))
            {
                type = StorageConstraintViolationType.PaymentCode;
                constraintName = StorageConstraints.PaymentCode;
            }
            else if (msg.Contains(StorageConstraints.PaymentRef, StringComparison.OrdinalIgnoreCase) ||
                     msg.Contains("PaymentRef", StringComparison.OrdinalIgnoreCase))
            {
                type = StorageConstraintViolationType.PaymentRef;
                constraintName = StorageConstraints.PaymentRef;
            }
            else if (msg.Contains(StorageConstraints.PaymentTransaction, StringComparison.OrdinalIgnoreCase) ||
                     msg.Contains("payment_transactions_Provider_ProviderTransactionId", StringComparison.OrdinalIgnoreCase))
            {
                type = StorageConstraintViolationType.PaymentTransaction;
                constraintName = StorageConstraints.PaymentTransaction;
            }

            if (type != StorageConstraintViolationType.Other)
            {
                constraintException = new StorageConstraintViolationException(
                    type,
                    constraintName,
                    msg,
                    exception);
                return true;
            }
        }

        constraintException = null!;
        return false;
    }
}
