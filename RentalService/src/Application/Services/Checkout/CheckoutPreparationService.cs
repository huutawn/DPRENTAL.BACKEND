using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Policies;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Services.Checkout;

/// <remarks>
/// Bước chuẩn bị trước khi giữ lịch: kiểm tra thông tin renter, điều khoản, địa chỉ và tài khoản hoàn tiền;
/// đọc lại listing/giá và kiểm tra khả dụng để dựng CheckoutPlan. Thành công ở đây chưa tạo order hoặc giữ máy.
/// </remarks>
public sealed class CheckoutPreparationService : ICheckoutPreparationService
{
    private readonly IRentalCartRepository _carts;
    private readonly IRenterAddressRepository _addresses;
    private readonly IRenterBankAccountRepository _bankAccounts;
    private readonly IInventoryRentalClient _inventory;
    private readonly IAuthRentalClient _auth;
    private readonly RentalTermsOptions _terms;
    private readonly TimeProvider _timeProvider;

    // Giỏ hàng cũng coi Listing có Status = Active là đang cho thuê (InventoryRentalCartReader).
    private const string ActiveStatus = "Active";

    // Khởi tạo các dependency và cấu hình dùng cho service; kiểm tra dependency bắt buộc ngay khi dựng đối tượng.
    public CheckoutPreparationService(
        IRentalCartRepository carts,
        IRenterAddressRepository addresses,
        IRenterBankAccountRepository bankAccounts,
        IInventoryRentalClient inventory,
        IAuthRentalClient auth,
        IOptions<RentalTermsOptions> termsOptions,
        TimeProvider timeProvider)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(termsOptions);

        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        _carts = carts ?? throw new ArgumentNullException(nameof(carts));
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _bankAccounts = bankAccounts ?? throw new ArgumentNullException(nameof(bankAccounts));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _terms = termsOptions.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    // Kiểm tra hồ sơ Auth, điều khoản, địa chỉ/tài khoản thuộc renter và các dòng được chọn; dựng snapshot rồi so tổng tiền client xác nhận.
    public async Task<CheckoutPreparationResult> PrepareAsync(
        CheckoutRenterContext renter,
        CheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra tham số bắt buộc ở đầu hàm để không dùng một dependency/request rỗng trong các bước sau.
        ArgumentNullException.ThrowIfNull(renter);
        ArgumentNullException.ThrowIfNull(request);

        // Từ chối định danh rỗng trước khi tra cứu hoặc gắn dữ liệu vào tài nguyên của luồng.
        if (renter.RenterId == Guid.Empty)
        {
            return CheckoutPreparationResult.Failure(CheckoutErrors.Unauthenticated);
        }

        // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
        Result<AuthUserSummaryDto> authResult = await _auth.GetCurrentUserAsync(cancellationToken);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (authResult.IsFailure)
        {
            return CheckoutPreparationResult.Failure(authResult.Error);
        }

        // Dùng hồ sơ từ Auth và đối chiếu với renter của request; không lấy danh tính/hồ sơ tùy ý từ client.
        AuthUserSummaryDto authUser = authResult.Value;
        // Điều kiện này quyết định có thể tiếp tục hay phải trả CheckoutPreparationResult.Failure theo contract của use case.
        if (authUser.Id != renter.RenterId)
        {
            return CheckoutPreparationResult.Failure(CheckoutErrors.DependencyUnavailable);
        }

        // Yêu cầu hoàn thiện hồ sơ trước checkout để snapshot người thuê có thể được dùng cho luồng giao nhận.
        if (!authUser.IsProfileCompleted)
        {
            return CheckoutPreparationResult.Failure(CheckoutErrors.ProfileIncomplete);
        }

        // Chạy validator/policy của bước hiện tại; chỉ chuyển sang bước sau khi kết quả hợp lệ.
        IReadOnlyList<CheckoutFieldError> fieldErrors = CheckoutRequestValidator.Validate(request);
        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (fieldErrors.Count > 0)
        {
            return CheckoutPreparationResult.InvalidRequest(fieldErrors);
        }

        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(_terms.CurrentVersion) ||
            string.IsNullOrWhiteSpace(_terms.DocumentRef) ||
            string.IsNullOrWhiteSpace(_terms.ContentHash))
        {
            throw new InvalidOperationException($"Thiếu cấu hình {RentalTermsOptions.SectionName}.");
        }

