using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Checkout;

/// <summary>
/// Tạo hóa đơn Payment cho đơn đang chờ thanh toán và gắn InvoiceRef vào đơn.
/// Gọi lại nhiều lần cho cùng một đơn chỉ tạo một hóa đơn.
/// </summary>
public interface ICheckoutInvoiceIssuer
{
    /// <summary>
    /// <paramref name="order"/> phải đã nạp đủ RentalItem và đang được DbContext của request theo dõi.
    /// </summary>
    /// <returns>
    /// Thành công khi đơn có hóa đơn. Thất bại là một trong <see cref="CheckoutErrors.InvoiceTemporarilyUnavailable"/>,
    /// <see cref="CheckoutErrors.InvoiceRejected"/> hoặc <see cref="CheckoutErrors.OrderExpired"/>; đơn hàng vẫn được giữ nguyên.
    /// </returns>
    Task<Result> EnsureInvoiceAsync(RentalOrder order, CancellationToken cancellationToken = default);
}
