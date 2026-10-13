using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface IPaymentRecordingService
{
    Task<Result> RecordPaymentReceivedAsync(
        string invoiceRef,
        string paymentRef,
        decimal amount,
        string currency,
        DateTimeOffset paidAt,
        CancellationToken ct = default);
}
