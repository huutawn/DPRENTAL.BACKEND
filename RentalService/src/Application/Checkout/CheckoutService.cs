using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Checkout;

public sealed class CheckoutService : ICheckoutService
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly ICheckoutPreparationService _preparation;
    private readonly ICheckoutHoldOrchestrator _holds;
    private readonly ICheckoutOrderWriter _writer;
    private readonly ICheckoutInvoiceIssuer _invoices;
    private readonly IRentalOrderRepository _orders;
    private readonly IOrderLockManager _locks;
    private readonly TimeProvider _timeProvider;

    public CheckoutService(
        ICheckoutPreparationService preparation,
        ICheckoutHoldOrchestrator holds,
        ICheckoutOrderWriter writer,
        ICheckoutInvoiceIssuer invoices,
        IRentalOrderRepository orders,
        IOrderLockManager locks,
        TimeProvider timeProvider)
    {
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<CheckoutResult> SubmitAsync(
        CheckoutRenterContext renter,
        CheckoutRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renter);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (renter.RenterId == Guid.Empty)
        {
            return CheckoutResult.Failure(CheckoutErrors.Unauthenticated);
        }

        string operationId = CheckoutRules.DeriveOperationId(renter.RenterId, idempotencyKey);

        // Khóa theo renter (không phải theo đơn): mỗi renter chỉ có một checkout chạy tại một thời điểm,
        // nên bấm đúp hay mở nhiều tab không thể cùng giữ lịch rồi nhả nhầm hold của nhau.
        IAsyncDisposable renterLock;
        try
        {
            renterLock = await _locks.AcquireOrderLockAsync(renter.RenterId, LockTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return CheckoutResult.Failure(CheckoutErrors.InProgress);
        }

        await using (renterLock)
        {
            RentalOrder? existing = await _orders.GetByCheckoutOperationIdAsync(operationId, cancellationToken);
            if (existing is not null)
            {
                return await ReplayAsync(existing, renter.RenterId, request, operationId, cancellationToken);
            }

            await EnsurePendingOrderHasInvoiceAsync(renter.RenterId, cancellationToken);

            CheckoutPreparationResult prepared = await _preparation.PrepareAsync(renter, request, cancellationToken);
            if (prepared.IsFailure)
            {
                return CheckoutResult.From(prepared);
            }

            DateTimeOffset utcNow = _timeProvider.GetUtcNow();
            CheckoutHoldResult held = await _holds.HoldAllLinesAsync(prepared.Plan, operationId, utcNow, cancellationToken);
            if (!held.IsSuccess)
            {
                return CheckoutResult.From(held);
            }

            Result<RentalOrder> order = await _writer.CreateOrderAsync(
                prepared.Plan, held.HeldLines, operationId, utcNow, cancellationToken);
            if (order.IsFailure)
            {
                return CheckoutResult.Failure(order.Error);
            }

            return await IssueInvoiceAsync(order.Value, isReplay: false, cancellationToken);
        }
    }

    /// <summary>
    /// Key đã có đơn: trả lại đơn đó, không chuẩn bị và không giữ lịch lần nữa. Đơn chưa có hóa đơn thì tạo nốt.
    /// </summary>
    private async Task<CheckoutResult> ReplayAsync(
        RentalOrder existing,
        Guid renterId,
        CheckoutRequest request,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (existing.RenterId != renterId || !IsSameSelection(existing, renterId, request, operationId))
        {
            return CheckoutResult.Failure(CheckoutErrors.IdempotencyKeyReused);
        }

        return await IssueInvoiceAsync(existing, isReplay: true, cancellationToken);
    }

    /// <summary>RentalItemId sinh từ dòng giỏ, nên so được yêu cầu gửi lại với đơn mà không cần lưu lại request.</summary>
    private static bool IsSameSelection(RentalOrder order, Guid renterId, CheckoutRequest request, string operationId)
    {
        if (request.CartItemIds is null)
        {
            return false;
        }

        HashSet<Guid> requestedItemIds = request.CartItemIds
            .Select(cartItemId => CheckoutRules.DeriveRentalItemId(renterId, operationId, cartItemId))
            .ToHashSet();

        return requestedItemIds.SetEquals(order.Items.Select(i => i.Id));
    }

    /// <summary>
    /// Renter quay lại bằng Idempotency-Key mới trong lúc đơn chờ thanh toán chưa có hóa đơn: tạo nốt hóa đơn
    /// để đơn đó thanh toán được. Bước chuẩn bị ngay sau vẫn báo đang có đơn chưa thanh toán.
    /// </summary>
    private async Task EnsurePendingOrderHasInvoiceAsync(Guid renterId, CancellationToken cancellationToken)
    {
        RentalOrder? pending = await _orders.GetPendingPaymentByRenterIdAsync(renterId, cancellationToken);
        if (pending is null ||
            !string.IsNullOrWhiteSpace(pending.InvoiceRef) ||
            pending.PaymentDeadline <= _timeProvider.GetUtcNow())
        {
            return;
        }

        RentalOrder? detailed = await _orders.GetByIdWithDetailsAsync(pending.Id, cancellationToken);
        if (detailed is not null)
        {
            await _invoices.EnsureInvoiceAsync(detailed, cancellationToken);
        }
    }

    private async Task<CheckoutResult> IssueInvoiceAsync(RentalOrder order, bool isReplay, CancellationToken cancellationToken)
    {
        Result invoice = await _invoices.EnsureInvoiceAsync(order, cancellationToken);
        return invoice.IsSuccess
            ? CheckoutResult.Success(order, isReplay)
            : CheckoutResult.OrderFailure(invoice.Error, order);
    }
}