        // Chuẩn hóa nội dung/key trước khi so sánh hoặc lưu, tránh khoảng trắng làm cùng dữ liệu bị coi là hai yêu cầu khác nhau.
        string effectiveTermsVersion = _terms.CurrentVersion.Trim();
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (!string.IsNullOrWhiteSpace(request.TermsVersion))
        {
            // Đối chiếu phiên bản điều khoản client đồng ý với phiên bản hiện hành, tránh gửi đơn theo nội dung đã cũ.
            if (!string.Equals(request.TermsVersion.Trim(), effectiveTermsVersion, StringComparison.Ordinal))
            {
                return CheckoutPreparationResult.Failure(CheckoutErrors.TermsVersionOutdated);
            }
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Chuẩn bị snapshot giao nhận từ địa chỉ thuộc sở hữu hoặc dữ liệu trực tiếp đã validate trong request.
        CheckoutDeliveryDto delivery;
        // Điều kiện này quyết định có thể tiếp tục hay phải trả CheckoutPreparationResult.Failure theo contract của use case.
        if (request.AddressId is { } addressId)
        {
            // Chọn từ sổ địa chỉ: chỉ địa chỉ của chính người thuê; đơn chép lại nội dung lúc đặt.
            RenterAddress? address = await _addresses.GetByIdAsync(renter.RenterId, addressId, cancellationToken);
            // Mục không có trong danh sách của renter thì trả NotFound, bao gồm ID có thật nhưng thuộc người khác.
            if (address is null)
            {
                return CheckoutPreparationResult.Failure(RenterAddressErrors.NotFound);
            }

            // Chuẩn bị snapshot giao nhận/tài khoản từ dữ liệu đã chọn, tách khỏi sổ dữ liệu có thể được sửa sau checkout.
            delivery = new(address.Province, address.AddressDetail, address.RecipientName, address.RecipientPhone, request.DeliveryNote);
        }
        else
        {
            // Chuẩn bị snapshot giao nhận/tài khoản từ dữ liệu đã chọn, tách khỏi sổ dữ liệu có thể được sửa sau checkout.
            delivery = request.Delivery!;
        }

        // Chuẩn bị snapshot giao nhận/tài khoản từ dữ liệu đã chọn, tách khỏi sổ dữ liệu có thể được sửa sau checkout.
        CheckoutRefundAccountDto refundAccount;
        // Điều kiện này quyết định có thể tiếp tục hay phải trả CheckoutPreparationResult.Failure theo contract của use case.
        if (request.RefundAccountId is { } refundAccountId)
        {
            // Chọn từ sổ tài khoản: chỉ tài khoản của chính người thuê; đơn chép lại tài khoản lúc đặt.
            RenterBankAccount? bankAccount = await _bankAccounts.GetByIdAsync(renter.RenterId, refundAccountId, cancellationToken);
            // Mục không có trong danh sách của renter thì trả NotFound, bao gồm ID có thật nhưng thuộc người khác.
            if (bankAccount is null)
            {
                return CheckoutPreparationResult.Failure(RenterBankAccountErrors.NotFound);
            }

            // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
            refundAccount = new(bankAccount.BankCode, bankAccount.AccountNumber, bankAccount.AccountHolderName);
        }
        else
        {
            // Cập nhật số lượng xử lý của batch để báo cáo tiến độ; không dùng bộ đếm thay cho trạng thái đã lưu của từng tác vụ.
            refundAccount = request.RefundAccount!;
        }

        // Giữ ID giỏ khi checkout từ giỏ; BuyNow không cần gắn hoặc sửa một giỏ không liên quan.
        Guid? cartId = null;
        List<CheckoutSelection> selections;
        // Mua ngay dựng selection riêng; nhánh giỏ hàng phải kiểm tra các dòng thuộc giỏ active của renter.
        if (request.BuyNow is { } buyNow)
        {
            // Thuê ngay: một dòng dựng từ body, không đọc giỏ. Mã dòng mới sinh, không thuộc giỏ nào.
            selections =
            [
                new(Guid.NewGuid(), buyNow.EquipmentListingId, buyNow.Quantity,
            buyNow.ExpectedStartDate, buyNow.ExpectedEndDate)
            ];
        }
        else
        {
            // Lấy dữ liệu qua abstraction sở hữu truy vấn; dùng các tiêu chí và phạm vi đã chuẩn bị trong service.
            RentalCart? cart = await _carts.GetByRenterIdAsync(renter.RenterId, cancellationToken);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (cart is null || cart.RenterId != renter.RenterId)
            {
                return CheckoutPreparationResult.Failure(RentalErrors.CartNotFound);
            }

            // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
            if (cart.Status != RentalCartStatus.Active)
            {
                return CheckoutPreparationResult.Failure(RentalErrors.CartNotActive);
            }

            // Giữ ID giỏ khi checkout từ giỏ; BuyNow không cần gắn hoặc sửa một giỏ không liên quan.
            cartId = cart.Id;
            selections = [];
            // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
            foreach (Guid cartItemId in request.CartItemIds!)
            {
                // Tìm đúng phần tử trong aggregate/tập đã tải theo ID; không dùng phần tử khác khi đối tượng cần xử lý không có.
                RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
                // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
                if (item is null)
                {
                    return CheckoutPreparationResult.Failure(RentalErrors.CartItemNotFound);
                }

                // Thêm đúng dòng thuộc giỏ được chọn vào kế hoạch, không tự checkout toàn bộ giỏ của renter.
                selections.Add(new(item.Id, item.EquipmentListingId, item.Quantity, item.ExpectedStartDate, item.ExpectedEndDate));
            }
        }

        // Lấy mốc thời gian cho lần kiểm tra này; các deadline được đối chiếu với mốc của bước xử lý tương ứng.
        DateOnly today = RentalInventoryDateAdapter.ToBusinessDate(utcNow);
        List<CheckoutLineIssue> dateIssues = selections
            .Select(i => CheckoutRules.CheckRentalPeriod(i, today))
            .OfType<CheckoutLineIssue>()
            .ToList();
        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (dateIssues.Count > 0)
        {
            return CheckoutPreparationResult.LinesRejected(CheckoutErrors.InvalidRentalDates, dateIssues);
        }

        // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
        LineBuildOutcome outcome = await BuildLinesAsync(renter.RenterId, selections, cancellationToken);
        // Lỗi phụ thuộc không được biến thành một kết quả đánh giá listing hợp lệ; trả lỗi để client có thể thử lại.
        if (outcome.DependencyFailed)
        {
            return CheckoutPreparationResult.Failure(CheckoutErrors.DependencyUnavailable);
        }

        // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
        if (outcome.Issues.Count > 0)
        {
            // Phân biệt toàn bộ lỗi do tự thuê listing của mình với các lỗi availability khác để trả đúng contract checkout.
            Error linesError = outcome.Issues.All(i => i.Code == CheckoutLineIssueCode.OwnListing)
                ? CheckoutErrors.CannotRentOwnListing
                : CheckoutErrors.ItemsUnavailable;
            return CheckoutPreparationResult.LinesRejected(linesError, outcome.Issues);
        }

        // Đóng gói kế hoạch gồm điều khoản, giao nhận, tài khoản hoàn và giá từng dòng; so tổng với ExpectedTotalAmount để yêu cầu xác nhận lại nếu giá đổi.
        CheckoutPlan plan = new(
            renter.RenterId,
            cartId,
            Normalize(delivery),
            Normalize(refundAccount),
            effectiveTermsVersion,
            _terms.DocumentRef.Trim(),
            _terms.ContentHash.Trim(),
            CheckoutRules.Currency,
            outcome.Lines);

        return plan.TotalAmount == request.ExpectedTotalAmount
            ? CheckoutPreparationResult.Success(plan)
            : CheckoutPreparationResult.PriceChanged(plan);
    }

