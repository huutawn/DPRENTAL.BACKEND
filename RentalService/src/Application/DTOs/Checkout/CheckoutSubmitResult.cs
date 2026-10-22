using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.DTOs.Checkout;

/// <summary>
/// Kết quả gửi checkout bất đồng bộ. Thành công nghĩa là đã nhận yêu cầu và đang chờ Inventory giữ lịch;
/// thất bại mang đủ chi tiết lỗi (ô nhập sai, giá mới, đơn đang chờ thanh toán...) để client hiển thị đúng chỗ.
/// </summary>
public sealed class CheckoutSubmitResult
{
    private readonly CheckoutResponse? _value;

    private CheckoutSubmitResult(CheckoutResponse? value, CheckoutResult? failure)
    {
        _value = value;
        Failure = failure;
    }

    public bool IsSuccess => Failure is null;

    public bool IsFailure => !IsSuccess;

    public Error Error => Failure?.Error ?? Error.None;

    public CheckoutResponse Value => _value
        ?? throw new InvalidOperationException("Không thể truy xuất kết quả checkout từ một kết quả thất bại.");

    /// <summary>Chi tiết lỗi, chỉ có khi thất bại.</summary>
    public CheckoutResult? Failure { get; }

    public static CheckoutSubmitResult Success(CheckoutResponse value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
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

    public static implicit operator CheckoutSubmitResult(CheckoutResponse value) => Success(value);
    public static implicit operator CheckoutSubmitResult(CheckoutResult failure) => Rejected(failure);

    public static implicit operator CheckoutSubmitResult(Error error) => Rejected(CheckoutResult.Failure(error));

    public static implicit operator CheckoutSubmitResult(CheckoutPreparationResult preparation) =>
        Rejected(CheckoutResult.From(preparation));
}
