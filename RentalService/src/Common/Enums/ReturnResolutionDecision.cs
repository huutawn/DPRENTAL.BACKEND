using System.Text.Json.Serialization;

namespace Dprental.RentalService.Common.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReturnResolutionDecision
{
    ConfirmReceived = 1,
    RejectRequest = 2
}