    /// <summary>
    /// Đọc lại mọi Listing trong một lần gọi Inventory, rồi hỏi lịch trống từng dòng (gọi song song).
    /// Lỗi của mọi dòng được gom lại để trả một lần, theo thứ tự dòng trong giỏ.
    /// </summary>
    private async Task<LineBuildOutcome> BuildLinesAsync(
        Guid renterId,
        List<CheckoutSelection> items,
        CancellationToken cancellationToken)
    {
        // 1. Một lần gọi cho mọi Listing của các dòng đã chọn.
        List<Guid> listingIds = items.Select(i => i.EquipmentListingId).Distinct().ToList();
        Result<ListingRentalSnapshotsResponse> snapshotsResult = await _inventory.GetListingRentalSnapshotsAsync(
            new ListingRentalSnapshotsRequest(listingIds, IncludeWarehouse: true), cancellationToken);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (snapshotsResult.IsFailure ||
            !TryIndexSnapshots(listingIds, snapshotsResult.Value, out Dictionary<Guid, ListingRentalSnapshotDto> listings))
        {
            return LineBuildOutcome.Unavailable;
        }

        // Tích lũy lỗi theo trường/dòng để trả đủ thông tin sửa yêu cầu thay vì che lỗi thành dữ liệu hợp lệ.
        List<CheckoutLineIssue> issues = [];
        List<(CheckoutSelection Item, ListingRentalSnapshotDto Listing, DateOnly From, DateOnly To)> candidates = [];
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (CheckoutSelection item in items)
        {
            // Chỉ xử lý ID có trong tập đã tra cứu; thiếu mapping phải báo lỗi thay vì gắn sang phần tử khác.
            if (!listings.TryGetValue(item.EquipmentListingId, out ListingRentalSnapshotDto? listing) || !IsRentable(listing))
            {
                // Tích lũy lỗi theo trường/dòng để trả đủ thông tin sửa yêu cầu thay vì che lỗi thành dữ liệu hợp lệ.
                issues.Add(new(item.LineId, item.EquipmentListingId, CheckoutLineIssueCode.ListingUnavailable,
                    "Thiết bị không còn cho thuê."));
                continue;
            }

            // Chặn người dùng thuê listing do chính mình sở hữu trước khi tạo kế hoạch hoặc hold.
            if (listing.OwnerUserId == renterId)
            {
                // Tích lũy lỗi theo trường/dòng để trả đủ thông tin sửa yêu cầu thay vì che lỗi thành dữ liệu hợp lệ.
                issues.Add(new(item.LineId, item.EquipmentListingId, CheckoutLineIssueCode.OwnListing,
                    "Bạn không thể thuê thiết bị của chính mình."));
                continue;
            }

            // Chuyển mốc ngày qua adapter/policy hiện có để Rental và Inventory dùng đúng quy ước khoảng thuê.
            (DateOnly orderStartDate, DateOnly orderEndDate) = CheckoutRules.ToOrderPeriod(item);
            candidates.Add((item, listing, orderStartDate, orderEndDate));
        }

        // 2. Hỏi lịch trống từng dòng bằng API của Inventory, các dòng gọi song song.
        // Giỏ và Inventory cùng tính ngày cuối có tính nên gửi thẳng ngày của giỏ. Chống đặt trùng là việc của Inventory lúc hold.
        Result<ListingAvailabilityCheckResponse>[] availability = await Task.WhenAll(candidates.Select(c =>
            _inventory.CheckListingAvailabilityAsync(
                new ListingAvailabilityCheckRequest(
                    c.Item.EquipmentListingId, c.Item.ExpectedStartDate, c.Item.ExpectedEndDate, c.Item.Quantity),
                cancellationToken)));

        // Tích lũy response theo các phần tử đã kiểm tra; danh sách này chỉ phục vụ kết quả của lần xử lý hiện tại.
        List<CheckoutPlanLine> lines = [];
        // Duyệt tập dữ liệu của bước hiện tại để tích lũy kết quả, giữ nguyên phạm vi và thứ tự mà caller đã chọn.
        for (int i = 0; i < candidates.Count; i++)
        {
            // Sử dụng dữ liệu của kết quả đã kiểm tra ở bước trước để dựng quyết định tiếp theo, không gọi lại để thay snapshot giữa luồng.
            var (item, listing, orderStartDate, orderEndDate) = candidates[i];
            Result<ListingAvailabilityCheckResponse> result = availability[i];
            // Trả lỗi từ bước kiểm tra vừa chạy; không thực hiện phần ghi hoặc gọi provider tiếp theo khi điều kiện chưa đạt.
            if (result.IsFailure && result.Error.Type == ErrorType.NotFound)
            {
                // Listing vừa bị xóa giữa hai lần gọi.
                issues.Add(new(item.LineId, item.EquipmentListingId, CheckoutLineIssueCode.ListingUnavailable,
                    "Thiết bị không còn cho thuê."));
                continue;
            }

            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (result.IsFailure || !MatchesLine(result.Value, item))
            {
                // Inventory không phản hồi, hoặc trả kết quả của Listing/khoảng ngày khác với dòng đã hỏi.
                return LineBuildOutcome.Unavailable;
            }

            // Sử dụng dữ liệu của kết quả đã kiểm tra ở bước trước để dựng quyết định tiếp theo, không gọi lại để thay snapshot giữa luồng.
            ListingAvailabilityCheckResponse lineAvailability = result.Value;
            // Kiểm tra số lượng và giới hạn tập dữ liệu trước khi xử lý, tránh chấp nhận thiếu/thừa phần tử của yêu cầu.
            if (!lineAvailability.IsAvailable || lineAvailability.AvailableQuantity < item.Quantity)
            {
                // Tích lũy lỗi theo trường/dòng để trả đủ thông tin sửa yêu cầu thay vì che lỗi thành dữ liệu hợp lệ.
                issues.Add(new(item.LineId, item.EquipmentListingId, CheckoutLineIssueCode.InsufficientAvailability,
                    "Thiết bị không đủ số lượng trống trong ngày đã chọn.", item.Quantity, lineAvailability.AvailableQuantity));
                continue;
            }

            // Tích lũy response theo các phần tử đã kiểm tra; danh sách này chỉ phục vụ kết quả của lần xử lý hiện tại.
            lines.Add(BuildLine(item, orderStartDate, orderEndDate, listing));
        }

        // Chọn, sắp xếp hoặc chiếu dữ liệu đã có thành tập phục vụ bước tiếp theo; không thay đổi nguồn dữ liệu gốc.
        Dictionary<Guid, int> cartOrder = items.Select((item, index) => (item.LineId, index)).ToDictionary(x => x.LineId, x => x.index);
        return new(false, lines, issues.OrderBy(i => cartOrder[i.CartItemId]).ToList());
    }

