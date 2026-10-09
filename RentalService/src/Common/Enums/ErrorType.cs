namespace Dprental.RentalService.Common;

public enum ErrorType
{
    Validation = 0,
    Unauthorized = 1,
    Forbidden = 2,
    NotFound = 3,
    Conflict = 4,
    Expired = 5,
    Unprocessable = 6,
    Failure = 7,
    Unavailable = 8
}
