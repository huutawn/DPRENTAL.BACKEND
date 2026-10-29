using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.IServices.Refunds;
using Dprental.RentalService.Application.Services.Refunds;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.RentalService.Maintenance;

public static partial class Program
{
    public static async Task<int> RunRefundReconciliationAsync(IConfiguration config, string connectionString, bool apply)
    {
        IConfiguration effectiveConfig = new ConfigurationBuilder().AddConfiguration(config)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString }).Build();
        var services = new ServiceCollection();
        services.AddSingleton(effectiveConfig);
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddRentalServiceDomainRepositories(connectionString);
        services.AddHttpClient<IPaymentRentalClient, HttpPaymentRentalClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<IAdminRefundCommandService, AdminRefundCommandService>();
        await using var provider = services.BuildServiceProvider();
        return await ReconcileRefundsAsync(provider, apply);
    }

    public static async Task<int> ReconcileRefundsAsync(IServiceProvider provider, bool apply,
        CancellationToken ct = default)
    {
        Console.WriteLine($"Refund confirmations: {(apply ? "APPLY" : "DRY-RUN")}. No bank transfers are initiated.");
        Guid? cursor = null;
        int failures = 0, changes = 0, inspected = 0;
        while (true)
        {
            using var pageScope = provider.CreateScope();
            var ids = await pageScope.ServiceProvider.GetRequiredService<IRentalRefundRepository>()
                .GetReconciliationItemIdsAsync(cursor, 100, ct);
            if (ids.Count == 0) break;
            cursor = ids[^1];
            foreach (var id in ids)
            {
                using var itemScope = provider.CreateScope();
                var service = itemScope.ServiceProvider.GetRequiredService<IAdminRefundCommandService>();
                try
                {
                    var result = await service.ReconcileAsync(id, apply, ct);
                    inspected++;
                    if (result.IsFailure)
                    {
                        failures++;
                        Console.WriteLine($"Item {id}: REVIEW_REQUIRED {result.Error.Code}");
                    }
                    else if (result.Value)
                    {
                        changes++;
                        Console.WriteLine($"Item {id}: {(apply ? "RECONCILED" : "WOULD_RECONCILE")}");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    failures++;
                    Console.WriteLine($"Item {id}: FAILED {exception.GetType().Name}");
                }
            }
        }
        Console.WriteLine($"Inspected={inspected}; changes={changes}; reviewRequired={failures}.");
        return failures == 0 ? 0 : 1;
    }
}
