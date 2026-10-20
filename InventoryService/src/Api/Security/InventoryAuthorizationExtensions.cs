using Dprental.InventoryService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.InventoryService.Api.Security;

/// <summary>
/// Mở rộng IServiceCollection đăng ký Authorization policies và handlers cho Inventory Service.
/// </summary>
public static class InventoryAuthorizationExtensions
{
    public static IServiceCollection AddInventoryAuthorization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        // Đăng ký Access Evaluator và Caller Context
        services.AddScoped<IInventoryCallerContext, InventoryCallerContext>();

        // Đăng ký Authorization Handler
        services.AddScoped<IAuthorizationHandler, InventoryAuthorizationHandler>();

        // Đăng ký 5 Policies chuẩn theo DPR-147
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy(InventoryPolicies.AssetManage, policy =>
                policy.RequireAuthenticatedUser()
                      .AddRequirements(new AssetManageRequirement()));

            options.AddPolicy(InventoryPolicies.CalendarRead, policy =>
                policy.RequireAuthenticatedUser()
                      .AddRequirements(new CalendarReadRequirement()));

            options.AddPolicy(InventoryPolicies.AvailabilityRead, policy =>
                policy.RequireAuthenticatedUser()
                      .AddRequirements(new AvailabilityReadRequirement()));

            options.AddPolicy(InventoryPolicies.AllocationWrite, policy =>
                policy.RequireAuthenticatedUser()
                      .AddRequirements(new AllocationWriteRequirement()));

            options.AddPolicy(InventoryPolicies.ManualAllocation, policy =>
                policy.RequireAuthenticatedUser()
                      .AddRequirements(new ManualAllocationRequirement()));

            options.AddPolicy(InventoryPolicies.EquipmentListingBatchRead, policy =>
                policy.RequireAuthenticatedUser()
                      .AddRequirements(new EquipmentListingBatchReadRequirement()));
        });

        return services;
    }
}