    /// <summary>
    /// Đánh chỉ mục snapshot theo ListingId. Sai hợp đồng (Listing lạ, trùng, thiếu trường bắt buộc, hoặc Listing đã hỏi
    /// nhưng không có trong cả kết quả lẫn danh sách không tìm thấy) là lỗi phía Inventory, không phải thiết bị ngừng cho thuê.
    /// </summary>
    private static bool TryIndexSnapshots(
        List<Guid> requestedIds,
        ListingRentalSnapshotsResponse response,
        out Dictionary<Guid, ListingRentalSnapshotDto> listings)
    {
        // Lập bảng lookup snapshot và tập ID yêu cầu để phát hiện phản hồi thừa, thiếu hoặc trùng từ Inventory.
        Dictionary<Guid, ListingRentalSnapshotDto> indexed = [];
        listings = indexed;
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (response.Items is null || response.MissingIds is null)
        {
            return false;
        }

        // Lập bảng lookup snapshot và tập ID yêu cầu để phát hiện phản hồi thừa, thiếu hoặc trùng từ Inventory.
        HashSet<Guid> requested = [.. requestedIds];
        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (ListingRentalSnapshotDto listing in response.Items)
        {
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (listing is null || !requested.Contains(listing.Id) || !IsComplete(listing) || !indexed.TryAdd(listing.Id, listing))
            {
                return false;
            }
        }

        // Kiểm tra danh sách MissingIds không trùng/không giao với snapshot trả về và phủ đủ các ID đã hỏi.
        HashSet<Guid> notFound = [.. response.MissingIds];
        return notFound.Count == response.MissingIds.Count &&
               notFound.All(id => requested.Contains(id) && !indexed.ContainsKey(id)) &&
               requestedIds.All(id => indexed.ContainsKey(id) || notFound.Contains(id));
    }

