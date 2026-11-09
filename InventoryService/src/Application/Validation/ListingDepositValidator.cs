using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Validation;

/// <summary>
/// Tiền cọc của listing phải lớn hơn giá thuê theo ngày (DPR-275).
/// </summary>
public static class ListingDepositValidator
{
       public static Result ValidateDepositExceedsDailyRate(decimal depositAmount, decimal dailyRate)
    {
        if (depositAmount <= dailyRate)
        {
            return Error.Validation(
                "DEPOSIT_NOT_GREATER_THAN_DAILY_RATE",
                $"Tiền cọc phải lớn hơn giá thuê theo ngày ({dailyRate})."
                );
        }
        return Result.Success();
    }
}