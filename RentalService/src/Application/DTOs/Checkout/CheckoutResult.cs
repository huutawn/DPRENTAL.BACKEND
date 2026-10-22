using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.DTOs.Checkout;

/// <summary>
/// Kết quả của một lần gửi checkout. Khi thất bại, ngoài <see cref="Error"/> còn mang chi tiết của bước bị lỗi
/// để client hiển thị đúng chỗ.
/// </summary>
public sealed class CheckoutResult
{
    private CheckoutResult(
        Error error,
        RentalOrder? order = null,
        bool isReplay = false,
        CheckoutPreparationResult? preparation = null,
        IReadOnlyList<CheckoutHoldConflict>? holdConflicts = null,
        string? operationId = null)
    {
        Error = error;
        Order = order;
        IsReplay = isReplay;
        FieldErrors = preparation?.FieldErrors ?? [];
        LineIssues = preparation?.LineIssues ?? [];
        CurrentQuote = preparation?.CurrentQuote;
        PendingOrderCode = preparation?.PendingOrderCode;
        HoldConflicts = holdConflicts ?? [];
        OperationId = operationId;
    }

    public bool IsSuccess => Error == Error.None;

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    /// <summary>
    /// Luôn có khi thành công. Khi thất bại chỉ có nếu đơn đã được tạo và lỗi nằm ở bước hóa đơn.
    /// </summary>
    public RentalOrder? Order { get; }

    /// <summary>Đơn đã được tạo từ một lần gửi trước với cùng Idempotency-Key.</summary>
    public bool IsReplay { get; }

    /// <summary>Gửi lại đúng yêu cầu này với cùng Idempotency-Key có thể thành công.</summary>
    public bool IsRetryable => Error.Type == ErrorType.ServiceUnavailable || Error == CheckoutErrors.InProgress || Error == CheckoutErrors.CheckoutNotReady;

    public string? OperationId { get; }
    public IReadOnlyList<CheckoutFieldError> FieldErrors { get; }

    public IReadOnlyList<CheckoutLineIssue> LineIssues { get; }

    /// <summary>Giá và số tiền hiện tại, chỉ có khi lỗi là <see cref="CheckoutErrors.PriceChanged"/>.</summary>
    public CheckoutPlan? CurrentQuote { get; }

    /// <summary>Mã đơn đang chờ thanh toán, chỉ có khi lỗi là <see cref="CheckoutErrors.PendingOrderExists"/>.</summary>
    public string? PendingOrderCode { get; }

    /// <summary>Các dòng không giữ được lịch, chỉ có khi lỗi là <see cref="CheckoutErrors.HoldFailed"/>.</summary>
    public IReadOnlyList<CheckoutHoldConflict> HoldConflicts { get; }

    public static CheckoutResult Success(RentalOrder order, bool isReplay)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new(Error.None, order, isReplay);
    }

    public static CheckoutResult Failure(Error error) => new(RequireError(error));

    /// <summary>Đơn đã tồn tại nhưng chưa dùng được, ví dụ chưa tạo được hóa đơn.</summary>
    public static CheckoutResult OrderFailure(Error error, RentalOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new(RequireError(error), order);
    }

    public static CheckoutResult From(CheckoutPreparationResult preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return new(RequireError(preparation.Error), preparation: preparation);
    }

    public static CheckoutResult From(CheckoutHoldResult hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return new(RequireError(hold.Error), holdConflicts: hold.Conflicts);
    }

    public static CheckoutResult HoldFailure(Error error, IReadOnlyList<CheckoutHoldConflict> conflicts) =>
        new(RequireError(error), holdConflicts: conflicts);

    public static CheckoutResult CheckoutNotReady(string operationId) =>
        new(CheckoutErrors.CheckoutNotReady, operationId: operationId);

    private static Error RequireError(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error == Error.None
            ? throw new ArgumentException("Kết quả thất bại phải chứa lỗi.", nameof(error))
            : error;
    }
}
