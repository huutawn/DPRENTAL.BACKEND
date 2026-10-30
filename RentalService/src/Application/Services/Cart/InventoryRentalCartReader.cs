using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Common.Utils;

namespace Dprental.RentalService.Application.Services.Cart;

/// <summary>
/// Chuyển đổi contract của InventoryService sang nhu cầu của Cart.
/// Chuẩn hóa toàn bộ lỗi dependency và bảo vệ dữ liệu response.
/// </summary>
public sealed class InventoryRentalCartReader(IInventoryRentalClient inventory) :
    IRentalListingReader,
    IRentalAvailabilityReader
{
    // Lấy snapshot listing từ Inventory; kiểm tra phản hồi đủ và duy nhất trước khi dùng giá, cọc và trạng thái cho giỏ.
    public async Task<Result<RentalListingSnapshot>> GetListingAsync(
        Guid listingId,
        CancellationToken cancellationToken = default)
    {
        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        Result<ListingRentalSnapshotsResponse> result = await inventory.GetListingRentalSnapshotsAsync(
            new ListingRentalSnapshotsRequest([listingId]), cancellationToken);

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (result.IsFailure)
        {
            return NormalizeDependencyError(result.Error);
        }

        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (result.Value is null || result.Value.Items is null || result.Value.MissingIds is null)
        {
            return InvalidInventoryContract();
        }

        // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
        ListingRentalSnapshotDto[] matches = result.Value.Items.Where(x => x is not null && x.Id == listingId).Take(2).ToArray();
        // Kiểm tra thành viên trong tập liên quan để không áp dụng yêu cầu lên dữ liệu ngoài phạm vi đang xử lý.
        if (matches.Length == 0 && result.Value.MissingIds.Contains(listingId))
        {
            return ListingNotFound();
        }

        // Kiểm tra thành viên trong tập liên quan để không áp dụng yêu cầu lên dữ liệu ngoài phạm vi đang xử lý.
        if (matches.Length != 1 || result.Value.MissingIds.Contains(listingId))
        {
            return InvalidInventoryContract();
        }

        // Sử dụng dữ liệu của kết quả đã kiểm tra ở bước trước để dựng quyết định tiếp theo, không gọi lại để thay snapshot giữa luồng.
        ListingRentalSnapshotDto snapshot = matches[0];

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (snapshot.DepositPerUnit is not { } deposit ||
            VndAmountValidator.ValidateVndAmount(deposit).IsFailure ||
            VndAmountValidator.ValidateVndAmount(snapshot.DailyRate).IsFailure ||
            !string.Equals(snapshot.Currency, CheckoutRules.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return InvalidInventoryContract();
        }

        // Chuyển trạng thái listing Active của Inventory sang cờ khả dụng cho bước đánh giá giỏ.
        bool active = string.Equals(snapshot.Status, "Active", StringComparison.OrdinalIgnoreCase);

        return Result.Success(new RentalListingSnapshot(
            snapshot.Id,
            snapshot.OwnerUserId,
            snapshot.EquipmentTypeId,
            active,
            active,
            snapshot.DailyRate,
            deposit,
            $"{snapshot.DailyRate.ToString(CultureInfo.InvariantCulture)}:{deposit.ToString(CultureInfo.InvariantCulture)}"));
    }

    // Kiểm tra số lượng khả dụng cho toàn khoảng thuê; đối chiếu listing/ngày/số lượng để không dùng nhầm phản hồi.
    public async Task<Result<RentalAvailabilityResult>> GetAvailableQuantityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        int requestedQuantity,
        CancellationToken cancellationToken = default)
    {
        // Dừng nhánh không hợp lệ với mã INVALID_DATES; caller nhận lỗi theo contract thay vì tiếp tục thay đổi dữ liệu.
        if (startDate > endDate)
        {
            return Error.Validation("INVALID_DATES", "Ngày bắt đầu không được lớn hơn ngày kết thúc.");
        }

        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (requestedQuantity <= 0)
        {
            return Error.Validation("INVALID_QUANTITY", "Số lượng yêu cầu phải lớn hơn 0.");
        }

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        Result<ListingAvailabilityCheckResponse> result = await inventory.CheckListingAvailabilityAsync(
            new ListingAvailabilityCheckRequest(listingId, startDate, endDate, requestedQuantity),
            cancellationToken);

        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (result.IsFailure)
        {
            return NormalizeDependencyError(result.Error);
        }

        // Sử dụng dữ liệu của kết quả đã kiểm tra ở bước trước để dựng quyết định tiếp theo, không gọi lại để thay snapshot giữa luồng.
        ListingAvailabilityCheckResponse? res = result.Value;
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (res is null ||
            res.ListingId != listingId ||
            res.StartDate != startDate ||
            res.EndDate != endDate ||
            res.RequestedQuantity != requestedQuantity ||
            res.AvailableQuantity < 0)
        {
            return InvalidInventoryContract();
        }

        return Result.Success(new RentalAvailabilityResult(res.AvailableQuantity, res.IsAvailable));
    }

    /// <summary>
    /// Chuẩn hóa lỗi từ Inventory client thành lỗi dependency an toàn, không làm lộ thông tin nội bộ.
    /// </summary>
    private static Error NormalizeDependencyError(Error upstreamError)
    {
        // Điều kiện này quyết định có thể tiếp tục hay phải trả ListingNotFound theo contract của use case.
        if (upstreamError.Type == ErrorType.NotFound)
        {
            return ListingNotFound();
        }

        return InventoryUnavailable();
    }

    // Tạo lỗi listing không tồn tại thống nhất cho các nhánh đọc snapshot và availability.
    private static Error ListingNotFound() =>
        Error.NotFound("LISTING_NOT_FOUND", "Sản phẩm không tồn tại hoặc đã bị xóa.");

    // Tạo lỗi phụ thuộc tạm thời mà không lộ chi tiết phản hồi nội bộ của Inventory.
    private static Error InventoryUnavailable() =>
        Error.ServiceUnavailable("INVENTORY_UNAVAILABLE", "Không thể kiểm tra thiết bị lúc này. Vui lòng thử lại.");

    // Từ chối phản hồi không đúng contract thay vì coi dữ liệu không xác minh được là khả dụng.
    private static Error InvalidInventoryContract() =>
        Error.ServiceUnavailable("INVENTORY_CONTRACT_INVALID", "Không thể xác minh dữ liệu thiết bị lúc này.");
}
