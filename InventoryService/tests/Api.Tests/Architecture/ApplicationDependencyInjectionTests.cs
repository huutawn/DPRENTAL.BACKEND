using Dprental.InventoryService.Api.Controllers;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DependencyInjection;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Architecture;

public sealed class ApplicationDependencyInjectionTests
{
    [Fact]
    public void AddInventoryApplication_RegistersAllFourServiceInterfacesWithScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddInventoryApplication();

        var assetDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IInventoryAssetService));
        Assert.NotNull(assetDescriptor);
        Assert.Equal(typeof(InventoryAssetService), assetDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, assetDescriptor.Lifetime);

        var calendarDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ICalendarService));
        Assert.NotNull(calendarDescriptor);
        Assert.Equal(typeof(CalendarService), calendarDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, calendarDescriptor.Lifetime);

        var mediaDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IInventoryMediaService));
        Assert.NotNull(mediaDescriptor);
        Assert.Equal(typeof(InventoryMediaService), mediaDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, mediaDescriptor.Lifetime);
        var allocationDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IInventoryAllocationService));
        Assert.NotNull(allocationDescriptor);
        Assert.Equal(typeof(InventoryAllocationService), allocationDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, allocationDescriptor.Lifetime);
    }

    [Fact]
    public async Task Controllers_CanBeResolvedFromScopedProvider_UsingInterfacesOnly()
    {
        await using var factory = new InventorySecurityTestFactory();
        using var scope = factory.Services.CreateScope();

        // Xác nhận cả 3 interface đều resolve thành công từ DI container
        var assetService = scope.ServiceProvider.GetService<IInventoryAssetService>();
        Assert.NotNull(assetService);

        var calendarService = scope.ServiceProvider.GetService<ICalendarService>();
        Assert.NotNull(calendarService);

        var mediaService = scope.ServiceProvider.GetService<IInventoryMediaService>();
        Assert.NotNull(mediaService);
        var allocationService = scope.ServiceProvider.GetService<IInventoryAllocationService>();
        Assert.NotNull(allocationService);


        // Khẳng định Controllers kích hoạt thành công phụ thuộc vào interfaces
        var assetController = ActivatorUtilities.CreateInstance<InventoryAssetsController>(scope.ServiceProvider);
        Assert.NotNull(assetController);

        var calendarController = ActivatorUtilities.CreateInstance<InventoryCalendarController>(scope.ServiceProvider);
        Assert.NotNull(calendarController);
        var allocationController = ActivatorUtilities.CreateInstance<InventoryAllocationsController>(scope.ServiceProvider);
        Assert.NotNull(allocationController);
    }
}
