using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

[Collection("PostgreSql")]
public sealed class ReturnRequestPersistencePostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
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
    public async Task ReturnRequest_RoundTrip_PersistsAndLoadsAllProperties()
    {
        var order = ReturnTests.NewOrder(3);
        var item = order.Items.Single();
        var assignment1 = item.UnitAssignments.ElementAt(0);
        var assignment2 = item.UnitAssignments.ElementAt(1);

        Guid requestId = Guid.NewGuid();
        Guid unitReq1Id = Guid.NewGuid();
        Guid unitReq2Id = Guid.NewGuid();
        Guid media1 = Guid.NewGuid();
        Guid media2 = Guid.NewGuid();
        Guid media3 = Guid.NewGuid();

        var unitReq1 = new ReturnRequestUnit(unitReq1Id, requestId, assignment1.Id, [media1, media2]);
        var unitReq2 = new ReturnRequestUnit(unitReq2Id, requestId, assignment2.Id, [media3]);
        var returnRequest = new ReturnRequest(
            requestId, item.Id, Now.AddHours(-1), "Roundtrip test note", order.RenterId, Now, [unitReq1, unitReq2]);
        item.AddReturnRequest(returnRequest);

        await using (var db = NewDb())
        {
            db.Add(order);
            await db.SaveChangesAsync();
        }

        await using (var verifyDb = NewDb())
        {
            var loadedRequest = await verifyDb.ReturnRequests
                .Include(r => r.Units)
                .FirstOrDefaultAsync(r => r.Id == requestId);

            Assert.NotNull(loadedRequest);
            Assert.Equal(requestId, loadedRequest.Id);
            Assert.Equal(item.Id, loadedRequest.RentalItemId);
            Assert.Equal(Now.AddHours(-1), loadedRequest.ActualReturnAt);
            Assert.Equal("Roundtrip test note", loadedRequest.Note);
            Assert.Equal(order.RenterId, loadedRequest.SubmittedById);
            Assert.Equal(Now, loadedRequest.SubmittedAt);
            Assert.Equal(2, loadedRequest.Units.Count);

            var u1 = loadedRequest.Units.Single(u => u.Id == unitReq1Id);
            Assert.Equal(assignment1.Id, u1.RentalUnitAssignmentId);
            Assert.Null(u1.ClosedAt);
            Assert.Equal(2, u1.MediaIds.Count);
            Assert.Contains(media1, u1.MediaIds);
            Assert.Contains(media2, u1.MediaIds);

            var u2 = loadedRequest.Units.Single(u => u.Id == unitReq2Id);
            Assert.Equal(assignment2.Id, u2.RentalUnitAssignmentId);
            Assert.Null(u2.ClosedAt);
            Assert.Single(u2.MediaIds);
            Assert.Equal(media3, u2.MediaIds[0]);
        }
    }

    [Fact]
    public async Task ReturnRequest_AggregateTracking_ViaOrderRepository()
    {
        var order = ReturnTests.NewOrder(2);
        var item = order.Items.Single();
        var assignment = item.UnitAssignments.First();

        await using (var seedDb = NewDb())
        {
            seedDb.Add(order);
            await seedDb.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var repo = new RentalOrderRepository(db);
            var loadedOrder = await repo.GetByIdWithDetailsAsync(order.Id);
            Assert.NotNull(loadedOrder);

            var loadedItem = loadedOrder.Items.Single();
            Assert.Empty(loadedItem.ReturnRequests);

            Guid reqId = Guid.NewGuid();
            var unitReq = new ReturnRequestUnit(Guid.NewGuid(), reqId, assignment.Id, []);
            var req = new ReturnRequest(reqId, loadedItem.Id, Now.AddHours(-2), "Via repository", loadedOrder.RenterId, Now, [unitReq]);
            loadedItem.AddReturnRequest(req);
            loadedOrder.RecordReadModelUpdate(Now);

            await repo.UpdateAsync(loadedOrder);
            await db.SaveChangesAsync();
        }

        await using (var verifyDb = NewDb())
        {
            var repo = new RentalOrderRepository(verifyDb);
            var reloadedOrder = await repo.GetByIdWithDetailsAsync(order.Id);
            Assert.NotNull(reloadedOrder);
            var reloadedItem = reloadedOrder.Items.Single();
            Assert.Single(reloadedItem.ReturnRequests);
            var loadedReq = reloadedItem.ReturnRequests.Single();
            Assert.Equal("Via repository", loadedReq.Note);
            Assert.Single(loadedReq.Units);
            Assert.Equal(assignment.Id, loadedReq.Units.Single().RentalUnitAssignmentId);
        }
    }

    [Fact]
    public async Task ReturnRequestUnit_UniqueIndex_PreventsConcurrentOpenRequestsForSameUnit()
    {
        var order = ReturnTests.NewOrder(2);
        var item = order.Items.Single();
        var assignment = item.UnitAssignments.First();

        await using (var seedDb = NewDb())
        {
            seedDb.Add(order);
            await seedDb.SaveChangesAsync();
        }

        await using var db1 = NewDb();
        await using var db2 = NewDb();

        Guid req1Id = Guid.NewGuid();
        var req1 = new ReturnRequest(req1Id, item.Id, Now.AddHours(-1), "Req 1", order.RenterId, Now,
            [new ReturnRequestUnit(Guid.NewGuid(), req1Id, assignment.Id, [])]);
        db1.Add(req1);
        await db1.SaveChangesAsync();

        Guid req2Id = Guid.NewGuid();
        var req2 = new ReturnRequest(req2Id, item.Id, Now.AddHours(-1), "Req 2", order.RenterId, Now,
            [new ReturnRequestUnit(Guid.NewGuid(), req2Id, assignment.Id, [])]);
        db2.Add(req2);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        Assert.NotNull(ex.InnerException);
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23505", ((PostgresException)ex.InnerException).SqlState);
    }

    [Fact]
    public async Task ReturnRequestUnit_UniqueIndex_AllowsNewRequest_WhenPreviousRequestClosed()
    {
        var order = ReturnTests.NewOrder(2);
        var item = order.Items.Single();
        var assignment = item.UnitAssignments.First();

        await using (var seedDb = NewDb())
        {
            seedDb.Add(order);
            await seedDb.SaveChangesAsync();
        }

        Guid req1Id = Guid.NewGuid();
        var unitReq1 = new ReturnRequestUnit(Guid.NewGuid(), req1Id, assignment.Id, []);
        var req1 = new ReturnRequest(req1Id, item.Id, Now.AddHours(-2), "Req 1", order.RenterId, Now.AddHours(-2), [unitReq1]);

        await using (var db = NewDb())
        {
            db.Add(req1);
            await db.SaveChangesAsync();
        }

        // Close unitReq1
        await using (var closeDb = NewDb())
        {
            var loadedUnit = await closeDb.ReturnRequestUnits.SingleAsync(u => u.Id == unitReq1.Id);
            loadedUnit.SetClosedAt(Now.AddHours(-1));
            await closeDb.SaveChangesAsync();
        }
        // Now create a new request for the same assignment
        Guid req2Id = Guid.NewGuid();
        var unitReq2 = new ReturnRequestUnit(Guid.NewGuid(), req2Id, assignment.Id, []);
        var req2 = new ReturnRequest(req2Id, item.Id, Now.AddMinutes(-10), "Req 2", order.RenterId, Now, [unitReq2]);

        await using (var db2 = NewDb())
        {
            db2.Add(req2);
            await db2.SaveChangesAsync();
        }

        await using (var verifyDb = NewDb())
        {
            var units = await verifyDb.ReturnRequestUnits
                .Where(u => u.RentalUnitAssignmentId == assignment.Id)
                .OrderBy(u => u.ClosedAt)
                .ToListAsync();

            Assert.Equal(2, units.Count);
            Assert.Contains(units, u => u.ClosedAt != null);
            Assert.Contains(units, u => u.ClosedAt == null);
        }
    }

    [Fact]
    public async Task ReturnRequest_DiscardChanges_RollsBackAllTrackedEntities()
    {
        var order = ReturnTests.NewOrder(2);
        var item = order.Items.Single();
        var assignment = item.UnitAssignments.First();

        await using (var seedDb = NewDb())
        {
            seedDb.Add(order);
            await seedDb.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var repo = new RentalOrderRepository(db);
            var loadedOrder = await repo.GetByIdWithDetailsAsync(order.Id);
            Assert.NotNull(loadedOrder);

            Guid reqId = Guid.NewGuid();
            var unitReq = new ReturnRequestUnit(Guid.NewGuid(), reqId, assignment.Id, []);
            var req = new ReturnRequest(reqId, item.Id, Now.AddHours(-1), "Rollback note", order.RenterId, Now, [unitReq]);
            loadedOrder.Items.Single().AddReturnRequest(req);
            loadedOrder.AddOperationReceipt(new RentalOperationReceipt(
                Guid.NewGuid(), order.Id, "rollback-op", "RequestReturn", "fp", true, null, null, Now,
                targetItemId: item.Id, payloadJson: "{}", apiResponseJson: "{}"));

            await repo.UpdateAsync(loadedOrder);

            // Discard before save
            db.DiscardChanges();
            await db.SaveChangesAsync();
        }

        await using (var verifyDb = NewDb())
        {
            Assert.Empty(await verifyDb.ReturnRequests.ToListAsync());
            Assert.Empty(await verifyDb.ReturnRequestUnits.ToListAsync());
            Assert.Empty(await verifyDb.RentalOperationReceipts.Where(r => r.OperationId == "rollback-op").ToListAsync());
        }
    }
}
