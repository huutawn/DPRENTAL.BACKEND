using Dprental.RentalService.Api.Tests.Returns;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Overdue;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Overdue;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Overdue;

public sealed class OverdueProtectionTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 16, 59, 59, TimeSpan.Zero));
    private string _connection = "";
    private string _database = "";

    public async Task InitializeAsync()
    {
        (_connection, _database) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        await using var db = NewDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(PostgresTestDatabaseHelper.BaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private RentalServiceDbContext NewDb() => new(PostgresTestDatabaseHelper.CreateOptions(_connection));

    private ServiceProvider Provider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _connection,
            ["Kafka:Enabled"] = "true"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IInventoryRentalClient>(Mock.Of<IInventoryRentalClient>());
        services.AddSingleton<IPaymentRentalClient>(Mock.Of<IPaymentRentalClient>());
        services.AddSingleton<IOrderLockManager>(new PostgresOrderLockManager(configuration, NullLogger<PostgresOrderLockManager>.Instance));
        services.AddDbContext<RentalServiceDbContext>(options => options.UseNpgsql(_connection));
        services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
        services.AddScoped<IOperationReceiptRepository, OperationReceiptRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
        services.AddScoped<IInventoryCommandProducer>(sp => new InventoryCommandProducer(
            sp.GetRequiredService<IOutboxRepository>(), Options.Create(new KafkaOptions { Enabled = true }), _clock));
        services.AddScoped<IRentalOperationExecutor, RentalOperationExecutor>();
        services.AddScoped<IOverdueProtectionService, OverdueProtectionService>();
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task DueHandedOverAllocation_IsProtectedByKafkaCommandInOutbox()
    {
        var order = ReturnTests.NewOrder();
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        await using (var db = NewDb())
        {
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        _clock.Advance(TimeSpan.FromSeconds(1));
        using var provider = Provider();
        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IOverdueProtectionService>();
            Assert.Equal(order.Id, Assert.Single(await service.GetDueOrderIdsAsync(10)));
            var result = await service.ProcessOrderAsync(order.Id);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
        }

        await using var verify = NewDb();
        var command = await verify.RentalOutboxMessages.SingleAsync(m =>
            m.MessageType == KafkaContractSpec.CommandTypes.MarkAssetsOverdue);
        Assert.Contains(unit.AllocationId.ToString(), command.Payload);
        var receipt = await verify.RentalOperationReceipts.SingleAsync(r => r.Action == RentalItem.OverdueProtectionAction);
        Assert.Equal(RentalOperationReceipt.StatusProcessing, receipt.Status);
        Assert.Equal(RentalOperationExecutor.ComputeOperationKey(RentalItem.OverdueProtectionAction, unit.Id.ToString("D")),
            receipt.InventoryOperationKey);
    }
}
