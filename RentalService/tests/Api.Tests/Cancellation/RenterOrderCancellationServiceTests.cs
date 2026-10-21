using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Cancellation;
using Dprental.RentalService.Common;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Cancellation;

public sealed class RenterOrderCancellationServiceTests
{
    [Fact]
    public async Task CancelOrder_DelegatesAuthenticatedRenterAndReason()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var caller = new Mock<IRentalCallerContext>();
        caller.SetupGet(c => c.IsAuthenticated).Returns(true);
        caller.SetupGet(c => c.UserId).Returns(renterId);

        var response = new CancelOrderResponse(orderId, "ORDER-1", "Cancelled", DateTimeOffset.UtcNow);
        var executor = new Mock<IRentalOperationExecutor>();
        executor.Setup(e => e.ExecuteRenterCancelOrderAsync(orderId, renterId, "Changed plans", "request-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<CancelOrderResponse>)response);

        var service = new RenterOrderCancellationService(executor.Object);
        Result<CancelOrderResponse> result = await service.CancelOrderAsync(
            orderId, new CancelOrderRequest("Changed plans"), "request-1", caller.Object);

        Assert.True(result.IsSuccess);
        Assert.Equal(response, result.Value);
        executor.VerifyAll();
    }

    [Fact]
    public async Task CancelOrder_RejectsServiceIdentityBeforeExecutor()
    {
        var caller = new Mock<IRentalCallerContext>();
        caller.SetupGet(c => c.IsAuthenticated).Returns(true);
        caller.SetupGet(c => c.IsService).Returns(true);
        caller.SetupGet(c => c.UserId).Returns(Guid.NewGuid());

        var executor = new Mock<IRentalOperationExecutor>();
        var service = new RenterOrderCancellationService(executor.Object);
        Result<CancelOrderResponse> result = await service.CancelOrderAsync(
            Guid.NewGuid(), null, "request-1", caller.Object);

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_REQUIRED", result.Error.Code);
        executor.VerifyNoOtherCalls();
    }
}
