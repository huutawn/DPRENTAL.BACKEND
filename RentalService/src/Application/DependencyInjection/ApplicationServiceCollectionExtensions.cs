using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Application.Services.Cart;
using System;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.RentalService.DependencyInjection; // Điều chỉnh namespace theo chuẩn project của bạn

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddRentalServiceApplication(this IServiceCollection services)
    {
        // Đăng ký Use Cases
        services.AddScoped<RentalCartUseCases>();

        // Đăng ký Read Models / Query Handlers
        services.AddScoped<GetRentalCart>();

        // Đăng ký các bước checkout
        services.AddScoped<ICheckoutPreparationService, CheckoutPreparationService>();
        services.AddScoped<ICheckoutHoldOrchestrator, CheckoutHoldOrchestrator>();
        services.AddScoped<ICheckoutOrderWriter, CheckoutOrderWriter>();
        services.AddScoped<ICheckoutInvoiceIssuer, CheckoutInvoiceIssuer>();
        services.AddScoped<ICheckoutService, CheckoutService>();

        // Đăng ký TimeProvider chung
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}