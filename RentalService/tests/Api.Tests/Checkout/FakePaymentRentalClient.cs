using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Api.Tests.Checkout;

/// <summary>
/// Payment giả cho test hóa đơn: ghi lại mọi lời gọi tạo hóa đơn và replay theo OperationKey.
/// </summary>
internal sealed class FakePaymentRentalClient : IPaymentRentalClient
{
    public static readonly Error Down = Error.ServiceUnavailable("Payment.Unavailable", "Payment không phản hồi.");

    private readonly Dictionary<string, CreateInvoiceResponse> _invoicesByOperationKey = [];

    public List<CreateInvoiceRequest> InvoiceRequests { get; } = [];

    /// <summary>Lỗi trả cho các lần gọi kế tiếp theo thứ tự; hết hàng đợi thì tạo hóa đơn bình thường.</summary>
    public Queue<Error> Failures { get; } = new();

    /// <summary>Khác null thì mọi lần gọi đều lỗi.</summary>
    public Error? AlwaysFailWith { get; set; }

    /// <summary>Sửa phản hồi trước khi trả, để giả lập Payment trả sai hợp đồng.</summary>
    public Func<CreateInvoiceResponse, CreateInvoiceResponse>? Tamper { get; set; }

    /// <summary>Khác null thì lời gọi dừng ở đây cho đến khi task hoàn tất, để dựng hai request chồng nhau.</summary>
    public Task? Gate { get; set; }
    public Action<CreateInvoiceRequest>? OnCreateInvoice { get; set; }


    public int CreatedInvoiceCount => _invoicesByOperationKey.Count;

    public async Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default)
    {
        InvoiceRequests.Add(request);
        OnCreateInvoice?.Invoke(request);

        if (Gate is not null)
        {
            await Gate;
        }

        if (AlwaysFailWith is not null)
        {
            return AlwaysFailWith;
        }

        if (Failures.TryDequeue(out Error? failure))
        {
            return failure;
        }

        if (!_invoicesByOperationKey.TryGetValue(request.OperationKey, out CreateInvoiceResponse? invoice))
        {
            invoice = new(
                $"INV-{_invoicesByOperationKey.Count + 1}",
                request.TotalAmount,
                request.Currency,
                request.PaymentDeadline,
                false);
            _invoicesByOperationKey[request.OperationKey] = invoice;
        }
        else
        {
            invoice = invoice with { IsReplay = true };
        }

        return Tamper is null ? invoice : Tamper(invoice);
    }

    public List<CancelInvoiceRequest> CancelRequests { get; } = [];

    public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default)
    {
        CancelRequests.Add(request);
        return Task.FromResult(Result.Success(
            new CancelInvoiceResponse(request.InvoiceRef, true, DateTimeOffset.UnixEpoch, false)));
    }

    public List<AcceptLatePaymentRequest> AcceptLatePaymentRequests { get; } = [];

    public Task<Result<AcceptLatePaymentResponse>> AcceptLatePaymentAsync(
        string invoiceRef, AcceptLatePaymentRequest request, CancellationToken ct = default)
    {
        AcceptLatePaymentRequests.Add(request);
        return Task.FromResult(Result.Success(
            new AcceptLatePaymentResponse(invoiceRef, "PAID", false, DateTimeOffset.UtcNow)));
    }

    public List<RequestRefundRequest> RefundRequests { get; } = [];

    public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default)
    {
        RefundRequests.Add(request);
        return Task.FromResult(Result.Success(
            new RequestRefundResponse($"ref-{RefundRequests.Count}", request.OperationKey, "Accepted", request.TotalRefundAmount, "VND", false)));
    }

    public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