    // Dựng snapshot bất biến của một dòng checkout: thông tin listing, điểm nhận máy, giá thuê và cọc theo số ngày/số lượng.
    private static CheckoutPlanLine BuildLine(
        CheckoutSelection item,
        DateOnly orderStartDate,
        DateOnly orderEndDate,
        ListingRentalSnapshotDto listing)
    {
        // Tính khoảng ngày hoặc số tiền từ dữ liệu của bước hiện tại; giữ công thức/chính sách đang được use case sử dụng.
        decimal dailyRate = listing.DailyRate;
        int rentalDays = RentalPricingPolicy.CalculateRentalDays(orderStartDate, orderEndDate);

        // Ghép địa chỉ kho thành snapshot điểm nhận; không có nội dung thì để null thay vì lưu địa chỉ giả định.
        string? pickupLocationSnapshot = null;
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (listing.Warehouse != null)
        {
            // Ghép địa chỉ kho thành snapshot điểm nhận; không có nội dung thì để null thay vì lưu địa chỉ giả định.
            var parts = new List<string>();
            // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
            if (!string.IsNullOrWhiteSpace(listing.Warehouse.AddressDetail)) parts.Add(listing.Warehouse.AddressDetail.Trim());
            // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
            if (!string.IsNullOrWhiteSpace(listing.Warehouse.WardName)) parts.Add(listing.Warehouse.WardName.Trim());
            // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
            if (!string.IsNullOrWhiteSpace(listing.Warehouse.ProvinceName)) parts.Add(listing.Warehouse.ProvinceName.Trim());
            var text = string.Join(", ", parts);
            // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
            if (listing.Warehouse.Latitude.HasValue && listing.Warehouse.Longitude.HasValue)
            {
                // Chuẩn hóa tọa độ bằng định dạng bất biến để snapshot không đổi dấu thập phân theo locale của server.
                var latStr = listing.Warehouse.Latitude.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                var lngStr = listing.Warehouse.Longitude.Value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                pickupLocationSnapshot = string.IsNullOrWhiteSpace(text)
                    ? $"{latStr}, {lngStr}"
                    : $"{text} ({latStr}, {lngStr})";
            }
            else
            {
                // Ghép địa chỉ kho thành snapshot điểm nhận; không có nội dung thì để null thay vì lưu địa chỉ giả định.
                pickupLocationSnapshot = string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }

        return new(
            item.LineId,
            item.EquipmentListingId,
            listing.OwnerUserId,
            item.Quantity,
            orderStartDate,
            orderEndDate,
            listing.Name.Trim(),
            new RentalItemListingSnapshotDto(
                listing.EquipmentTypeId,
                listing.EquipmentTypeName,
                listing.BrandId,
                listing.BrandName,
                listing.Model,
                listing.SpecSchemaVersion,
                listing.SpecValues?.GetRawText(),
                listing.ThumbnailMediaId),
            dailyRate,
            listing.DepositPerUnit!.Value,
            rentalDays,
            RentalPricingPolicy.CalculateRentalAmount(dailyRate, item.Quantity, rentalDays),
            RentalPricingPolicy.CalculateDepositAmount(listing.DepositPerUnit.Value, item.Quantity),
            pickupLocationSnapshot);
    }

    // Đối chiếu listing và khoảng ngày phản hồi availability với đúng dòng được hỏi.
    private static bool MatchesLine(ListingAvailabilityCheckResponse response, CheckoutSelection item) =>
        response is not null &&
        response.ListingId == item.EquipmentListingId &&
        response.StartDate == item.ExpectedStartDate &&
        response.EndDate == item.ExpectedEndDate;

    // Kiểm tra các trường bắt buộc của snapshot thương mại trước khi chấp nhận dữ liệu từ Inventory.
    private static bool IsComplete(ListingRentalSnapshotDto listing) =>
        listing.OwnerUserId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(listing.Name) &&
        !string.IsNullOrWhiteSpace(listing.Status) &&
        !string.IsNullOrWhiteSpace(listing.Currency) &&
        listing.EquipmentTypeId is { } equipmentTypeId && equipmentTypeId != Guid.Empty &&
        listing.BrandId is { } brandId && brandId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(listing.Model) &&
        listing.DepositPerUnit is >= 0 &&
        VndAmountValidator.ValidateVndAmount(listing.DepositPerUnit.Value).IsSuccess &&
        listing.SpecSchemaVersion is >= 1 &&
        listing.SpecValues is { ValueKind: System.Text.Json.JsonValueKind.Object };

    // Chỉ nhận listing đang Active, giá thuê VND hợp lệ và đúng loại tiền checkout hỗ trợ.
    private static bool IsRentable(ListingRentalSnapshotDto listing) =>
        string.Equals(listing.Status, ActiveStatus, StringComparison.OrdinalIgnoreCase) &&
        listing.DailyRate > 0 &&
        VndAmountValidator.ValidateVndAmount(listing.DailyRate).IsSuccess &&
        string.Equals(listing.Currency, CheckoutRules.Currency, StringComparison.OrdinalIgnoreCase);

    // Chuẩn hóa khoảng trắng của snapshot giao nhận hoặc tài khoản hoàn tiền trước khi ghi vào kế hoạch checkout.
    private static CheckoutDeliveryDto Normalize(CheckoutDeliveryDto delivery) =>
        new(
            delivery.Province!.Trim(),
            delivery.AddressDetail!.Trim(),
            delivery.RecipientName!.Trim(),
            delivery.RecipientPhone!.Trim(),
            string.IsNullOrWhiteSpace(delivery.Note) ? null : delivery.Note.Trim());

    // Chuẩn hóa khoảng trắng của snapshot giao nhận hoặc tài khoản hoàn tiền trước khi ghi vào kế hoạch checkout.
    private static CheckoutRefundAccountDto Normalize(CheckoutRefundAccountDto refundAccount) =>
        new(
            refundAccount.BankCode!.Trim(),
            refundAccount.AccountNumber!.Trim(),
            refundAccount.AccountHolderName!.Trim());

    private sealed record LineBuildOutcome(
        bool DependencyFailed,
        List<CheckoutPlanLine> Lines,
        List<CheckoutLineIssue> Issues)
    {
        public static readonly LineBuildOutcome Unavailable = new(true, [], []);
    }
}
