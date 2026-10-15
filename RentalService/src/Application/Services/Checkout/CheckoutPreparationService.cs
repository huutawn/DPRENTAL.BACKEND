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
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Policies;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Services.Checkout;

public sealed class CheckoutPreparationService : ICheckoutPreparationService
{
    private readonly IRentalCartRepository _carts;
    private readonly IRentalOrderRepository _orders;
    private readonly IInventoryRentalClient _inventory;
    private readonly RentalTermsOptions _terms;
    private readonly TimeProvider _timeProvider;

    // Giỏ hàng cũng coi Listing có Status = Active là đang cho thuê (InventoryRentalCartReader).
    private const string ActiveStatus = "Active";

    public CheckoutPreparationService(
        IRentalCartRepository carts,
        IRentalOrderRepository orders,
        IInventoryRentalClient inventory,
        IOptions<RentalTermsOptions> termsOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(termsOptions);

        _carts = carts ?? throw new ArgumentNullException(nameof(carts));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _terms = termsOptions.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<CheckoutPreparationResult> PrepareAsync(
        CheckoutRenterContext renter,
        CheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renter);
        ArgumentNullException.ThrowIfNull(request);

        if (renter.RenterId == Guid.Empty)
        {
            return CheckoutPreparationResult.Failure(CheckoutErrors.Unauthenticated);
        }

        if (!renter.IsProfileCompleted)
        {
            return CheckoutPreparationResult.Failure(CheckoutErrors.ProfileIncomplete);
        }

        IReadOnlyList<CheckoutFieldError> fieldErrors = CheckoutRequestValidator.Validate(request);
        if (fieldErrors.Count > 0)
        {
            return CheckoutPreparationResult.InvalidRequest(fieldErrors);
        }

        if (string.IsNullOrWhiteSpace(_terms.CurrentVersion) ||
            string.IsNullOrWhiteSpace(_terms.DocumentRef) ||
            string.IsNullOrWhiteSpace(_terms.ContentHash))
        {
            throw new InvalidOperationException($"Thiếu cấu hình {RentalTermsOptions.SectionName}.");
        }

        if (!string.Equals(request.TermsVersion!.Trim(), _terms.CurrentVersion.Trim(), StringComparison.Ordinal))
        {
            return CheckoutPreparationResult.Failure(CheckoutErrors.TermsVersionOutdated);
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        RentalOrder? pendingOrder = await _orders.GetPendingPaymentByRenterIdAsync(renter.RenterId, cancellationToken);
        if (pendingOrder is not null && pendingOrder.PaymentDeadline > utcNow)
        {
            return CheckoutPreparationResult.PendingOrder(pendingOrder.OrderCode);
        }

        RentalCart? cart = await _carts.GetByRenterIdAsync(renter.RenterId, cancellationToken);
        if (cart is null || cart.RenterId != renter.RenterId)
        {
            return CheckoutPreparationResult.Failure(RentalErrors.CartNotFound);
        }

        if (cart.Status != RentalCartStatus.Active)
        {
            return CheckoutPreparationResult.Failure(RentalErrors.CartNotActive);
        }

        List<RentalCartItem> selectedItems = [];
        foreach (Guid cartItemId in request.CartItemIds!)
        {
            RentalCartItem? item = cart.Items.FirstOrDefault(i => i.Id == cartItemId);
            if (item is null)
            {
                return CheckoutPreparationResult.Failure(RentalErrors.CartItemNotFound);
            }

            selectedItems.Add(item);
        }

        DateOnly today = RentalInventoryDateAdapter.ToBusinessDate(utcNow);
        List<CheckoutLineIssue> dateIssues = selectedItems
            .Select(i => CheckoutRules.CheckRentalPeriod(i, today))
            .OfType<CheckoutLineIssue>()
            .ToList();
        if (dateIssues.Count > 0)
        {
            return CheckoutPreparationResult.LinesRejected(CheckoutErrors.InvalidRentalDates, dateIssues);
        }

        LineBuildOutcome outcome = await BuildLinesAsync(renter.RenterId, selectedItems, cancellationToken);
        if (outcome.DependencyFailed)
        {
            return CheckoutPreparationResult.Failure(CheckoutErrors.DependencyUnavailable);
        }

        if (outcome.Issues.Count > 0)
        {
            return CheckoutPreparationResult.LinesRejected(CheckoutErrors.ItemsUnavailable, outcome.Issues);
        }

        CheckoutPlan plan = new(
            renter.RenterId,
            cart.Id,
            Normalize(request.Delivery!),
            Normalize(request.RefundAccount!),
            _terms.CurrentVersion.Trim(),
            _terms.DocumentRef.Trim(),
            _terms.ContentHash.Trim(),
            CheckoutRules.Currency,
            outcome.Lines);

        return plan.TotalAmount == request.ExpectedTotalAmount
            ? CheckoutPreparationResult.Success(plan)
            : CheckoutPreparationResult.PriceChanged(plan);
    }

    /// <summary>
    /// Đọc lại Listing và lịch trống của mọi dòng, mỗi việc một lần gọi Inventory theo danh sách.
    /// Lỗi của mọi dòng được gom lại để trả một lần, theo thứ tự dòng trong giỏ.
    /// </summary>
    private async Task<LineBuildOutcome> BuildLinesAsync(
        Guid renterId,
        List<RentalCartItem> items,
        CancellationToken cancellationToken)
    {
        // 1. Một lần gọi cho mọi Listing của các dòng đã chọn.
        List<Guid> listingIds = items.Select(i => i.EquipmentListingId).Distinct().ToList();
        Result<ListingRentalSnapshotsResponse> snapshotsResult = await _inventory.GetListingRentalSnapshotsAsync(
            new ListingRentalSnapshotsRequest(listingIds), cancellationToken);
        if (snapshotsResult.IsFailure ||
            !TryIndexSnapshots(listingIds, snapshotsResult.Value, out Dictionary<Guid, ListingRentalSnapshotDto> listings))
        {
            return LineBuildOutcome.Unavailable;
        }

        List<CheckoutLineIssue> issues = [];
        List<(RentalCartItem Item, ListingRentalSnapshotDto Listing, DateOnly From, DateOnly To)> candidates = [];
        foreach (RentalCartItem item in items)
        {
            if (!listings.TryGetValue(item.EquipmentListingId, out ListingRentalSnapshotDto? listing) || !IsRentable(listing))
            {
                issues.Add(new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.ListingUnavailable,
                    "Thiết bị không còn cho thuê."));
                continue;
            }

            if (listing.OwnerUserId == renterId)
            {
                issues.Add(new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.OwnListing,
                    "Bạn không thể thuê thiết bị của chính mình."));
                continue;
            }

            (DateOnly orderStartDate, DateOnly orderEndDate) = CheckoutRules.ToOrderPeriod(item);
            candidates.Add((item, listing, orderStartDate, orderEndDate));
        }

