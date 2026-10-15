using Dprental.PaymentService.Application.DTOs.Bank;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.IServices;

public interface IInvoicePaymentProcessor
{
    Task<Result> ProcessAsync(VerifiedBankTransaction transaction, CancellationToken ct = default);
}
