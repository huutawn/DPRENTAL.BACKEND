using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Policies;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Phần dùng chung của API xem đơn: kiểm tra người gọi, chuẩn hóa phân trang và đổi entity sang response.
/// </summary>
internal static class OrderQueryMapper
{
    public const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    // Cùng cách checkout ghi tài khoản hoàn tiền vào RefundDestinationRef.
    private static readonly JsonSerializerOptions RefundAccountJsonOptions = new(JsonSerializerDefaults.Web);

    // Yêu cầu danh tính người dùng cá nhân; từ chối token service trước khi xác định chủ thể của truy vấn đơn.
    public static Result<Guid> RequirePersonalCaller(IRentalCallerContext caller)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(caller);

        // Kiểm tra danh tính và loại caller trước khi dùng user ID để xác định phạm vi dữ liệu được phép truy cập.
        if (!caller.IsAuthenticated)
        {
            return OrderQueryErrors.Unauthenticated;
        }

        // Kiểm tra danh tính và loại caller trước khi dùng user ID để xác định phạm vi dữ liệu được phép truy cập.
        if (caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return OrderQueryErrors.PersonalAccountRequired;
        }

        return caller.UserId.Value;
    }

    /// <summary>page nhỏ hơn 1 được đổi thành 1; pageSize được giới hạn trong 1–100, giống API lịch thuê.</summary>
    public static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

    /// <summary>
    /// Đọc trạng thái lọc theo đúng dạng API trả ra (PENDING_PAYMENT), chấp nhận cả PendingPayment.
    /// Rỗng là không lọc; số hoặc tên lạ là lỗi.
    /// </summary>
    public static Result<TEnum?> ParseStatusFilter<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Success<TEnum?>(null);
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string name = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        // Từ chối dữ liệu không parse được theo định dạng yêu cầu, không tự sửa thành một giá trị hợp lệ giả định.
        if (!name.All(char.IsAsciiLetter) || !Enum.TryParse(name, ignoreCase: true, out TEnum status))
        {
            return OrderQueryErrors.InvalidStatus;
        }

        return Result.Success<TEnum?>(status);
    }

    // Ghép dữ liệu trang và tổng số bản ghi để client hiển thị phân trang nhất quán.
    public static PagedResponse<T> ToPage<T>(IReadOnlyList<T> items, int page, int pageSize, int totalCount) =>
        new(items, page, pageSize, totalCount, totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / pageSize));

    // Trả snapshot giao nhận đã lưu trên order, không lấy lại địa chỉ hiện tại trong sổ renter.
    public static OrderDeliveryResponse ToDelivery(RentalOrder order) => new(
        order.DeliveryMethod,
        order.RecipientName,
        order.RecipientPhone,
        order.DeliveryAddress,
        order.DeliveryNote);

    // Dựng phần thông tin chung của item và cách hiển thị cho renter/supplier từ snapshot order.
    public static OrderItemResponse ToItem(RentalItem item, bool isSupplier = false)
    {
        // Đơn lưu ngày trả máy (không tính); trả ra ngày dùng cuối (có tính) như giỏ hàng.
        Result<(DateOnly StartDate, DateOnly EndDate)> dates =
            RentalInventoryDateAdapter.ToInventoryDates(item.ExpectedStartDate, item.ExpectedEndDate);
        DateOnly endDate = dates.IsSuccess ? dates.Value.EndDate : item.ExpectedEndDate;

        // Khách chỉ được xem địa chỉ thật sau khi chủ xác nhận đơn (Status >= AwaitingHandover).
        // Nhà cung cấp xem được địa chỉ bất kỳ lúc nào.
        bool canSeeRealAddress = isSupplier || item.Status is Dprental.RentalService.Common.Enums.RentalItemStatus.AwaitingHandover
            or Dprental.RentalService.Common.Enums.RentalItemStatus.InRental
            or Dprental.RentalService.Common.Enums.RentalItemStatus.PendingSettlement
            or Dprental.RentalService.Common.Enums.RentalItemStatus.Completed;

        // Chỉ đưa điểm nhận máy thật vào response khi điều kiện hiển thị cho vai trò/trạng thái hiện tại cho phép.
        string? pickupLocation = canSeeRealAddress ? item.PickupLocationSnapshot : null;

        return new OrderItemResponse(
            item.ListingTitleSnapshot,
            item.Quantity,
            item.ExpectedStartDate,
            endDate,
            pickupLocation);
    }

    /// <summary>Các dòng theo thứ tự ngày giao rồi mã dòng, để kết quả ổn định giữa các lần gọi.</summary>
    public static List<OrderItemResponse> ToItems(IEnumerable<RentalItem> items, bool isSupplier = false) => items
        .OrderBy(i => i.ExpectedStartDate)
        .ThenBy(i => i.Id)
        .Select(i => ToItem(i, isSupplier))
        .ToList();

    // Chiếu projection danh sách thành thông tin tóm tắt item, không tải lại aggregate chỉ để dựng response.
    public static OrderSummaryItemResponse ToSummaryItem(OrderItemSummaryRecord item)
    {
        // Đổi khoảng ngày projection qua adapter dùng chung trước khi hiển thị dòng tóm tắt; không sửa dữ liệu ngày đã lưu.
        Result<(DateOnly StartDate, DateOnly EndDate)> dates =
            RentalInventoryDateAdapter.ToInventoryDates(item.StartDate, item.EndDate);
        DateOnly endDate = dates.IsSuccess ? dates.Value.EndDate : item.EndDate;
        int rentalDays = RentalPricingPolicy.CalculateRentalDays(item.StartDate, item.EndDate);

        return new OrderSummaryItemResponse(
            item.RentalItemId,
            item.EquipmentListingId,
            item.SupplierId,
            item.Name,
            item.ThumbnailMediaId,
            item.Quantity,
            rentalDays,
            item.DailyRate,
            item.RentalAmount,
            item.DepositAmount,
            item.StartDate,
            endDate,
            item.Status);
    }

    // Trả biên bản bàn giao nếu đã được chốt; chưa có record thì không suy diễn từ intent còn Pending.
    public static OrderHandoverResponse? ToHandover(RentalItem item) => item.HandoverRecord is { } handover
        ? new OrderHandoverResponse(
            handover.ActualHandoverAt,
            handover.Note,
            handover.RenterAcknowledgedAt,
            handover.RenterAcknowledgedById,
            handover.RenterAcknowledgementNote)
        : null;

    /// <summary>Mỗi lần trả kèm số máy bình thường và hư hỏng.</summary>
    public static List<OrderReturnResponse> ToReturns(RentalItem item) => item.ReturnRecords
        .OrderBy(r => r.ActualReturnAt)
        .ThenBy(r => r.Id)
        .Select(r =>
        {
            // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
            List<RentalUnitAssignment> units = item.UnitAssignments.Where(u => u.ReturnRecordId == r.Id).ToList();
            return new OrderReturnResponse(
                r.ActualReturnAt,
                r.Note,
                units.Count(u => u.ReturnCondition == UnitCondition.Normal),
                units.Count(u => u.ReturnCondition == UnitCondition.Damaged));
        })
        .ToList();
    // Trả trạng thái unit phục vụ renter theo các trường DTO cho phép, không đưa toàn bộ entity nội bộ ra ngoài.
    public static IReadOnlyList<RenterUnitResponse> ToRenterUnits(RentalItem item)
    {
        // Lập tập unit đang có yêu cầu trả mở để response phân biệt khả năng gửi yêu cầu trả mới.
        var openUnits = ReturnEligibilityPolicy.GetOpenReturnRequestAssignmentIds(item);
        return item.UnitAssignments
            .OrderBy(u => u.Id)
            .Select(u => new RenterUnitResponse(
                u.Id,
                u.Status,
                openUnits.Contains(u.Id),
                ReturnEligibilityPolicy.CanRequestReturn(item, u, openUnits)))
            .ToList();
    }

    // Tổng hợp từng yêu cầu trả, unit, issue và resolution để UI phân biệt đã xử lý với phần còn chờ.
    public static IReadOnlyList<ReturnRequestResponse> ToReturnRequests(RentalItem item, DateTimeOffset? now = null, bool includeAdminNotes = false)
    {
        // Dùng mốc caller cung cấp khi có để toàn bộ trạng thái quá hạn trong response được tính nhất quán.
        var current = now ?? DateTimeOffset.UtcNow;
        return item.ReturnRequests
            .OrderBy(r => r.SubmittedAt)
            .ThenBy(r => r.Id)
            .Select(r =>
            {
                // Tính trạng thái từ từng unit và issue hiện có qua policy chung, không dùng một cờ chung che phần đã xử lý.
                var disputedAssignmentIds = ReturnRequestStatusPolicy.GetDisputedAssignmentIds(r);
                var unitResponses = r.Units
                    .OrderBy(u => u.RentalUnitAssignmentId)
                    .Select(u =>
                    {
                        // Tính trạng thái từ từng unit và issue hiện có qua policy chung, không dùng một cờ chung che phần đã xử lý.
                        var uStatus = ReturnRequestStatusPolicy.GetUnitStatus(u, disputedAssignmentIds);
                        var isUnitOverdue = ReturnRequestStatusPolicy.IsUnitOverdue(u, r.ConfirmationDeadlineAt, current);
                        return new ReturnRequestUnitResponse(
                            u.RentalUnitAssignmentId,
                            u.MediaIds,
                            u.ClosedAt,
                            u.RejectedAt,
                            uStatus,
                            isUnitOverdue,
                            u.OverdueNotifiedAt);
                    })
                    .ToList();

                // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
                var reqStatus = ReturnRequestStatusPolicy.GetRequestStatus(unitResponses.Select(u => u.Status).ToList());
                var isReqOverdue = ReturnRequestStatusPolicy.IsRequestOverdue(r, current);

                // Tích lũy lỗi theo trường/dòng để trả đủ thông tin sửa yêu cầu thay vì che lỗi thành dữ liệu hợp lệ.
                var issues = r.Issues
                    .OrderBy(i => i.ReportedAt)
                    .Select(i => new ReturnRequestIssueResponse(
                        i.Id,
                        i.ReturnRequestId,
                        i.ReporterId,
                        i.ReporterRole,
                        i.Reason,
                        i.Note,
                        i.MediaIds,
                        i.ReportedAt,
                        i.Units.Select(u => u.RentalUnitAssignmentId).ToList()))
                    .ToList();

                // Ghép resolution với các unit được giải quyết để response giữ được lịch sử xử lý từng phần của yêu cầu trả.
                var resolutions = r.Resolutions
                    .OrderBy(res => res.ResolvedAt)
                    .Select(res => new ReturnRequestResolutionResponse(
                        res.Id,
                        res.ReturnRequestId,
                        res.AdminId,
                        res.Decision,
                        includeAdminNotes ? res.DecisionNote : null,
                        res.ResolutionMediaIds,
                        res.ResolvedAt,
                        res.Units.Select(u => u.RentalUnitAssignmentId).ToList()))
                    .ToList();

                return new ReturnRequestResponse(
                    r.Id,
                    r.RentalItemId,
                    r.ActualReturnAt,
                    r.Note,
                    r.SubmittedAt,
                    unitResponses,
                    reqStatus,
                    r.ConfirmationDeadlineAt,
                    isReqOverdue,
                    issues,
                    resolutions);
            })
            .ToList();
    }


    /// <summary>Rỗng khi chưa trả đủ máy và chưa có đề xuất khấu trừ.</summary>
    public static OrderSettlementResponse? ToSettlement(RentalItem item)
    {
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!item.ReturnsCompletedAt.HasValue && item.DeductionProposal is null)
        {
            return null;
        }

        // Lấy đề xuất khấu trừ để dựng tiến độ/phản hồi; đề xuất và số khấu trừ cuối là hai mốc dữ liệu khác nhau.
        DeductionProposal? proposal = item.DeductionProposal;
        return new OrderSettlementResponse(
            item.ReturnsCompletedAt,
            item.DeductionDeadline,
            proposal?.Status,
            proposal?.TotalProposedAmount,
            proposal?.RenterResponseDeadline,
            item.FinalDeductionAmount,
            item.FinalDeductionAmount is { } deduction ? item.DepositAmount - deduction : null,
            item.FinancialClosureConfirmedAt);
    }

    /// <summary>Khoản hoàn của từng dòng trong đơn đã nạp, theo <see cref="RentalRefundPolicy"/>.</summary>
    public static Dictionary<Guid, RentalRefundLine> RefundLinesByItem(RentalOrder order) =>
        RentalRefundPolicy.LinesOf(order, order.SubOrders.SelectMany(s => s.Items).Concat(order.Items))
            .ToDictionary(l => l.Item.Id);

    /// <summary>Có xác nhận chuyển khoản của Admin là đã hoàn; chỉ có khoản hoàn là đang chờ hoàn.</summary>
    public static OrderItemRefundResponse ToRefund(RentalItem item, RentalRefundLine? line) => line is null
        ? new OrderItemRefundResponse(OrderRefundState.None, item.RefundRef)
        : new OrderItemRefundResponse(
            line.Confirmation is null ? OrderRefundState.Processing : OrderRefundState.Completed,
            item.RefundRef,
            line.Kind,
            line.RefundAmount,
            line.Confirmation?.ConfirmedAt);

    /// <summary>Không có khoản hoàn là None; còn khoản chưa chuyển khoản là Pending; mọi khoản đã chuyển là Refunded.</summary>
    public static OrderRefundProgress ToRefundProgress(int refundCount, int pendingRefundCount) =>
        refundCount == 0 ? OrderRefundProgress.None
        : pendingRefundCount > 0 ? OrderRefundProgress.Pending
        : OrderRefundProgress.Refunded;

    /// <summary>Trả trễ cũng có PaymentConfirmedAt nên phải xét cờ trả trễ trước.</summary>
    public static OrderPaymentState ToPaymentState(RentalOrder order)
    {
        // Đối chiếu tham chiếu thanh toán để tách khoản tiền đã ghi nhận khỏi một thanh toán khác hoặc sự kiện lặp.
        if (order.LatePaymentRefundRequired)
        {
            return OrderPaymentState.LatePaymentRefund;
        }

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (order.PaymentConfirmedAt.HasValue)
        {
            return OrderPaymentState.Paid;
        }

        return order.Status == RentalOrderStatus.PendingPayment
            ? OrderPaymentState.Pending
            : OrderPaymentState.Expired;
    }

    /// <summary>
    /// Checkout lưu tài khoản hoàn tiền dạng JSON trong RefundDestinationRef. Đơn cũ lưu ORIGINAL_PAYMENT_SOURCE
    /// (hoàn về nguồn thanh toán) nên không có tài khoản; JSON hỏng hoặc thiếu ô cũng trả rỗng.
    /// </summary>
    public static RenterRefundAccountResponse? ParseRefundAccount(string refundDestinationRef)
    {
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(refundDestinationRef) || !refundDestinationRef.TrimStart().StartsWith('{'))
        {
            return null;
        }

        // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
        try
        {
            // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
            CheckoutRefundAccountDto? account =
                JsonSerializer.Deserialize<CheckoutRefundAccountDto>(refundDestinationRef, RefundAccountJsonOptions);
            // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
            if (account is null ||
                string.IsNullOrWhiteSpace(account.BankCode) ||
                string.IsNullOrWhiteSpace(account.AccountNumber) ||
                string.IsNullOrWhiteSpace(account.AccountHolderName))
            {
                return null;
            }

            return new RenterRefundAccountResponse(account.BankCode, account.AccountNumber, account.AccountHolderName);
        }
        // Dữ liệu JSON không đọc được: trả lỗi hoặc đi nhánh fallback đã định nghĩa, không dùng một snapshot dựng từ dữ liệu hỏng.
        catch (JsonException)
        {
            return null;
        }
    }
}