        // 2. Một lần gọi hỏi lịch trống của mọi dòng còn lại; chống đặt trùng là việc của Inventory lúc hold.
        Dictionary<(Guid ListingId, DateOnly From, DateOnly To), ListingAvailabilityBatchResult> availability = [];
        if (candidates.Count > 0)
        {
            Result<ListingAvailabilityBatchResponse> availabilityResult = await _inventory.CheckListingAvailabilityBatchAsync(
                new ListingAvailabilityBatchRequest(candidates
                    .Select(c => new ListingAvailabilityBatchLine(c.Item.EquipmentListingId, c.From, c.To, c.Item.Quantity))
                    .ToList()),
                cancellationToken);
            if (availabilityResult.IsFailure || availabilityResult.Value.Results is null)
            {
                return LineBuildOutcome.Unavailable;
            }

            foreach (ListingAvailabilityBatchResult result in availabilityResult.Value.Results)
            {
                availability[(result.ListingId, result.From, result.To)] = result;
            }
        }

        List<CheckoutPlanLine> lines = [];
        foreach (var (item, listing, orderStartDate, orderEndDate) in candidates)
        {
            if (!availability.TryGetValue((item.EquipmentListingId, orderStartDate, orderEndDate), out ListingAvailabilityBatchResult? lineAvailability))
            {
                // Inventory trả thiếu kết quả của một dòng đã hỏi.
                return LineBuildOutcome.Unavailable;
            }

            if (!lineAvailability.IsAvailable || lineAvailability.AvailableQuantity < item.Quantity)
            {
                issues.Add(new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.InsufficientAvailability,
                    "Thiết bị không đủ số lượng trống trong ngày đã chọn.", item.Quantity, lineAvailability.AvailableQuantity));
                continue;
            }

            lines.Add(BuildLine(item, orderStartDate, orderEndDate, listing));
        }

        Dictionary<Guid, int> cartOrder = items.Select((item, index) => (item.Id, index)).ToDictionary(x => x.Id, x => x.index);
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
        Dictionary<Guid, ListingRentalSnapshotDto> indexed = [];
        listings = indexed;
        if (response.Items is null || response.MissingIds is null)
        {
            return false;
        }

        HashSet<Guid> requested = [.. requestedIds];
        foreach (ListingRentalSnapshotDto listing in response.Items)
        {
            if (listing is null || !requested.Contains(listing.Id) || !IsComplete(listing) || !indexed.TryAdd(listing.Id, listing))
            {
                return false;
            }
        }

        HashSet<Guid> notFound = [.. response.MissingIds];
        return requestedIds.All(id => indexed.ContainsKey(id) || notFound.Contains(id));
    }

    private static CheckoutPlanLine BuildLine(
        RentalCartItem item,
        DateOnly orderStartDate,
        DateOnly orderEndDate,
        ListingRentalSnapshotDto listing)
    {
        decimal dailyRate = listing.DailyRate;
        int rentalDays = RentalPricingPolicy.CalculateRentalDays(orderStartDate, orderEndDate);

        return new(
            item.Id,
            item.EquipmentListingId,
            listing.OwnerUserId,
            item.Quantity,
            orderStartDate,
            orderEndDate,
            listing.Name.Trim(),
            RentalItemListingSnapshotDto.ThumbnailOnly(listing.ThumbnailMediaId),
            dailyRate,
            CheckoutRules.DepositPerUnit,
            rentalDays,
            RentalPricingPolicy.CalculateRentalAmount(dailyRate, item.Quantity, rentalDays),
            RentalPricingPolicy.CalculateDepositAmount(CheckoutRules.DepositPerUnit, item.Quantity));
    }

    private static bool IsComplete(ListingRentalSnapshotDto listing) =>
        listing.OwnerUserId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(listing.Name) &&
        !string.IsNullOrWhiteSpace(listing.Status) &&
        !string.IsNullOrWhiteSpace(listing.Currency);

    private static bool IsRentable(ListingRentalSnapshotDto listing) =>
        string.Equals(listing.Status, ActiveStatus, StringComparison.OrdinalIgnoreCase) &&
        listing.DailyRate > 0 &&
        string.Equals(listing.Currency, CheckoutRules.Currency, StringComparison.OrdinalIgnoreCase);

    private static CheckoutDeliveryDto Normalize(CheckoutDeliveryDto delivery) =>
        new(
            delivery.Province!.Trim(),
            delivery.AddressDetail!.Trim(),
            delivery.RecipientName!.Trim(),
            delivery.RecipientPhone!.Trim(),
            string.IsNullOrWhiteSpace(delivery.Note) ? null : delivery.Note.Trim());

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
