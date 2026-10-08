namespace Dprental.RentalService.Common.Enums;

public enum RentalItemStatus
{
    PendingPayment = 0,
    PendingSupplierConfirmation = 1,
    AwaitingHandover = 2,
    InRental = 3,
    PendingSettlement = 4,
    Completed = 5,
    Rejected = 6,
    Expired = 7,
    Cancelled = 8
}
