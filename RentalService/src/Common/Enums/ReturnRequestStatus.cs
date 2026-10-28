using System.Text.Json.Serialization;

namespace Dprental.RentalService.Common.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReturnRequestStatus
{
    PendingSupplierConfirmation = 0,
    Disputed = 1,
    Confirmed = 2,
    Rejected = 3,
    Resolved = 4
}
