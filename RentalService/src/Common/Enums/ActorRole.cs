using System.Text.Json.Serialization;

namespace Dprental.RentalService.Common.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActorRole
{
    Renter = 0,
    Supplier = 1,
    Admin = 2,
    System = 3
}
