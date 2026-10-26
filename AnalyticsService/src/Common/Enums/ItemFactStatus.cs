namespace Dprental.AnalyticsService.Common.Enums;

public enum ItemFactStatus
{
    PendingPayment,
    PendingSupplierConfirmation,
    AwaitingHandover,
    InRental,
    PendingSettlement,
    Completed, Rejected,
    Expired,
    Cancelled
}