using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Policies;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Checkout;

public sealed class CheckoutPreparationService : ICheckoutPreparationService
{
    private readonly IRentalCartRepository _carts;
    private readonly IRentalOrderRepository _orders;
    private readonly IInventoryListingClient _inventory;
    private readonly ICatalogReferenceClient _catalog;
    private readonly RentalTermsOptions _terms;
    private readonly TimeProvider _timeProvider;

    public CheckoutPreparationService(
        IRentalCartRepository carts,
        IRentalOrderRepository orders,
        IInventoryListingClient inventory,
        ICatalogReferenceClient catalog,
        IOptions<RentalTermsOptions> termsOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(termsOptions);

        _carts = carts ?? throw new ArgumentNullException(nameof(carts));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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
    /// Đọc lại Listing, lịch và tên loại/hãng cho từng dòng. Lỗi của mọi dòng được gom lại để trả một lần.
    /// </summary>
    private async Task<LineBuildOutcome> BuildLinesAsync(
        Guid renterId,
        List<RentalCartItem> items,
        CancellationToken cancellationToken)
    {
        List<CheckoutPlanLine> lines = [];
        List<CheckoutLineIssue> issues = [];
        Dictionary<Guid, InventoryListingDto?> listings = [];
        Dictionary<Guid, string?> equipmentTypeNames = [];
        Dictionary<Guid, string?> brandNames = [];

        foreach (RentalCartItem item in items)
        {
            if (!listings.TryGetValue(item.EquipmentListingId, out InventoryListingDto? listing))
            {
                Result<InventoryListingDto?> listingResult = await _inventory.GetListingAsync(item.EquipmentListingId, cancellationToken);
                if (listingResult.IsFailure)
                {
                    return LineBuildOutcome.Unavailable;
                }

                listing = listingResult.Value;
                listings[item.EquipmentListingId] = listing;
            }

            if (listing is null || !IsRentable(listing))
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

            // Mỗi dòng được hỏi lịch độc lập để báo lỗi sớm; chống đặt trùng là việc của Inventory lúc hold.
            Result<InventoryListingAvailabilityDto> availabilityResult = await _inventory.CheckAvailabilityAsync(
                item.EquipmentListingId,
                item.ExpectedStartDate,
                item.ExpectedEndDate,
                item.Quantity,
                cancellationToken);
            if (availabilityResult.IsFailure)
            {
                return LineBuildOutcome.Unavailable;
            }

            InventoryListingAvailabilityDto availability = availabilityResult.Value;
            if (!availability.IsAvailable || availability.AvailableQuantity < item.Quantity)
            {
                issues.Add(new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.InsufficientAvailability,
                    "Thiết bị không đủ số lượng trống trong ngày đã chọn.", item.Quantity, availability.AvailableQuantity));
                continue;
            }

            if (!equipmentTypeNames.TryGetValue(listing.EquipmentTypeId, out string? equipmentTypeName))
            {
                Result<string?> typeResult = await _catalog.GetEquipmentTypeNameAsync(listing.EquipmentTypeId, cancellationToken);
                if (typeResult.IsFailure)
                {
                    return LineBuildOutcome.Unavailable;
                }

                equipmentTypeName = typeResult.Value;
                equipmentTypeNames[listing.EquipmentTypeId] = equipmentTypeName;
            }

            if (!brandNames.TryGetValue(listing.BrandId, out string? brandName))
            {
                Result<string?> brandResult = await _catalog.GetBrandNameAsync(listing.BrandId, cancellationToken);
                if (brandResult.IsFailure)
                {
                    return LineBuildOutcome.Unavailable;
                }

                brandName = brandResult.Value;
                brandNames[listing.BrandId] = brandName;
            }

            if (string.IsNullOrWhiteSpace(equipmentTypeName) || string.IsNullOrWhiteSpace(brandName))
            {
                issues.Add(new(item.Id, item.EquipmentListingId, CheckoutLineIssueCode.ListingUnavailable,
                    "Thiết bị không còn cho thuê."));
                continue;
            }

            lines.Add(BuildLine(item, listing, equipmentTypeName.Trim(), brandName.Trim()));
        }

        return new(false, lines, issues);
    }

    private static CheckoutPlanLine BuildLine(
        RentalCartItem item,
        InventoryListingDto listing,
        string equipmentTypeName,
        string brandName)
    {
        decimal dailyRate = listing.DailyRate!.Value;
        decimal depositPerUnit = listing.DepositPerUnit!.Value;
        int rentalDays = RentalPricingPolicy.CalculateRentalDays(item.ExpectedStartDate, item.ExpectedEndDate);

        RentalItemListingSnapshotDto snapshot = new(
            listing.EquipmentTypeId,
            equipmentTypeName,
            listing.BrandId,
            brandName,
            listing.Model.Trim(),
            listing.SchemaVersion,
            listing.SpecValuesJson.Trim(),
            listing.ThumbnailMediaId);

        return new(
            item.Id,
            item.EquipmentListingId,
            listing.OwnerUserId,
            item.Quantity,
            item.ExpectedStartDate,
            item.ExpectedEndDate,
            listing.Title.Trim(),
            listing.PickupLocation!.Trim(),
            snapshot,
            dailyRate,
            depositPerUnit,
            rentalDays,
            RentalPricingPolicy.CalculateRentalAmount(dailyRate, item.Quantity, rentalDays),
            RentalPricingPolicy.CalculateDepositAmount(depositPerUnit, item.Quantity));
    }

    private static bool IsRentable(InventoryListingDto listing) =>
        listing.IsActive &&
        listing.OwnerUserId != Guid.Empty &&
        listing.EquipmentTypeId != Guid.Empty &&
        listing.BrandId != Guid.Empty &&
        listing.SchemaVersion >= 1 &&
        !string.IsNullOrWhiteSpace(listing.Title) &&
        !string.IsNullOrWhiteSpace(listing.Model) &&
        !string.IsNullOrWhiteSpace(listing.SpecValuesJson) &&
        !string.IsNullOrWhiteSpace(listing.PickupLocation) &&
        listing.DailyRate is > 0 &&
        listing.DepositPerUnit is >= 0 &&
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
