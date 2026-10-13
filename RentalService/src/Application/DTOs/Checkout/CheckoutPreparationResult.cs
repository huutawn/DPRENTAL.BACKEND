using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.DTOs.Checkout;

/// <summary>
/// Kết quả bước chuẩn bị checkout. Khi thất bại, ngoài <see cref="Error"/> còn mang chi tiết
/// theo từng ô nhập hoặc từng dòng giỏ để client hiển thị đúng chỗ.
/// </summary>
public sealed class CheckoutPreparationResult
{
    private readonly CheckoutPlan? _plan;

    private CheckoutPreparationResult(
        CheckoutPlan? plan,
        Error error,
        IReadOnlyList<CheckoutFieldError>? fieldErrors = null,
        IReadOnlyList<CheckoutLineIssue>? lineIssues = null,
        CheckoutPlan? currentQuote = null,
        string? pendingOrderCode = null)
    {
        _plan = plan;
        Error = error;
        FieldErrors = fieldErrors ?? [];
        LineIssues = lineIssues ?? [];
        CurrentQuote = currentQuote;
        PendingOrderCode = pendingOrderCode;
    }

    public bool IsSuccess => Error == Error.None;

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public CheckoutPlan Plan => IsSuccess
        ? _plan!
        : throw new InvalidOperationException("Không thể truy xuất kế hoạch checkout từ một kết quả thất bại.");

    public IReadOnlyList<CheckoutFieldError> FieldErrors { get; }

    public IReadOnlyList<CheckoutLineIssue> LineIssues { get; }

    /// <summary>Giá và số tiền hiện tại, chỉ có khi lỗi là <see cref="CheckoutErrors.PriceChanged"/>.</summary>
    public CheckoutPlan? CurrentQuote { get; }

    /// <summary>Mã đơn hàng đang chờ thanh toán, chỉ có khi lỗi là <see cref="CheckoutErrors.PendingOrderExists"/>.</summary>
    public string? PendingOrderCode { get; }

    public static CheckoutPreparationResult Success(CheckoutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(plan, Error.None);
    }

    public static CheckoutPreparationResult Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error == Error.None)
        {
            throw new ArgumentException("Kết quả thất bại phải chứa lỗi.", nameof(error));
        }

        return new(null, error);
    }

    public static CheckoutPreparationResult InvalidRequest(IReadOnlyList<CheckoutFieldError> fieldErrors) =>
        new(null, CheckoutErrors.InvalidRequest, fieldErrors: fieldErrors);

    public static CheckoutPreparationResult LinesRejected(Error error, IReadOnlyList<CheckoutLineIssue> lineIssues) =>
        new(null, error, lineIssues: lineIssues);

    public static CheckoutPreparationResult PriceChanged(CheckoutPlan currentQuote) =>
        new(null, CheckoutErrors.PriceChanged, currentQuote: currentQuote);

    public static CheckoutPreparationResult PendingOrder(string orderCode) =>
        new(null, CheckoutErrors.PendingOrderExists, pendingOrderCode: orderCode);
}
