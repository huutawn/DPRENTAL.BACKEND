using System.IO;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Maintenance;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

[Collection("PostgreSql")]
public sealed class ReturnRequestMaintenanceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
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

    [Fact]
    public async Task DryRun_WhenNoUnclosedRequests_ReturnsZero()
    {
        var dbOptions = PostgresTestDatabaseHelper.CreateOptions(_connection);
        using var sw = new StringWriter();
        var count = await Dprental.RentalService.Maintenance.Program.RunReconcileReturnRequestsAsync(dbOptions, apply: false, output: sw);

        Assert.Equal(0, count);
        Assert.Contains("Found 0 unclosed return request unit(s)", sw.ToString());
    }

    [Fact]
    public async Task Maintenance_DryRunAndApply_ReconcilesIdempotentlyWithBatching()
    {
        var dbOptions = PostgresTestDatabaseHelper.CreateOptions(_connection);
        var order = ReturnTests.NewOrder(3);
        var item = order.Items.Single();
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);
        var u3 = item.UnitAssignments.ElementAt(2);

        // Record return for u1 and u2 (u3 is NOT returned)
        var submittedAt1 = Now.AddHours(-3);
        var retRec1 = new ReturnRecord(Guid.NewGuid(), item.Id, Now.AddHours(-4), "Return u1",
            item.SupplierId, submittedAt1);
        u1.RecordReturn(retRec1.Id, UnitCondition.Normal, "Good", [], null, null);
        item.AddReturnRecord(retRec1);

        var submittedAt2 = Now.AddHours(-2);
        var retRec2 = new ReturnRecord(Guid.NewGuid(), item.Id, Now.AddHours(-3), "Return u2",
            item.SupplierId, submittedAt2);
        u2.RecordReturn(retRec2.Id, UnitCondition.Normal, "Good", [], null, null);
        item.AddReturnRecord(retRec2);

        // Add return request with units:
        // - reqUnit1 for u1 (ClosedAt is null, but u1 has retRec1) -> eligible!
        // - reqUnit2 for u2 (ClosedAt is null, but u2 has retRec2) -> eligible!
        // - reqUnit3 for u3 (ClosedAt is null, u3 has NO return record) -> ineligible!
        // - reqUnitAlreadyClosed for u1 (ClosedAt already set) -> already closed!
        var reqUnit1 = new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u1.Id, [Guid.NewGuid()]);
        var reqUnit2 = new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u2.Id, [Guid.NewGuid()]);
        var reqUnit3 = new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u3.Id, [Guid.NewGuid()]);
        var reqUnitAlreadyClosed = new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u1.Id, [Guid.NewGuid()]);
        reqUnitAlreadyClosed.SetClosedAt(Now.AddHours(-5));

        var req1 = new ReturnRequest(reqUnit1.ReturnRequestId, item.Id, Now.AddHours(-5), "Request 1",
            order.RenterId, Now.AddHours(-5), [reqUnit1, reqUnitAlreadyClosed]);
        var req2 = new ReturnRequest(reqUnit2.ReturnRequestId, item.Id, Now.AddHours(-4), "Request 2",
            order.RenterId, Now.AddHours(-4), [reqUnit2, reqUnit3]);

        item.AddReturnRequest(req1);
        item.AddReturnRequest(req2);

        await using (var db = NewDb())
        {
            db.Add(order);
            await db.SaveChangesAsync();
        }

        // 1. Dry run: should count exactly 2 eligible units (reqUnit1 and reqUnit2)
        using var dryRunSw = new StringWriter();
        var dryRunCount = await Dprental.RentalService.Maintenance.Program.RunReconcileReturnRequestsAsync(dbOptions, apply: false, batchSize: 1, output: dryRunSw);

        Assert.Equal(2, dryRunCount);
        Assert.Contains("Found 2 unclosed return request unit(s)", dryRunSw.ToString());
        Assert.Contains("Dry-run complete. No changes were made.", dryRunSw.ToString());

        // Verify database: no units were modified in dry-run
        await using (var verifyDb = NewDb())
        {
            var units = await verifyDb.ReturnRequestUnits.ToDictionaryAsync(u => u.Id);
            Assert.Null(units[reqUnit1.Id].ClosedAt);
            Assert.Null(units[reqUnit2.Id].ClosedAt);
            Assert.Null(units[reqUnit3.Id].ClosedAt);
            Assert.Equal(Now.AddHours(-5), units[reqUnitAlreadyClosed.Id].ClosedAt);
        }

        // 2. Apply with batchSize = 1 to test multi-batch iteration and transaction per batch
        using var applySw = new StringWriter();
        var applyUpdatedCount = await Dprental.RentalService.Maintenance.Program.RunReconcileReturnRequestsAsync(dbOptions, apply: true, batchSize: 1, output: applySw);

        Assert.Equal(2, applyUpdatedCount);
        Assert.Contains("Reconciliation complete. Total updated: 2 unit(s).", applySw.ToString());

        // Verify database after apply:
        await using (var verifyDb = NewDb())
        {
            var units = await verifyDb.ReturnRequestUnits.ToDictionaryAsync(u => u.Id);

            // Reconciled units have ClosedAt set to ReturnRecord.SubmittedAt
            Assert.Equal(submittedAt1, units[reqUnit1.Id].ClosedAt);
            Assert.Equal(submittedAt2, units[reqUnit2.Id].ClosedAt);

            // Ineligible unit (u3 has no return record) remains untouched
            Assert.Null(units[reqUnit3.Id].ClosedAt);

            // Already closed unit was untouched
            Assert.Equal(Now.AddHours(-5), units[reqUnitAlreadyClosed.Id].ClosedAt);

            // Evidence and note remain intact
            Assert.Single(units[reqUnit1.Id].MediaIds);
            Assert.Single(units[reqUnit2.Id].MediaIds);
        }

        // 3. Replay: running apply again should find 0 units to reconcile (idempotent)
        using var replaySw = new StringWriter();
        var replayUpdatedCount = await Dprental.RentalService.Maintenance.Program.RunReconcileReturnRequestsAsync(dbOptions, apply: true, batchSize: 10, output: replaySw);

        Assert.Equal(0, replayUpdatedCount);
        Assert.Contains("Found 0 unclosed return request unit(s)", replaySw.ToString());

        // 4. Dry-run after apply also reports 0
        using var secondDryRunSw = new StringWriter();
        var secondDryRunCount = await Dprental.RentalService.Maintenance.Program.RunReconcileReturnRequestsAsync(dbOptions, apply: false, output: secondDryRunSw);

        Assert.Equal(0, secondDryRunCount);
    }

    [Fact]
    public async Task Main_ReconcileCommand_ReturnsZeroExitCode_BothDryRunAndApply()
    {
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", _connection);
        try
        {
            int dryRunExit = await Dprental.RentalService.Maintenance.Program.Main(["reconcile-return-requests"]);
            Assert.Equal(0, dryRunExit);

            int applyExit = await Dprental.RentalService.Maintenance.Program.Main(["reconcile-return-requests", "--apply"]);
            Assert.Equal(0, applyExit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", null);
        }
    }

    [Fact]
    public async Task Main_DatabaseFailure_ReturnsNonZeroExitCode()
    {
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", "Host=invalid-host-that-does-not-exist;Port=5433;Database=rentalservice;Username=postgres;Password=postgres;Timeout=1");
        try
        {
            int failExit = await Dprental.RentalService.Maintenance.Program.Main(["reconcile-return-requests"]);
            Assert.Equal(1, failExit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", null);
        }
    }
}
