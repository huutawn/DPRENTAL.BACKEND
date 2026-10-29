using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Cancellation;

public sealed class RenterOrderCancellationPolicyTests
{
    [Fact]
    public void PendingPayment_CanCancel() =>
        Assert.True(RenterOrderCancellationPolicy.CanCancel(
            RentalOrderStatus.PendingPayment, [RentalItemStatus.PendingPayment]));

    [Fact]
    public void Paid_EveryActiveItemAwaitingConfirmation_CanCancel() =>
        Assert.True(RenterOrderCancellationPolicy.CanCancel(
            RentalOrderStatus.Paid,
            [RentalItemStatus.PendingSupplierConfirmation, RentalItemStatus.Rejected, RentalItemStatus.PendingSupplierConfirmation]));

    [Fact]
    public void Paid_SomeItemAlreadyAccepted_CannotCancel() =>
        Assert.False(RenterOrderCancellationPolicy.CanCancel(
            RentalOrderStatus.Paid,
            [RentalItemStatus.PendingSupplierConfirmation, RentalItemStatus.AwaitingHandover]));

    [Theory]
    [InlineData(RentalOrderStatus.AwaitingHandover)]
    [InlineData(RentalOrderStatus.InRental)]
    [InlineData(RentalOrderStatus.Completed)]
    [InlineData(RentalOrderStatus.Cancelled)]
    [InlineData(RentalOrderStatus.Expired)]
    [InlineData(RentalOrderStatus.Rejected)]
    public void OrderPastConfirmationOrEnded_CannotCancel(RentalOrderStatus status) =>
        Assert.False(RenterOrderCancellationPolicy.CanCancel(status, [RentalItemStatus.PendingSupplierConfirmation]));
}
