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
            ["cartItemIds", "delivery", "expectedTotalAmount", "refundAccount", "termsAccepted", "termsVersion"],
            errors.Select(e => e.Field).Order(StringComparer.Ordinal));
    }
}
