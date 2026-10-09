using System;
using Dprental.RentalService.Application.Cart;
using Dprental.RentalService.Application.Cart.ReadModel;
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

        // Đăng ký TimeProvider chung
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}