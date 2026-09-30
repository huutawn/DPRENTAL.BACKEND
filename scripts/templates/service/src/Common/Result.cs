using System.Diagnostics.CodeAnalysis;

namespace Dprental.__SERVICE_NAME__.Common;

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

public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (isSuccess != (error == Error.None))
        {
            throw new ArgumentException("Thao tác thành công không thể chứa lỗi và thao tác thất bại phải chứa lỗi.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Không thể truy xuất giá trị từ một kết quả thất bại.");

    public static implicit operator Result<TValue>(TValue value) => Success(value);
    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}
