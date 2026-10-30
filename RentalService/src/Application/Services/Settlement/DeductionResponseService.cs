using Dprental.RentalService.Application.Hubs;
#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.IServices.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Settlement;

/// <remarks>
/// Nhánh renter phản hồi đề xuất khấu trừ: chấp nhận hoặc tranh chấp; worker xử lý khi quá hạn phản hồi.
/// Quyết định được lưu tại Rental; tác vụ quyết toán Payment được điều phối qua receipt/executor.
/// </remarks>
public sealed partial class DeductionResponseService(
    IRentalOrderRepository orders,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    IRentalMediaClient mediaClient,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<DeductionResponseService> logger,
    IOrderHubNotifier? notifier = null) : IDeductionResponseService
{
    private const string RespondAction = "RenterRespondDeduction";

    // Renter chấp nhận hoặc tranh chấp đề xuất sau phân quyền/replay; kiểm tra bằng chứng ở nhánh dispute và lưu kết quả quyết định.
    public async Task<Result<RespondDeductionResponse>> RespondAsync(
        Guid itemId, IRentalCallerContext caller, RespondDeductionRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);
        // Kiểm tra danh tính và loại caller trước khi dùng user ID để xác định phạm vi dữ liệu được phép truy cập.
        if (caller.IsService)
            return Error.Forbidden("SERVICE_TOKEN_NOT_ALLOWED", "Service tokens cannot respond for a renter.");
        // Kiểm tra danh tính và loại caller trước khi dùng user ID để xác định phạm vi dữ liệu được phép truy cập.
        if (!caller.IsAuthenticated || !caller.UserId.HasValue || caller.UserId == Guid.Empty)
            return Error.Unauthorized("AUTH_REQUIRED", "Renter authentication is required.");
        // Từ chối key rỗng hoặc vượt giới hạn lưu trữ để mỗi thao tác có thể được nhận diện khi client gửi lại.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (request is null || !Enum.IsDefined(request.Decision))
            return Error.Validation("INVALID_RESPONSE", "Decision must be Accept or Dispute.");

        // Giữ định danh/vai trò đã xác định cho thao tác để audit và các bước tiếp theo dùng cùng chủ thể.
        Guid renterId = caller.UserId.Value;
        var orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null || item is null) return RentalErrors.ItemNotFound;
        var authorization = RentalAuthorizationPolicy.AuthorizeRenterAction(caller, order);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (authorization.IsFailure) return authorization.Error;

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string key = idempotencyKey.Trim();
        var mediaIds = (request.EvidenceMediaIds ?? []).ToList();
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (request.Decision == RenterDeductionDecision.Accept &&
            (!string.IsNullOrWhiteSpace(request.DisputeReason) || mediaIds.Count > 0))
            return Error.Validation("INVALID_RESPONSE", "Accept response cannot include dispute reason or evidence.");
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (request.Decision == RenterDeductionDecision.Dispute &&
            (string.IsNullOrWhiteSpace(request.DisputeReason) || request.DisputeReason.Trim().Length > 2000 ||
             mediaIds.Count > 10 || mediaIds.Contains(Guid.Empty) || mediaIds.Distinct().Count() != mediaIds.Count))
            return Error.Validation("INVALID_DISPUTE", "A reason (maximum 2000 characters) and at most 10 distinct evidence IDs are required.");
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            RenterId = renterId,
            request.Decision,
            Reason = request.DisputeReason?.Trim(),
            MediaIds = mediaIds.OrderBy(id => id)
        });
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        // Replay dựa vào cả action, item và nội dung, không chỉ key. Kiểm tra trước khi
        // áp dụng quyết định để lần gửi lại không chấp nhận/tranh chấp proposal lần thứ hai.
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        // Xử lý dữ liệu/receipt đã tồn tại trước; dùng kết quả cũ hoặc kiểm tra xung đột để không tạo tác vụ trùng.
        if (receipt is not null)
        {
            // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
            if (receipt.Action != RespondAction || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            // Receipt đã thành công thì trả kết quả cũ/no-op; không gửi lại một tác vụ đã được xác nhận hoàn tất.
            if (!receipt.IsSuccess || string.IsNullOrWhiteSpace(receipt.ApiResponseJson))
                return Error.Conflict("RESPONSE_IN_PROGRESS", "The response is not complete.");
            var saved = JsonSerializer.Deserialize<RespondDeductionResponse>(receipt.ApiResponseJson);
            return saved is null ? Error.Failure("INVALID_RESPONSE_RECEIPT", "Stored response is unavailable.") : saved;
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset now = timeProvider.GetUtcNow();   // sau khi có khóa

        // Accept chốt số khấu trừ; Dispute chuyển sang chờ giải quyết và có thể cần Media.
        // Cả hai nhánh chỉ ghi quyết định tại Rental, chưa xác nhận Payment đã quyết toán.
        Result applied;
        // Chọn nhánh theo quyết định gửi lên; mỗi nhánh có điều kiện bằng chứng và thay đổi trạng thái riêng.
        if (request.Decision == RenterDeductionDecision.Accept)
        {
            // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
            applied = item.AcceptDeductionByRenter(renterId, now, key);
        }
        else
        {
            // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
            if (mediaIds.Count > 0)
            {
                // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
                var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                    mediaIds, renterId, mediaClient, ct: cancellationToken);
                // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
                if (evidence.IsFailure) return evidence.Error;
            }
            applied = item.DisputeDeductionByRenter(renterId, request.DisputeReason, mediaIds, now, key);
        }
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (applied.IsFailure) return applied.Error;

        // Lấy đề xuất khấu trừ để dựng tiến độ/phản hồi; đề xuất và số khấu trừ cuối là hai mốc dữ liệu khác nhau.
        var proposal = item.DeductionProposal!;
        var response = new RespondDeductionResponse(
            proposal.Id, item.Id, proposal.Status.ToString(), item.FinalDeductionAmount, now, item.Status.ToString());

        // Tạo/lưu journal của thao tác để các lần retry và đối soát dùng cùng định danh, payload và kết quả.
        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(), order.Id, key, RespondAction, fingerprint, true, null, null, now,
            targetItemId: item.Id, payloadJson: payload, apiResponseJson: JsonSerializer.Serialize(response)));

        // Tổng hợp lại trạng thái order từ các item sau quyết định; không ép các item khác đi theo một trạng thái chung.
        RentalOrderStatusPolicy.Apply(order, now, key);
        order.RecordReadModelUpdate(now);
        // Gửi thông báo theo chính sách bắt lỗi của luồng; đây là bước sau thay đổi nghiệp vụ, không phải bằng chứng provider đã xử lý.
        try
        {
            // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
            await orders.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
            if (notifier != null)
            {
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        OrderChangeTypes.SettlementChanged,
                        now),
                    cancellationToken);

                // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (subOrder != null)
                {
                    // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                    await notifier.NotifySupplierSubOrderChangedAsync(
                        item.SupplierId,
                        new SupplierSubOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            subOrder.Id,
                            order.Version,
                            OrderChangeTypes.SettlementChanged,
                            now),
                        cancellationToken);
                }
            }
        }
        // Bỏ thay đổi tracked khi lưu thất bại; giữ cách trả conflict hoặc chuyển tiếp exception của use case.
        catch
        {
            // Bỏ thay đổi đang tracked sau lỗi để lần xử lý tiếp theo không lưu nhầm dữ liệu của nhánh đã thất bại.
            unitOfWork.DiscardChanges();
            throw;
        }
        return response;
    }

    // Quét batch đề xuất quá hạn phản hồi, cô lập lỗi từng item và không nuốt cancellation của worker.
    public async Task<int> ProcessRenterResponseTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        var itemIds = await orders.GetRenterResponseTimeoutItemIdsAsync(
            timeProvider.GetUtcNow(),
            configuration.GetValue("Workers:RenterResponseDeadline:BatchSize", 50),
            cancellationToken);

        // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
        int count = 0;
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (Guid itemId in itemIds)
        {
            // Thực hiện lời gọi có thể phát sinh exception; nhánh catch phía dưới quyết định trả lỗi, retry hoặc chuyển tiếp exception.
            try
            {
                // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
                var result = await ExpireSingleRenterResponseAsync(itemId, cancellationToken);
                // Xử lý kết quả thành công ở nhánh này; nhánh còn lại giữ cách trả lỗi hoặc đếm thất bại của caller.
                if (result.IsSuccess) count++;
            }
            // Xử lý exception theo chính sách của nhánh này: chuyển thành lỗi/ghi nhận retry hoặc ném lại; không tự suy ra provider chưa thực thi.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
                logger.LogWarning(ex, "Renter response timeout lỗi cho item {ItemId}", itemId);
            }
        }
        return count;
    }

    // Đọc lại item dưới khóa và chốt theo quy tắc hết hạn phản hồi; lưu version/state cùng kết quả trước thông báo.
    public async Task<Result> ExpireSingleRenterResponseAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var orderId = await orders.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!orderId.HasValue) return Result.Success();

        // Lấy khóa đúng phạm vi trước khi đọc lại và thay đổi dữ liệu, tránh quyết định trên snapshot đã cũ.
        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        var order = await orders.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        var item = order?.Items.FirstOrDefault(i => i.Id == itemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order is null || item is null) return Result.Success();

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset now = timeProvider.GetUtcNow();   // sau khi có khóa
        string opKey = RentalOperationExecutor.ComputeOperationKey("RenterResponseTimeout", $"{order.Id}:{item.Id}");

        // Chỉ chốt timeout khi entity vẫn đủ điều kiện tại thời điểm xử lý; nếu trạng thái đã đổi thì bỏ qua lượt quét cũ.
        if (!item.TryAutoAcceptDeduction(now, opKey)) return Result.Success();

        // Tổng hợp lại trạng thái order từ các item sau quyết định; không ép các item khác đi theo một trạng thái chung.
        RentalOrderStatusPolicy.Apply(order, now, opKey);
        order.RecordReadModelUpdate(now);
        // Gửi thông báo theo chính sách bắt lỗi của luồng; đây là bước sau thay đổi nghiệp vụ, không phải bằng chứng provider đã xử lý.
        try
        {
            // Đăng ký thay đổi aggregate với repository trước khi lưu; không truy cập DbContext trực tiếp từ service.
            await orders.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            // Chỉ gửi thông báo realtime khi service có notifier được cấu hình; không tạo một đường lưu nghiệp vụ khác.
            if (notifier != null)
            {
                // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                await notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        OrderChangeTypes.SettlementChanged,
                        now),
                    cancellationToken);

                // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (subOrder != null)
                {
                    // Gửi tín hiệu thay đổi tới các bên liên quan bằng ID/version, để client tải lại dữ liệu đã lưu.
                    await notifier.NotifySupplierSubOrderChangedAsync(
                        item.SupplierId,
                        new SupplierSubOrderChangedNotification(
                            Guid.NewGuid(),
                            order.Id,
                            subOrder.Id,
                            order.Version,
                            OrderChangeTypes.SettlementChanged,
                            now),
                        cancellationToken);
                }
            }
        }
        // Bỏ thay đổi tracked khi lưu thất bại; giữ cách trả conflict hoặc chuyển tiếp exception của use case.
        catch
        {
            // Bỏ thay đổi đang tracked sau lỗi để lần xử lý tiếp theo không lưu nhầm dữ liệu của nhánh đã thất bại.
            unitOfWork.DiscardChanges();
            throw;
        }

        // Ghi metadata của bước xử lý để chẩn đoán; log không thay cho receipt/history đã lưu.
        logger.LogInformation("Renter response deadline expired for item {ItemId}; proposal auto-accepted", itemId);
        return Result.Success();
    }
}
