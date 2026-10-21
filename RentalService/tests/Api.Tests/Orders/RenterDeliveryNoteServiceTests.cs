using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

public sealed class RenterDeliveryNoteServiceTests
{
    private readonly string _dbName = $"DeliveryNote_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static RenterDeliveryNoteService Service(RentalServiceDbContext context) =>
        new(new RentalOrderRepository(context), context, new FixedTimeProvider(OrderTestData.Now.AddHours(1)));

    private async Task<RentalOrder> SeedAsync(bool handedOver = false)
    {
        RentalOrder order = OrderTestData.Order(_renterId, "DH-2026-000321", OrderTestData.Now,
            OrderTestData.Line(_supplierId, "Máy xúc Komatsu PC200", "Máy xúc", "Komatsu", 1, 3_000_000m, 20_000_000m));
        if (handedOver)
        {
            OrderTestData.HandOver(order.Items.Single(), OrderTestData.Now);
        }

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    private async Task<string?> LoadNoteAsync(Guid orderId)
    {
        await using RentalServiceDbContext context = NewContext();
        return (await context.RentalOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).DeliveryNote;
    }

    [Fact]
    public async Task Update_SavesTrimmedNote_AndReturnsDelivery()
    {
        RentalOrder order = await SeedAsync();

        await using RentalServiceDbContext context = NewContext();
        Result<OrderDeliveryResponse> result = await Service(context).UpdateDeliveryNoteAsync(
            order.Id, new UpdateDeliveryNoteRequest("  Gọi trước 30 phút. Vào cổng số 3.  "), OrderTestData.User(_renterId));

        Assert.True(result.IsSuccess);
        Assert.Equal("Gọi trước 30 phút. Vào cổng số 3.", result.Value.Note);
        Assert.Equal("Gọi trước 30 phút. Vào cổng số 3.", await LoadNoteAsync(order.Id));
    }

    [Fact]
    public async Task Update_WithEmptyNote_ClearsTheNote()
    {
        RentalOrder order = await SeedAsync();

        await using RentalServiceDbContext context = NewContext();
        Result<OrderDeliveryResponse> result = await Service(context).UpdateDeliveryNoteAsync(
            order.Id, new UpdateDeliveryNoteRequest("   "), OrderTestData.User(_renterId));

        Assert.True(result.IsSuccess);
        Assert.Null(await LoadNoteAsync(order.Id));
    }

    [Fact]
    public async Task AnotherRenter_GetsNotFound_AndTheNoteStays()
    {
        RentalOrder order = await SeedAsync();

        await using RentalServiceDbContext context = NewContext();
        Result<OrderDeliveryResponse> result = await Service(context).UpdateDeliveryNoteAsync(
            order.Id, new UpdateDeliveryNoteRequest("Ghi chú lạ"), OrderTestData.User(Guid.NewGuid()));

        Assert.Equal(OrderQueryErrors.OrderNotFound, result.Error);
        Assert.Equal("Giao trước 7 giờ sáng", await LoadNoteAsync(order.Id));
    }

    [Fact]
    public async Task NoteLongerThan500_IsRejected()
    {
        RentalOrder order = await SeedAsync();

        await using RentalServiceDbContext context = NewContext();
        Result<OrderDeliveryResponse> result = await Service(context).UpdateDeliveryNoteAsync(
            order.Id, new UpdateDeliveryNoteRequest(new string('a', 501)), OrderTestData.User(_renterId));

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("Giao trước 7 giờ sáng", await LoadNoteAsync(order.Id));
    }

    [Fact]
    public async Task AfterHandover_TheNoteIsLocked()
    {
        RentalOrder order = await SeedAsync(handedOver: true);

        await using RentalServiceDbContext context = NewContext();
        Result<OrderDeliveryResponse> result = await Service(context).UpdateDeliveryNoteAsync(
            order.Id, new UpdateDeliveryNoteRequest("Muộn rồi"), OrderTestData.User(_renterId));

        Assert.Equal("Order.DeliveryNoteLocked", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task ServiceToken_IsRejected()
    {
        RentalOrder order = await SeedAsync();

        await using RentalServiceDbContext context = NewContext();
        Result<OrderDeliveryResponse> result = await Service(context).UpdateDeliveryNoteAsync(
            order.Id, new UpdateDeliveryNoteRequest("Ghi chú"), OrderTestData.Service());

        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }
}
