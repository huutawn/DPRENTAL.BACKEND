namespace Dprental.RentalService.Common.Enums;

public enum RentalOrderStatus
{
    PendingPayment = 0,
    PendingSupplierConfirmation = 1,
    AwaitingHandover = 2,
    InRental = 3,
    PendingSettlement = 4,
    Cozmpleted = 5,
    Rejected = 6,
    Expired = 7,
    Cancelled = 8
}
