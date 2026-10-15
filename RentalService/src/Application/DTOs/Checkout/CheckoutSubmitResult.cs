using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.DTOs.Checkout;

/// <summary>
/// Kết quả gửi checkout bất đồng bộ. Thành công nghĩa là đã nhận yêu cầu và đang chờ Inventory giữ lịch;
/// thất bại mang đủ chi tiết lỗi (ô nhập sai, giá mới, đơn đang chờ thanh toán...) để client hiển thị đúng chỗ.
/// </summary>
public sealed class CheckoutSubmitResult
{
    private readonly CheckoutOperationAcceptedResponse? _accepted;

    private CheckoutSubmitResult(CheckoutOperationAcceptedResponse? accepted, CheckoutResult? failure)
    {
        _accepted = accepted;
        Failure = failure;
    }

    public bool IsSuccess => Failure is null;

    public bool IsFailure => !IsSuccess;

    public Error Error => Failure?.Error ?? Error.None;

    public CheckoutOperationAcceptedResponse Value => _accepted
        ?? throw new InvalidOperationException("Không thể truy xuất thao tác checkout từ một kết quả thất bại.");

    /// <summary>Chi tiết lỗi, chỉ có khi thất bại.</summary>
    public CheckoutResult? Failure { get; }

    public static CheckoutSubmitResult Accepted(CheckoutOperationAcceptedResponse accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return new(accepted, null);
    }

    public static CheckoutSubmitResult Rejected(CheckoutResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.IsSuccess)
        {
            throw new ArgumentException("Kết quả thất bại phải chứa lỗi.", nameof(failure));
        }

        return new(null, failure);
    }

    public static implicit operator CheckoutSubmitResult(CheckoutOperationAcceptedResponse accepted) => Accepted(accepted);

    public static implicit operator CheckoutSubmitResult(Error error) => Rejected(CheckoutResult.Failure(error));

    public static implicit operator CheckoutSubmitResult(CheckoutPreparationResult preparation) =>
        Rejected(CheckoutResult.From(preparation));
}
