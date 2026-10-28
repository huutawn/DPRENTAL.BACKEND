using System.Text.Json.Serialization;

namespace Dprental.RentalService.Common.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReturnRequestUnitStatus
{
    PendingSupplierConfirmation = 0,
    Confirmed = 1,
    Rejected = 2,
    Disputed = 3
}
