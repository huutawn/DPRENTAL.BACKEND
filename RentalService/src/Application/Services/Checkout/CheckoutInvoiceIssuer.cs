using System.Text.Json;
#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Checkout;

/// <remarks>
/// Đảm bảo hóa đơn của order bằng HTTP Payment với operation key ổn định theo order.
/// Kiểm tra số tiền, tiền tệ và deadline của phản hồi trước khi lưu InvoiceRef/receipt; không kéo dài hạn thanh toán.
/// Caller phải kết thúc transaction tạo order trước cuộc gọi mạng; dữ liệu hóa đơn được lưu sau khi Payment trả về.
/// </remarks>
public sealed class CheckoutInvoiceIssuer : ICheckoutInvoiceIssuer
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    private readonly IPaymentRentalClient _payment;
    private readonly IRentalOrderRepository _orders;
    private readonly IRentalCheckoutOperationRepository _checkoutOperations;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CheckoutInvoiceIssuer> _logger;
    private readonly IOrderHubNotifier? _notifier;
    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public CheckoutInvoiceIssuer(
        IPaymentRentalClient payment,
        IRentalOrderRepository orders,
        IRentalCheckoutOperationRepository checkoutOperations,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<CheckoutInvoiceIssuer> logger,
        IOrderHubNotifier? notifier = null)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _payment = payment ?? throw new ArgumentNullException(nameof(payment));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _checkoutOperations = checkoutOperations ?? throw new ArgumentNullException(nameof(checkoutOperations));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notifier = notifier;
    }

    // Tận dụng response đã lưu nếu còn hợp lệ; nếu cần thì gọi Payment cùng operation key.
    // Chỉ retry ngắn với lỗi tạm thời và luôn kiểm tra lại deadline trước khi tiếp tục.
    public async Task<Result<CreateInvoiceResponse>> EnsureInvoiceAsync(RentalOrder order, CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(order);

        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (order.Status != RentalOrderStatus.PendingPayment || _timeProvider.GetUtcNow() >= order.PaymentDeadline)
        {
            return CheckoutErrors.OrderExpired;
        }

        // Chỉ dùng response đã lưu khi vẫn khớp order và đủ dữ liệu thanh toán.
        // Có InvoiceRef nhưng thiếu QR vẫn cần hỏi lại Payment bằng cùng operation key.
        if (!string.IsNullOrWhiteSpace(order.InvoiceRef))
        {
            // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
            string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
            var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey);
            string? cachedJson = receipt?.ApiResponseJson ?? receipt?.ResponseJson;
            // Chỉ đọc payload/response journal khi có dữ liệu; không dựng lại nội dung provider từ giả định khi cần đối soát.
            if (!string.IsNullOrWhiteSpace(cachedJson))
            {
                // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
                try
                {
                    // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
                    var cached = JsonSerializer.Deserialize<CreateInvoiceResponse>(cachedJson);
                    // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                    if (cached != null && MatchesOrder(cached, order) && IsReady(cached, order))
                    {
                        return cached;
                    }
                }
                // Dữ liệu JSON không đọc được: trả lỗi hoặc đi nhánh fallback đã định nghĩa, không dùng một snapshot dựng từ dữ liệu hỏng.
                catch (JsonException)
                {
                    // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                    _logger.LogWarning("Receipt hóa đơn của đơn {OrderId} không đọc được; gọi lại Payment cùng operation key.", order.Id);
                }
            }
        }
        // OperationKey tính từ mã đơn nên gọi lại bao nhiêu lần Payment cũng chỉ tạo một hóa đơn.
        CreateInvoiceRequest request = new(
            order.Id,
            order.OrderCode,
            order.RenterId,
            order.Currency,
            order.PaymentDeadline,
            order.Items
                .Select(i => new CreateInvoiceItemRequest(i.Id, i.SupplierId, i.RentalAmount, i.DepositAmount))
                .ToList(),
            RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString()));

        // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
        Result validation = request.Validate();
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (validation.IsFailure)
        {
            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogError(
                "Đơn {OrderCode} không dựng được yêu cầu tạo hóa đơn hợp lệ: {ErrorCode}.", order.OrderCode, validation.Error.Code);
            return CheckoutErrors.InvoiceRejected;
        }

        // Gọi Payment với request/idempotency key đã dựng; HTTP thành công còn phải qua kiểm tra nội dung phản hồi.
        Result<CreateInvoiceResponse> response = await _payment.CreateInvoiceAsync(request, cancellationToken);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (response.IsFailure && IsTemporary(response.Error))
        {
            // Chờ một khoảng retry ngắn có cancellation; không mở vòng lặp gọi Payment vô hạn trong request.
            await Task.Delay(RetryDelay, cancellationToken);
            // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
            if (_timeProvider.GetUtcNow() >= order.PaymentDeadline)
            {
                return CheckoutErrors.OrderExpired;
            }
            response = await _payment.CreateInvoiceAsync(request, cancellationToken);
        }

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (response.IsFailure)
        {
            // Lỗi tạm thời giữ cơ hội retry/tiếp tục bằng journal cũ; không chốt thất bại nghiệp vụ từ lỗi mạng.
            if (IsTemporary(response.Error))
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogWarning(
                    "Chưa tạo được hóa đơn cho đơn {OrderCode}: {ErrorCode}. Đơn được giữ để tạo lại.", order.OrderCode, response.Error.Code);
                return CheckoutErrors.InvoiceTemporarilyUnavailable;
            }

            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogError("Payment từ chối tạo hóa đơn cho đơn {OrderCode}: {ErrorCode}.", order.OrderCode, response.Error.Code);
            return CheckoutErrors.InvoiceRejected;
        }

        // Không tin phản hồi chỉ vì HTTP 2xx: invoice phải khớp snapshot tiền và deadline.
        // Kiểm tra lại thời gian sau cuộc gọi mạng để không mở thanh toán cho đơn vừa hết hạn.
        CreateInvoiceResponse invoice = response.Value;
        // Điều kiện này quyết định có thể tiếp tục hay phải trả CheckoutErrors.InvoiceRejected theo contract của use case.
        if (!MatchesOrder(invoice, order))
        {
            // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
            _logger.LogError(
                "Hóa đơn Payment trả về cho đơn {OrderId} ({OrderCode}) không hợp lệ hoặc lệch số tiền/loại tiền/deadline. OperationKey: {OperationKey}, Error: {ErrorCode}.",
                order.Id, order.OrderCode, request.OperationKey, CheckoutErrors.InvoiceRejected.Code);
            return CheckoutErrors.InvoiceRejected;
        }
        DateTimeOffset completedAt = _timeProvider.GetUtcNow();
        // Đối chiếu mốc hết hạn của luồng hiện tại; nhánh quá hạn không được tiếp tục như một yêu cầu mới còn hiệu lực.
        if (completedAt >= order.PaymentDeadline)
        {
            return CheckoutErrors.OrderExpired;
        }
        bool attachedNewInvoice = false;
        // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
        if (string.IsNullOrWhiteSpace(order.InvoiceRef))
        {
            // Gắn invoice vào order sau khi phản hồi đã được đối chiếu; không thay invoice cũ bằng hóa đơn khác.
            Result attached = order.AttachInvoice(invoice.InvoiceRef, completedAt);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (attached.IsFailure)
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                _logger.LogError("Không gắn được hóa đơn vào đơn {OrderCode}: {ErrorCode}.", order.OrderCode, attached.Error.Code);
                return CheckoutErrors.InvoiceRejected;
            }
            attachedNewInvoice = true;
        }

        // Payment có thể đã tạo invoice dù lưu local thất bại. Retry phải dùng cùng key,
        // không tạo invoice mới để bù cho lỗi ghi database tại Rental.
        var localResult = await CompleteInvoiceLocalAsync(order, completedAt, invoice, cancellationToken);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (localResult.IsFailure)
        {
            return localResult.Error;
        }

        // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
        if (attachedNewInvoice && _notifier != null)
        {
            // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
            await _notifier.NotifyRenterOrderChangedAsync(
                order.RenterId,
                new RenterOrderChangedNotification(
                    Guid.NewGuid(),
                    order.Id,
                    order.Version,
                    OrderChangeTypes.InvoiceAttached,
                    completedAt),
                cancellationToken);
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (!IsReady(invoice, order) &&
            !string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(invoice.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(invoice.Status, "Expired", StringComparison.OrdinalIgnoreCase))
        {
            return CheckoutErrors.InvoiceTemporarilyUnavailable;
        }

        return invoice;
    }

    // Lưu ACK hóa đơn, trạng thái checkout và receipt CreateInvoice; đồng bộ dữ liệu local sau khi Payment trả kết quả.
    private async Task<Result> CompleteInvoiceLocalAsync(
        RentalOrder order,
        DateTimeOffset completedAt,
        CreateInvoiceResponse invoiceResponse,
        CancellationToken cancellationToken)
    {
        // Tạo định danh ổn định của thao tác để các lần gọi lại không sinh một side effect mới.
        string opKey = RentalOperationExecutor.ComputeOperationKey("CreateInvoice", order.Id.ToString());
        string responseJson = JsonSerializer.Serialize(invoiceResponse);
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (receipt != null)
        {
            // Ghi kết quả thành công của receipt; ý nghĩa hoàn tất nghiệp vụ còn phụ thuộc action và event xác nhận riêng.
            receipt.MarkSucceeded(completedAt, responseJson, responseJson);
        }
        else
        {
            // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
            order.AddOperationReceipt(new RentalOperationReceipt(
                Guid.NewGuid(), order.Id, opKey, "CreateInvoice", opKey,
                isSuccess: true, errorCode: null, errorMessage: null,
                completedAt, RentalOperationReceipt.StatusSucceeded,
                completedAt: completedAt,
                responseJson: responseJson,
                apiResponseJson: responseJson));
        }

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        var op = await _checkoutOperations.GetByExpectedOrderIdAsync(order.Id, cancellationToken);
        string invoiceStatus = IsReady(invoiceResponse, order) ? "Ready" : "Pending";
        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (op != null && op.InvoiceStatus != invoiceStatus)
        {
            // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
            op.SetInvoiceStatus(invoiceStatus, completedAt);
            await _checkoutOperations.UpdateAsync(op, cancellationToken);
        }

        // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
        await _orders.UpdateAsync(order, cancellationToken);
        // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
        try
        {
            // Lưu thay đổi đã đăng ký trong UnitOfWork; transaction đang bao quanh, nếu có, vẫn do caller sở hữu.
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        // Bỏ thay đổi tracked khi lưu thất bại; giữ cách trả conflict hoặc chuyển tiếp exception của use case.
        catch (DbUpdateConcurrencyException)
        {
            // Bỏ thay đổi đang tracked sau lỗi để lần xử lý tiếp theo không lưu nhầm dữ liệu của nhánh đã thất bại.
            _unitOfWork.DiscardChanges();
            return CheckoutErrors.InvoicePersistenceConflict;
        }

        return Result.Success();
    }

    // Đối chiếu định danh, tổng tiền, tiền tệ và deadline hóa đơn với snapshot của order.
    private static bool MatchesOrder(CreateInvoiceResponse invoice, RentalOrder order) =>
        !string.IsNullOrWhiteSpace(invoice.InvoiceRef) &&
        (string.IsNullOrWhiteSpace(order.InvoiceRef) || string.Equals(invoice.InvoiceRef, order.InvoiceRef, StringComparison.Ordinal)) &&
        invoice.TotalAmount == order.TotalPayableAmount &&
        string.Equals(invoice.Currency, order.Currency, StringComparison.OrdinalIgnoreCase) &&
        invoice.PaymentDeadline.UtcTicks / TimeSpan.TicksPerMicrosecond ==
        order.PaymentDeadline.UtcTicks / TimeSpan.TicksPerMicrosecond;

    // Kiểm tra hóa đơn có đủ thông tin phục vụ thanh toán và vẫn phù hợp với trạng thái/hạn của order.
    private static bool IsReady(CreateInvoiceResponse invoice, RentalOrder order) =>
        order.TotalPayableAmount == 0
            ? string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase)
            : string.Equals(invoice.Status, "Pending", StringComparison.OrdinalIgnoreCase) &&
              Uri.TryCreate(invoice.QrUrl, UriKind.Absolute, out var qrUri) &&
              qrUri.Scheme == Uri.UriSchemeHttps &&
              !string.IsNullOrWhiteSpace(invoice.Bank) &&
              !string.IsNullOrWhiteSpace(invoice.Account) &&
              !string.IsNullOrWhiteSpace(invoice.TransferContent);

    /// <summary>Không kết nối được hoặc Payment lỗi phía máy chủ: gọi lại có thể thành công.</summary>
    private static bool IsTemporary(Error error) =>
        error.Type is ErrorType.ServiceUnavailable or ErrorType.Failure;
}
