#pragma warning disable CA1848, CA1873
using System.Text.Encodings.Web;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Checkout;

/// <remarks>
/// Chuyển kế hoạch checkout và các allocation đã giữ thành order, item, suborder và unit assignment.
/// Không tự giữ máy hay tạo invoice; CheckoutService sở hữu thứ tự điều phối và transaction bao quanh bước ghi này.
/// </remarks>
public sealed class CheckoutOrderWriter : ICheckoutOrderWriter
{
    // Giữ nguyên chữ có dấu để chuỗi không phình quá giới hạn 256 ký tự của RefundDestinationRef.
    private static readonly JsonSerializerOptions RefundDestinationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IRentalOrderRepository _orders;
    private readonly IRentalCartRepository _carts;
    private readonly IOrderCodeGenerator _orderCodes;
    private readonly IUnitOfWork? _unitOfWork;
    private readonly ILogger<CheckoutOrderWriter> _logger;

    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public CheckoutOrderWriter(
        IRentalOrderRepository orders,
        IRentalCartRepository carts,
        IOrderCodeGenerator orderCodes,
        ILogger<CheckoutOrderWriter> logger)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _carts = carts ?? throw new ArgumentNullException(nameof(carts));
        _orderCodes = orderCodes ?? throw new ArgumentNullException(nameof(orderCodes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public CheckoutOrderWriter(
        IRentalOrderRepository orders,
        IRentalCartRepository carts,
        IOrderCodeGenerator orderCodes,
        IUnitOfWork unitOfWork,
        ILogger<CheckoutOrderWriter> logger)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _carts = carts ?? throw new ArgumentNullException(nameof(carts));
        _orderCodes = orderCodes ?? throw new ArgumentNullException(nameof(orderCodes));
        _unitOfWork = unitOfWork;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Nhận kế hoạch và các dòng đã hold; chuyển vào luồng lưu dùng chung với định danh/deadline mà caller cung cấp.
    public Task<Result<RentalOrder>> CreateOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        return CreateOrderAsync(
            plan,
            heldLines,
            checkoutOperationId,
            Guid.NewGuid(),
            utcNow.Add(CheckoutRules.PaymentWindow),
            utcNow,
            cancellationToken);
    }

    // Nhận kế hoạch và các dòng đã hold; chuyển vào luồng lưu dùng chung với định danh/deadline mà caller cung cấp.
    public async Task<Result<RentalOrder>> CreateOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        Guid expectedRentalOrderId,
        DateTimeOffset holdExpiresAt,
        DateTimeOffset orderCreatedAt,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(heldLines);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutOperationId);

        // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
        try
        {
            return await SaveOrderAsync(
                plan,
                heldLines,
                checkoutOperationId,
                expectedRentalOrderId,
                holdExpiresAt,
                orderCreatedAt,
                cancellationToken);
        }
        // Bỏ thay đổi tracked khi lưu thất bại; giữ cách trả conflict hoặc chuyển tiếp exception của use case.
        catch (Exception ex)
        {
            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogWarning(ex, "Checkout {CheckoutOperationId} hỏng trong lúc lưu đơn hàng.", checkoutOperationId);
            _unitOfWork?.DiscardChanges();
            throw;
        }
    }

    // Tái sử dụng order cùng checkout nếu đã tồn tại; tạo snapshot order/assignment và loại các dòng đã mua khỏi giỏ trong luồng lưu.
    private async Task<Result<RentalOrder>> SaveOrderAsync(
        CheckoutPlan plan,
        IReadOnlyList<CheckoutHeldLine> heldLines,
        string checkoutOperationId,
        Guid expectedRentalOrderId,
        DateTimeOffset holdExpiresAt,
        DateTimeOffset orderCreatedAt,
        CancellationToken cancellationToken)
    {
        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        RentalOrder? existingOrder = await _orders.GetByCheckoutOperationIdAsync(checkoutOperationId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (existingOrder is not null && existingOrder.RenterId == plan.RenterId)
        {
            return existingOrder;
        }

        // Thuê ngay không đi qua giỏ: không kiểm tra và không gỡ dòng giỏ nào.
        RentalCart? cart = null;
        // Điều kiện này quyết định có thể tiếp tục hay phải trả FailAsync theo contract của use case.
        if (plan.CartId is { } cartId)
        {
            // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
            cart = await _carts.GetActiveByRenterIdAsync(plan.RenterId, cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (cart is null || cart.Id != cartId)
            {
                return await FailAsync(heldLines, RentalErrors.CartNotFound);
            }
        }

        // Cấp mã đơn qua abstraction sequence để các request đồng thời không tự sinh mã trùng.
        string orderCode = await _orderCodes.NextAsync(orderCreatedAt, cancellationToken);

        // Tạo order từ snapshot kế hoạch và mapping máy đã hold, giữ nguyên deadline/ID mà journal checkout đã ghi.
        Result<RentalOrder> order = RentalOrder.Create(
            expectedRentalOrderId,
            orderCode,
            plan.RenterId,
            plan.Currency,
            CheckoutRules.DeliveryMethod,
            plan.Delivery.RecipientName!,
            plan.Delivery.RecipientPhone!,
            $"{plan.Delivery.AddressDetail}, {plan.Delivery.Province}",
            plan.Delivery.Note,
            JsonSerializer.Serialize(plan.RefundAccount, RefundDestinationJsonOptions),
            plan.TermsVersion,
            plan.TermsDocumentRef,
            plan.TermsContentHash,
            checkoutOperationId,
            orderCreatedAt,
            heldLines.Select(ToInitialItem),
            paymentDeadline: holdExpiresAt);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (order.IsFailure)
        {
            return await FailAsync(heldLines, order.Error);
        }

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (cart is not null)
        {
            // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
            Result cartResult = cart.CompleteCheckout(plan.Lines.Select(l => l.CartItemId), orderCreatedAt);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (cartResult.IsFailure)
            {
                // Bỏ thay đổi đang tracked sau lỗi để lần xử lý tiếp theo không lưu nhầm dữ liệu của nhánh đã thất bại.
                _unitOfWork?.DiscardChanges();
                return await FailAsync(heldLines, cartResult.Error);
            }
        }

        // Đăng ký order mới cùng snapshot và các assignment để persistence lưu trong phạm vi caller đang quản lý.
        await _orders.CreateAsync(order.Value, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (_unitOfWork is not null)
        {
            // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
            try
            {
                // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            // Bỏ thay đổi tracked khi lưu thất bại; giữ cách trả conflict hoặc chuyển tiếp exception của use case.
            catch (DbUpdateException ex)
            {
                // Bỏ thay đổi đang tracked sau lỗi để lần xử lý tiếp theo không lưu nhầm dữ liệu của nhánh đã thất bại.
                _unitOfWork.DiscardChanges();

                // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
                RentalOrder? existing = await _orders.GetByCheckoutOperationIdAsync(checkoutOperationId, cancellationToken);
                // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
                if (existing is not null && existing.RenterId == plan.RenterId)
                {
                    return existing;
                }

                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogWarning(ex, "Không lưu được đơn hàng cho checkout {CheckoutOperationId}.", checkoutOperationId);
                return await FailAsync(heldLines, CheckoutErrors.OrderNotSaved);
            }
        }

        return order;
    }

    // Trả lỗi lưu order cho caller; việc bù trừ hold thuộc orchestration checkout, không được giả vờ đã nhả tại đây.
    private static Task<Result<RentalOrder>> FailAsync(IReadOnlyList<CheckoutHeldLine> heldLines, Error error) =>
        Task.FromResult<Result<RentalOrder>>(error);

    // Chuyển dòng đã giữ chỗ thành dữ liệu khởi tạo item và mapping AssetId/AllocationId; không tự chọn lại máy vật lý.
    private static InitialRentalItemDto ToInitialItem(CheckoutHeldLine held) => new(
        held.RentalItemId,
        held.Line.EquipmentListingId,
        held.Line.SupplierId,
        held.Line.Quantity,
        held.Line.ExpectedStartDate,
        held.Line.ExpectedEndDate,
        held.Line.ListingName,
        null,
        held.Line.PickupLocationSnapshot,
        held.Line.DailyRate,
        held.Line.DepositPerUnit,
        held.AllocationGroupId,
        held.AssignedAssets
            .Select(a => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), a.AssetId, a.AllocationId))
            .ToList(),
        held.Line.ListingSnapshot);
}
