using System.Diagnostics.CodeAnalysis;

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
    Failure = 7
}

[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Consumed only from C#.")]
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Validation)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    public static Error Validation(string code, string message) =>
        new(code, message, ErrorType.Validation);

    public static Error Unauthorized(string code, string message) =>
        new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) =>
        new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) =>
        new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) =>
        new(code, message, ErrorType.Conflict);

    public static Error Expired(string code, string message) =>
        new(code, message, ErrorType.Expired);

    public static Error Unprocessable(string code, string message) =>
        new(code, message, ErrorType.Unprocessable);

    public static Error Failure(string code, string message) =>
        new(code, message, ErrorType.Failure);
}

