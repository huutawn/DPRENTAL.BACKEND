using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

public sealed class ReturnQueryAndEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    private static (RentalOrder Order, RentalItem Item, RentalUnitAssignment Assignment, HandoverRecord Handover)
        CreateTestOrder(RentalItemStatus itemStatus = RentalItemStatus.InRental)
    {
        var order = ReturnTests.NewOrder(2);
        var item = order.Items.Single();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, itemStatus);
        var assignment = item.UnitAssignments.First();
        var handover = item.HandoverRecord!;
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.PaymentConfirmedAt))!.SetValue(order, Now.AddDays(-2));
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, itemStatus);
        return (order, item, assignment, handover);
    }

    [Fact]
    public void CanRequestReturn_WhenItemNotInRental_ReturnsFalse()
    {
        var (_, item, assignment, _) = CreateTestOrder(RentalItemStatus.AwaitingHandover);
        Assert.False(ReturnEligibilityPolicy.CanRequestReturn(item, assignment));

        var val = ReturnEligibilityPolicy.ValidateAssignmentForReturnRequest(
            item, assignment, Now, new HashSet<Guid>());
        Assert.Equal(RentalErrors.InvalidItemStatusForReturn.Code, val.Error.Code);
    }

    [Fact]
    public void CanRequestReturn_WhenAssignmentNotHandedOver_ReturnsFalse()
    {
        var (_, item, assignment, _) = CreateTestOrder();
        typeof(RentalUnitAssignment).GetProperty(nameof(RentalUnitAssignment.Status))!.SetValue(assignment, RentalUnitAssignmentStatus.Assigned);
        Assert.False(ReturnEligibilityPolicy.CanRequestReturn(item, assignment));

        var val = ReturnEligibilityPolicy.ValidateAssignmentForReturnRequest(
            item, assignment, Now, new HashSet<Guid>());
        Assert.Equal(RentalErrors.AssetNotHandedOver(assignment.Id).Code, val.Error.Code);
    }

    [Fact]
    public void CanRequestReturn_WhenAssignmentAlreadyReturned_ReturnsFalse()
    {
        var (_, item, assignment, _) = CreateTestOrder();
        var returnRecId = Guid.NewGuid();
        typeof(RentalUnitAssignment).GetProperty(nameof(RentalUnitAssignment.ReturnRecordId))!.SetValue(assignment, returnRecId);
        Assert.False(ReturnEligibilityPolicy.CanRequestReturn(item, assignment));

        var val = ReturnEligibilityPolicy.ValidateAssignmentForReturnRequest(
            item, assignment, Now, new HashSet<Guid>());
        Assert.Equal(RentalErrors.AssetAlreadyReturned(assignment.Id).Code, val.Error.Code);
    }

    [Fact]
    public void CanRequestReturn_WhenReturnTimeBeforeHandover_ReturnsValidationError()
    {
        var (_, item, assignment, handover) = CreateTestOrder();
        var val = ReturnEligibilityPolicy.ValidateAssignmentForReturnRequest(
            item, assignment, handover.ActualHandoverAt.AddMinutes(-5), new HashSet<Guid>());
        Assert.Equal(RentalErrors.InvalidReturnTimeBeforeHandover(assignment.Id).Code, val.Error.Code);
    }

    [Fact]
    public void CanRequestReturn_WhenOpenReturnRequestExists_ReturnsFalse()
    {
        var (_, item, assignment, _) = CreateTestOrder();
        var req = new ReturnRequest(Guid.NewGuid(), item.Id, Now, "Returning unit",
            Guid.NewGuid(), Now, [new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), assignment.Id, [])]);
        item.AddReturnRequest(req);

        Assert.True(ReturnEligibilityPolicy.HasOpenReturnRequest(item, assignment.Id));
        Assert.False(ReturnEligibilityPolicy.CanRequestReturn(item, assignment));

        var openUnits = ReturnEligibilityPolicy.GetOpenReturnRequestAssignmentIds(item);
        Assert.Contains(assignment.Id, openUnits);

        var val = ReturnEligibilityPolicy.ValidateAssignmentForReturnRequest(item, assignment, Now, openUnits);
        Assert.Equal("RETURN_REQUEST_ALREADY_OPEN", val.Error.Code);
    }

    [Fact]
    public void CanRequestReturn_WhenRequestUnitClosed_AndNoOtherOpenRequest_ReturnsTrue()
    {
        var (_, item, assignment, _) = CreateTestOrder();
        var reqUnit = new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), assignment.Id, []);
        reqUnit.SetClosedAt(Now.AddMinutes(-10));
        var req = new ReturnRequest(Guid.NewGuid(), item.Id, Now.AddHours(-1), "Previous request",
            Guid.NewGuid(), Now.AddHours(-1), [reqUnit]);
        item.AddReturnRequest(req);

        Assert.False(ReturnEligibilityPolicy.HasOpenReturnRequest(item, assignment.Id));
        Assert.True(ReturnEligibilityPolicy.CanRequestReturn(item, assignment));

        var openUnits = ReturnEligibilityPolicy.GetOpenReturnRequestAssignmentIds(item);
        Assert.Empty(openUnits);
    }

    [Fact]
    public void ToRenterUnits_MapsPropertiesAndExcludesInternalIds_StablySorted()
    {
        var (_, item, u1, _) = CreateTestOrder();
        var u2 = item.UnitAssignments.ElementAt(1);

        // Add open request for u1
        var req = new ReturnRequest(Guid.NewGuid(), item.Id, Now, "Unit 1 request",
            Guid.NewGuid(), Now, [new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u1.Id, [])]);
        item.AddReturnRequest(req);

        var units = OrderQueryMapper.ToRenterUnits(item);
        Assert.Equal(2, units.Count);

        // Sorted by AssignmentId
        Assert.True(units[0].AssignmentId.CompareTo(units[1].AssignmentId) <= 0);

        var mappedU1 = units.Single(u => u.AssignmentId == u1.Id);
        Assert.True(mappedU1.HasOpenReturnRequest);
        Assert.False(mappedU1.CanRequestReturn);

        var mappedU2 = units.Single(u => u.AssignmentId == u2.Id);
        Assert.False(mappedU2.HasOpenReturnRequest);
        Assert.True(mappedU2.CanRequestReturn);
    }

    [Fact]
    public void ToReturnRequests_SortsRequestsBySubmittedAtThenId_AndUnitsByAssignmentId()
    {
        var (_, item, u1, _) = CreateTestOrder();
        var u2 = item.UnitAssignments.ElementAt(1);

        var media1 = Guid.NewGuid();
        var media2 = Guid.NewGuid();

        var req2 = new ReturnRequest(Guid.NewGuid(), item.Id, Now, "Second request",
            Guid.NewGuid(), Now.AddMinutes(10),
            [new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u2.Id, [media2])]);

        var req1 = new ReturnRequest(Guid.NewGuid(), item.Id, Now.AddMinutes(-5), "First request",
            Guid.NewGuid(), Now.AddMinutes(5),
            [
                new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u2.Id, [media2]),
                new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u1.Id, [media1])
            ]);

        item.AddReturnRequest(req2);
        item.AddReturnRequest(req1);

        var mapped = OrderQueryMapper.ToReturnRequests(item);
        Assert.Equal(2, mapped.Count);
        Assert.Equal(req1.Id, mapped[0].ReturnRequestId);
        Assert.Equal(req2.Id, mapped[1].ReturnRequestId);

        // Units inside req1 sorted by AssignmentId
        Assert.Equal(2, mapped[0].Units.Count);
        Assert.True(mapped[0].Units[0].RentalUnitAssignmentId.CompareTo(mapped[0].Units[1].RentalUnitAssignmentId) <= 0);
    }

    [Fact]
    public void LegacyReceiptJson_WithoutClosedAt_DeserializesWithClosedAtNull()
    {
        Guid reqId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid assignId = Guid.NewGuid();
        Guid mediaId = Guid.NewGuid();

        string legacyReceiptJson = $$"""
        {
            "ReturnRequestId": "{{reqId}}",
            "ItemId": "{{itemId}}",
            "ActualReturnAt": "2026-10-28T10:00:00Z",
            "Note": "Legacy receipt note",
            "SubmittedAt": "2026-10-28T10:05:00Z",
            "Units": [
                {
                    "RentalUnitAssignmentId": "{{assignId}}",
                    "MediaIds": ["{{mediaId}}"]
                }
            ]
        }
        """;

        var deserialized = JsonSerializer.Deserialize<ReturnRequestResponse>(legacyReceiptJson, CaseInsensitiveOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(reqId, deserialized.ReturnRequestId);
        Assert.Equal(itemId, deserialized.ItemId);
        Assert.Single(deserialized.Units);
        Assert.Equal(assignId, deserialized.Units[0].RentalUnitAssignmentId);
        Assert.Equal(assignId, deserialized.Units[0].AssignmentId);
        Assert.Null(deserialized.Units[0].ClosedAt);
    }

    [Fact]
    public async Task GetMyOrder_ReturnsUnitsAndReturnRequestsForRenter()
    {
        using OrderApiFactory factory = new();
        var (order, item, u1, _) = CreateTestOrder();

        var reqUnit = new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u1.Id, [Guid.NewGuid()]);
        var req = new ReturnRequest(Guid.NewGuid(), item.Id, Now, "Renter request",
            order.RenterId, Now, [reqUnit]);
        item.AddReturnRequest(req);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        using var renter = factory.CreateClientFor(order.RenterId);
        using var resp = await renter.GetAsync($"/api/v1/rental/orders/{order.Id}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var subOrders = body.GetProperty("subOrders");
        Assert.Single(subOrders.EnumerateArray());

        var items = subOrders[0].GetProperty("items");
        Assert.Single(items.EnumerateArray());
        var firstItem = items[0];

        // Units present with expected fields
        var units = firstItem.GetProperty("units");
        Assert.Equal(2, units.GetArrayLength());
        var unitElem = units.EnumerateArray().Single(u => u.GetProperty("assignmentId").GetString() == u1.Id.ToString());
        Assert.True(unitElem.GetProperty("hasOpenReturnRequest").GetBoolean());
        Assert.False(unitElem.GetProperty("canRequestReturn").GetBoolean());
        Assert.False(unitElem.TryGetProperty("assetId", out _));
        Assert.False(unitElem.TryGetProperty("allocationId", out _));

        // ReturnRequests present
        var returnReqs = firstItem.GetProperty("returnRequests");
        Assert.Single(returnReqs.EnumerateArray());
        var returnReqElem = returnReqs[0];
        Assert.Equal(req.Id.ToString(), returnReqElem.GetProperty("returnRequestId").GetString());
        Assert.Equal(item.Id.ToString(), returnReqElem.GetProperty("itemId").GetString());
        Assert.Equal("Renter request", returnReqElem.GetProperty("note").GetString());

        var reqUnits = returnReqElem.GetProperty("units");
        Assert.Single(reqUnits.EnumerateArray());
        Assert.Equal(u1.Id.ToString(), reqUnits[0].GetProperty("rentalUnitAssignmentId").GetString());
        Assert.True(reqUnits[0].GetProperty("closedAt").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task GetMySubOrder_ReturnsReturnRequestsForSupplier_AndIsolatesOtherSupplier()
    {
        using OrderApiFactory factory = new();
        var (order, item, u1, _) = CreateTestOrder();

        var req = new ReturnRequest(Guid.NewGuid(), item.Id, Now, "Supplier visible note",
            order.RenterId, Now, [new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), u1.Id, [])]);
        item.AddReturnRequest(req);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        var subOrderId = order.SubOrders.First().Id;
        var supplierId = item.SupplierId;

        // Owner supplier sees the suborder and return request
        using var ownerSupplier = factory.CreateClientFor(supplierId);
        using var ownerResp = await ownerSupplier.GetAsync($"/api/v1/rental/supplier/sub-orders/{subOrderId}");

        Assert.Equal(HttpStatusCode.OK, ownerResp.StatusCode);
        var body = await ownerResp.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items");
        Assert.Single(items.EnumerateArray());
        var returnReqs = items[0].GetProperty("returnRequests");
        Assert.Single(returnReqs.EnumerateArray());
        Assert.Equal(req.Id.ToString(), returnReqs[0].GetProperty("returnRequestId").GetString());
        Assert.Equal("Supplier visible note", returnReqs[0].GetProperty("note").GetString());

        // Stranger supplier gets 404 (isolation)
        using var strangerSupplier = factory.CreateClientFor(Guid.NewGuid());
        using var strangerResp = await strangerSupplier.GetAsync($"/api/v1/rental/supplier/sub-orders/{subOrderId}");
        Assert.Equal(HttpStatusCode.NotFound, strangerResp.StatusCode);
    }

    [Fact]
    public void ReturnRequestStatusPolicy_DerivesStatusesAndOverdue_Correctly()
    {
        Guid assign1 = Guid.NewGuid();
        Guid assign2 = Guid.NewGuid();
        Guid reqId = Guid.NewGuid();

        var u1 = new ReturnRequestUnit(Guid.NewGuid(), reqId, assign1, []);
        var u2 = new ReturnRequestUnit(Guid.NewGuid(), reqId, assign2, []);

        // Both actionable, no issues -> PendingSupplierConfirmation
        HashSet<Guid> disputedIds = [];
        Assert.Equal(ReturnRequestUnitStatus.PendingSupplierConfirmation, ReturnRequestStatusPolicy.GetUnitStatus(u1, disputedIds));
        Assert.Equal(ReturnRequestUnitStatus.PendingSupplierConfirmation, ReturnRequestStatusPolicy.GetUnitStatus(u2, disputedIds));
        Assert.Equal(ReturnRequestStatus.PendingSupplierConfirmation, ReturnRequestStatusPolicy.GetRequestStatus([
            ReturnRequestUnitStatus.PendingSupplierConfirmation,
            ReturnRequestUnitStatus.PendingSupplierConfirmation
        ]));

        // Issue reported on u1 -> u1 is Disputed, request is Disputed
        disputedIds.Add(assign1);
        Assert.Equal(ReturnRequestUnitStatus.Disputed, ReturnRequestStatusPolicy.GetUnitStatus(u1, disputedIds));
        Assert.Equal(ReturnRequestStatus.Disputed, ReturnRequestStatusPolicy.GetRequestStatus([
            ReturnRequestUnitStatus.Disputed,
            ReturnRequestUnitStatus.PendingSupplierConfirmation
        ]));

        // u1 is Confirmed (ClosedAt set), u2 is Rejected (RejectedAt set)
        u1.SetClosedAt(Now);
        u2.SetRejectedAt(Now);
        Assert.Equal(ReturnRequestUnitStatus.Confirmed, ReturnRequestStatusPolicy.GetUnitStatus(u1, disputedIds));
        Assert.Equal(ReturnRequestUnitStatus.Rejected, ReturnRequestStatusPolicy.GetUnitStatus(u2, disputedIds));
        Assert.Equal(ReturnRequestStatus.Resolved, ReturnRequestStatusPolicy.GetRequestStatus([
            ReturnRequestUnitStatus.Confirmed,
            ReturnRequestUnitStatus.Rejected
        ]));

        // All confirmed -> Confirmed
        Assert.Equal(ReturnRequestStatus.Confirmed, ReturnRequestStatusPolicy.GetRequestStatus([
            ReturnRequestUnitStatus.Confirmed,
            ReturnRequestUnitStatus.Confirmed
        ]));

        // All rejected -> Rejected
        Assert.Equal(ReturnRequestStatus.Rejected, ReturnRequestStatusPolicy.GetRequestStatus([
            ReturnRequestUnitStatus.Rejected,
            ReturnRequestUnitStatus.Rejected
        ]));
    }

    [Fact]
    public void ReturnEligibilityPolicy_RejectionFreesAssignmentForNewRequest()
    {
        var (_, item, assignment, _) = CreateTestOrder();
        var reqUnit = new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), assignment.Id, []);

        var req = new ReturnRequest(Guid.NewGuid(), item.Id, Now.AddHours(-1), "Previous request",
            Guid.NewGuid(), Now.AddHours(-1), [reqUnit]);
        item.AddReturnRequest(req);

        // When open, cannot request return
        Assert.True(ReturnEligibilityPolicy.HasOpenReturnRequest(item, assignment.Id));
        Assert.False(ReturnEligibilityPolicy.CanRequestReturn(item, assignment));

        // Once rejected, can request return again!
        reqUnit.SetRejectedAt(Now);
        Assert.False(ReturnEligibilityPolicy.HasOpenReturnRequest(item, assignment.Id));
        Assert.True(ReturnEligibilityPolicy.CanRequestReturn(item, assignment));
    }
}
