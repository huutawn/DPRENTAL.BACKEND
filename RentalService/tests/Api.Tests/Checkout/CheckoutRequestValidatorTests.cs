using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Validation;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

public sealed class CheckoutRequestValidatorTests
{
    private static CheckoutRequest ValidRequest() => new(
        [Guid.NewGuid()],
        new("Hồ Chí Minh", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", "Gọi trước khi giao"),
        new("VCB", "0123456789", "NGUYEN VAN A"),
        "v1.0",
        true,
        7_000_000m);

    private static CheckoutRequest BuyNowRequest() => ValidRequest() with
    {
        CartItemIds = null,
        BuyNow = new(Guid.NewGuid(), 1, new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 27))
    };

    [Fact]
    public void Validate_AcceptsBuyNowWithoutCartItems()
    {
        Assert.Empty(CheckoutRequestValidator.Validate(BuyNowRequest()));
    }

    [Fact]
    public void Validate_RejectsCartItemsAndBuyNowTogether()
    {
        CheckoutRequest request = BuyNowRequest() with { CartItemIds = [Guid.NewGuid()] };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "buyNow" && e.Code == "CONFLICTING");
    }

    [Fact]
    public void Validate_RejectsInvalidBuyNowFields()
    {
        CheckoutRequest request = BuyNowRequest() with
        {
            BuyNow = new(Guid.Empty, 0, new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 25))
        };

        IReadOnlyList<CheckoutFieldError> errors = CheckoutRequestValidator.Validate(request);

        Assert.Contains(errors, e => e.Field == "buyNow.equipmentListingId");
        Assert.Contains(errors, e => e.Field == "buyNow.quantity");
        Assert.Contains(errors, e => e.Field == "buyNow.expectedEndDate");
    }

    [Fact]
    public void Validate_AcceptsValidRequest()
    {
        Assert.Empty(CheckoutRequestValidator.Validate(ValidRequest()));
    }

    [Theory]
    [InlineData("0901234567")]
    [InlineData("+84901234567")]
    public void Validate_AcceptsPhoneStartingWithZeroOrPlus84(string phone)
    {
        CheckoutRequest request = ValidRequest();
        request = request with { Delivery = request.Delivery! with { RecipientPhone = phone } };

        Assert.Empty(CheckoutRequestValidator.Validate(request));
    }

    [Theory]
    [InlineData("901234567")]
    [InlineData("09012345678")]
    [InlineData("84901234567")]
    [InlineData("09012a4567")]
    [InlineData("")]
    public void Validate_RejectsInvalidPhone(string phone)
    {
        CheckoutRequest request = ValidRequest();
        request = request with { Delivery = request.Delivery! with { RecipientPhone = phone } };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "delivery.recipientPhone");
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("123456789012345678901")]
    [InlineData("12345A")]
    public void Validate_RejectsInvalidAccountNumber(string accountNumber)
    {
        CheckoutRequest request = ValidRequest();
        request = request with { RefundAccount = request.RefundAccount! with { AccountNumber = accountNumber } };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "refundAccount.accountNumber");
    }

    [Fact]
    public void Validate_RejectsAddressShorterThanFiveCharacters()
    {
        CheckoutRequest request = ValidRequest();
        request = request with { Delivery = request.Delivery! with { AddressDetail = "12 A" } };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "delivery.addressDetail");
    }

    [Fact]
    public void Validate_RejectsNoteLongerThan500Characters()
    {
        CheckoutRequest request = ValidRequest();
        request = request with { Delivery = request.Delivery! with { Note = new string('x', 501) } };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "delivery.note");
    }

    [Fact]
    public void Validate_RequiresTermsAcceptance()
    {
        CheckoutRequest request = ValidRequest() with { TermsAccepted = false };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "termsAccepted");
    }

    [Fact]
    public void Validate_RejectsEmptyOrDuplicateCartItems()
    {
        Guid duplicated = Guid.NewGuid();

        Assert.Contains(
            CheckoutRequestValidator.Validate(ValidRequest() with { CartItemIds = [] }),
            e => e.Field == "cartItemIds");
        Assert.Contains(
            CheckoutRequestValidator.Validate(ValidRequest() with { CartItemIds = [duplicated, duplicated] }),
            e => e.Field == "cartItemIds" && e.Code == "DUPLICATE");
    }

    [Fact]
    public void Validate_ReportsEveryMissingSection()
    {
        CheckoutRequest request = new(null, null, null, null, false, -1m);

        IReadOnlyList<CheckoutFieldError> errors = CheckoutRequestValidator.Validate(request);

        Assert.Equal(
            ["cartItemIds", "delivery", "expectedTotalAmount", "refundAccount", "termsAccepted"],
            errors.Select(e => e.Field).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsMissingOrNullTermsVersion()
    {
        CheckoutRequest request = ValidRequest() with { TermsVersion = null };
        Assert.Empty(CheckoutRequestValidator.Validate(request));
    }

    [Fact]
    public void Validate_AcceptsAddressIdInsteadOfDelivery()
    {
        CheckoutRequest request = ValidRequest() with { Delivery = null, AddressId = Guid.NewGuid(), DeliveryNote = "Gọi trước khi giao" };

        Assert.Empty(CheckoutRequestValidator.Validate(request));
    }

    [Fact]
    public void Validate_RejectsAddressIdAndDeliveryTogether()
    {
        CheckoutRequest request = ValidRequest() with { AddressId = Guid.NewGuid() };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "addressId" && e.Code == "CONFLICTING");
    }

    [Fact]
    public void Validate_RejectsDeliveryNoteWithoutAddressId()
    {
        CheckoutRequest request = ValidRequest() with { DeliveryNote = "Gọi trước khi giao" };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "deliveryNote");
    }

    [Fact]
    public void Validate_RequiresDeliveryWhenNoAddressId()
    {
        CheckoutRequest request = ValidRequest() with { Delivery = null };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "delivery");
    }

    [Fact]
    public void Validate_AcceptsRefundAccountIdInsteadOfRefundAccount()
    {
        CheckoutRequest request = ValidRequest() with { RefundAccount = null, RefundAccountId = Guid.NewGuid() };

        Assert.Empty(CheckoutRequestValidator.Validate(request));
    }

    [Fact]
    public void Validate_RejectsRefundAccountIdAndRefundAccountTogether()
    {
        CheckoutRequest request = ValidRequest() with { RefundAccountId = Guid.NewGuid() };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "refundAccountId" && e.Code == "CONFLICTING");
    }

    [Fact]
    public void Validate_RejectsEmptyRefundAccountId()
    {
        CheckoutRequest request = ValidRequest() with { RefundAccount = null, RefundAccountId = Guid.Empty };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "refundAccountId" && e.Code == "INVALID_FORMAT");
    }

    [Fact]
    public void Validate_PickupDeliveryMethod_AcceptsWithoutAddressDetailOrProvince()
    {
        CheckoutRequest request = ValidRequest() with
        {
            DeliveryMethod = "Pickup",
            Delivery = new(null, null, "Nguyễn Văn B", "0901234567", "Tự tới lấy"),
        };

        Assert.Empty(CheckoutRequestValidator.Validate(request));
    }

    [Fact]
    public void Validate_PickupDeliveryMethod_RejectsInvalidRecipientPhone()
    {
        CheckoutRequest request = ValidRequest() with
        {
            DeliveryMethod = "Pickup",
            Delivery = new(null, null, "Nguyễn Văn B", "invalid-phone", "Tự tới lấy"),
        };

        Assert.Contains(CheckoutRequestValidator.Validate(request), e => e.Field == "delivery.recipientPhone");
    }

    [Fact]
    public void Validate_RejectsPlaceholderRecipientNameAndPhone()
    {
        CheckoutRequest reqName = ValidRequest() with
        {
            Delivery = ValidRequest().Delivery! with { RecipientName = "Người thuê" }
        };
        Assert.Contains(CheckoutRequestValidator.Validate(reqName), e => e.Field == "delivery.recipientName" && e.Code == "INVALID_FORMAT");

        CheckoutRequest reqPhone = ValidRequest() with
        {
            Delivery = ValidRequest().Delivery! with { RecipientPhone = "0900000000" }
        };
        Assert.Contains(CheckoutRequestValidator.Validate(reqPhone), e => e.Field == "delivery.recipientPhone" && e.Code == "INVALID_FORMAT");
    }

    [Theory]
    [InlineData("0900000000")]
    [InlineData("+84900000000")]
    [InlineData("0000000000")]
    [InlineData("+84000000000")]
    [InlineData("0111111111")]
    [InlineData("0999999999")]
    public void Validate_RejectsPlaceholderRecipientPhone(string placeholderPhone)
    {
        CheckoutRequest request = ValidRequest();
        request = request with { Delivery = request.Delivery! with { RecipientPhone = placeholderPhone } };

        IReadOnlyList<CheckoutFieldError> errors = CheckoutRequestValidator.Validate(request);
        Assert.Contains(errors, e => e.Field == "delivery.recipientPhone" && e.Code == "INVALID_FORMAT");
    }

    [Theory]
    [InlineData("Người thuê")]
    [InlineData("nguoi thue")]
    [InlineData("NGUOI THUE")]
    [InlineData("NGƯỜI THUÊ")]
    [InlineData("Người Nhận")]
    [InlineData("nguoi nhan")]
    [InlineData("Khách hàng")]
    [InlineData("khach hang")]
    [InlineData("  Người nhận  ")]
    [InlineData("  nguoi   thue  ")]
    public void Validate_RejectsPlaceholderRecipientName(string placeholderName)
    {
        CheckoutRequest request = ValidRequest();
        request = request with { Delivery = request.Delivery! with { RecipientName = placeholderName } };

        IReadOnlyList<CheckoutFieldError> errors = CheckoutRequestValidator.Validate(request);
        Assert.Contains(errors, e => e.Field == "delivery.recipientName" && e.Code == "INVALID_FORMAT");
    }

    [Theory]
    [InlineData("Nguyễn Văn A")]
    [InlineData("Trần Thị B")]
    [InlineData("John Doe")]
    [InlineData("Phạm Hoàng Minh")]
    public void Validate_AcceptsLegitimateRecipientName(string legitimateName)
    {
        CheckoutRequest request = ValidRequest();
        request = request with { Delivery = request.Delivery! with { RecipientName = legitimateName } };

        Assert.Empty(CheckoutRequestValidator.Validate(request));
    }

    [Theory]
    [InlineData("0901234567")]
    [InlineData("+84901234567")]
    [InlineData("0987654321")]
    [InlineData("+84987654321")]
    public void Validate_AcceptsLegitimateRecipientPhone(string legitimatePhone)
    {
        CheckoutRequest request = ValidRequest();
        request = request with { Delivery = request.Delivery! with { RecipientPhone = legitimatePhone } };

        Assert.Empty(CheckoutRequestValidator.Validate(request));
    }

    [Fact]
    public void Validate_PickupDeliveryMethod_RejectsPlaceholderRecipientNameAndPhone()
    {
        CheckoutRequest reqName = ValidRequest() with
        {
            DeliveryMethod = "Pickup",
            Delivery = new(null, null, "nguoi nhan", "0901234567", "Tự tới lấy"),
        };
        Assert.Contains(CheckoutRequestValidator.Validate(reqName), e => e.Field == "delivery.recipientName" && e.Code == "INVALID_FORMAT");

        CheckoutRequest reqPhone = ValidRequest() with
        {
            DeliveryMethod = "Pickup",
            Delivery = new(null, null, "Nguyễn Văn B", "+84900000000", "Tự tới lấy"),
        };
        Assert.Contains(CheckoutRequestValidator.Validate(reqPhone), e => e.Field == "delivery.recipientPhone" && e.Code == "INVALID_FORMAT");
    }
}
