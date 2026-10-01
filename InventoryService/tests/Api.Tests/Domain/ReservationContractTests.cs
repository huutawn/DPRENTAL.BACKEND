using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

public sealed class ReservationContractTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Delivered = new(2026, 10, 5, 3, 0, 0, TimeSpan.Zero);
    private static readonly RentalPeriod Period = new(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 10));

    private static Reservation Manual(Guid equipmentId, Guid? renter = null, string? key = null) =>
        Reservation.CreateManual(Guid.NewGuid(), equipmentId, Period, "Renter", "0900000000", Created,
            renter, operationKey: key).Value;

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(12)]
    public void CompletedUsesActualVietnamReturnDate(int returnDay)
    {
        var equipment = InventoryEquipment.Create(Guid.NewGuid(), Guid.NewGuid(), CatalogLifecycleStatus.Active, true, Created).Value;
        var reservation = Manual(equipment.Id);
        var returned = new DateTimeOffset(2026, 10, returnDay, 17, 30, 0, TimeSpan.Zero);
        Assert.True(reservation.Deliver("staff", Delivered, null, null, Delivered).IsSuccess);
        Assert.True(reservation.Return("staff", returned, EquipmentReturnCondition.Intact, null, null, returned).IsSuccess);
        var vietnamDate = BusinessDateTime.ToVietnamDate(returned);
        Assert.Equal(returnDay + 1, vietnamDate.Day);
        var busy = EquipmentAvailabilityPolicy.CheckAvailability(equipment, new(vietnamDate, vietnamDate), returned, [reservation], []);
        var free = EquipmentAvailabilityPolicy.CheckAvailability(equipment, new(vietnamDate.AddDays(1), vietnamDate.AddDays(1)), returned, [reservation], []);
        Assert.True(busy.IsFailure);
        Assert.True(free.IsSuccess);
        Assert.Equal(Period, reservation.RentalPeriod);
    }

    [Theory]
    [InlineData(-25, -25)]
    [InlineData(1, 0)]
    public void InvalidDeliveryTimeDoesNotChangeState(int deliveredHoursFromEarliest, int nowHoursFromEarliest)
    {
        var reservation = Manual(Guid.NewGuid());
        var earliest = BusinessDateTime.StartOfDayUtc(Period.StartDate.AddDays(-1));
        var result = reservation.Deliver("staff", earliest.AddHours(deliveredHoursFromEarliest), null, null, earliest.AddHours(nowHoursFromEarliest));
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DELIVERY_TIME", result.Error.Code);
        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        Assert.Single(reservation.Transitions);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 2)]
    [InlineData(3, 2)]
    public void InvalidReturnTimeDoesNotComplete(int returnHours, int nowHours)
    {
        var reservation = Manual(Guid.NewGuid());
        Assert.True(reservation.Deliver("staff", Delivered, null, null, Delivered).IsSuccess);
        var result = reservation.Return("staff", Delivered.AddHours(returnHours), EquipmentReturnCondition.Intact, null, null, Delivered.AddHours(nowHours));
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_RETURN_TIME", result.Error.Code);
        Assert.Equal(ReservationStatus.InUse, reservation.Status);
        Assert.Equal(2, reservation.Transitions.Count);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("Broken lens", 0)]
    [InlineData(null, 1)]
    public void DamagedReturnRequiresDescriptionAndPhotos(string? description, int photos)
    {
        var reservation = Manual(Guid.NewGuid());
        Assert.True(reservation.Deliver("staff", Delivered, null, null, Delivered).IsSuccess);
        var result = reservation.Return("staff", Delivered.AddHours(1), EquipmentReturnCondition.Damaged,
            description, Enumerable.Range(0, photos).Select(_ => Guid.NewGuid()).ToArray(), Delivered.AddHours(1));
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_RETURN_EVIDENCE", result.Error.Code);
        Assert.Equal(ReservationStatus.InUse, reservation.Status);
    }

    [Fact]
    public void PhotoEvidenceAndTransitionsCannotBeMutatedByCallers()
    {
        var renter = Guid.NewGuid();
        var reservation = Manual(Guid.NewGuid(), renter);
        var photo = Guid.NewGuid();
        var input = new List<Guid> { photo };
        Assert.True(reservation.Deliver("staff", Delivered, null, input, Delivered).IsSuccess);
        input.Clear();
        Assert.Equal(photo, Assert.Single(reservation.DeliveryPhotoIds));
        Assert.Throws<NotSupportedException>(() => ((ICollection<Guid>)reservation.DeliveryPhotoIds).Add(Guid.NewGuid()));
        Assert.Throws<NotSupportedException>(() => ((ICollection<ReservationTransition>)reservation.Transitions).Clear());
        Assert.True(reservation.Return("staff", Delivered.AddHours(1), EquipmentReturnCondition.Damaged,
            "Broken lens", [photo], Delivered.AddHours(1)).IsSuccess);
        Assert.Throws<NotSupportedException>(() => ((ICollection<Guid>)reservation.ReturnPhotoIds).Clear());
    }

    [Fact]
    public void CreationKeyCannotBeReusedForCancellation()
    {
        var reservation = Manual(Guid.NewGuid(), key: "create");
        var result = reservation.Cancel("reason", Created, "operator", "create");
        Assert.True(result.IsFailure);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", result.Error.Code);
        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        Assert.Single(reservation.Transitions);
    }

    [Fact]
    public void RetryMustMatchOperationAndPayload()
    {
        var reservation = Reservation.CreateHold(Guid.NewGuid(), Guid.NewGuid(), Period, Created.AddHours(2), Created,
            "tests", "reference", Guid.NewGuid()).Value;
        Assert.True(reservation.ExtendHold(Created.AddHours(3), Created.AddHours(1), "service", "extend").IsSuccess);
        var count = reservation.Transitions.Count;
        Assert.True(reservation.ExtendHold(Created.AddHours(3), Created.AddHours(2), "service", "extend").IsSuccess);
        Assert.True(reservation.ExtendHold(Created.AddHours(4), Created.AddHours(2), "service", "extend").IsFailure);
        Assert.True(reservation.ConfirmHold(Created.AddHours(2), "service", "extend").IsFailure);
        Assert.Equal(count, reservation.Transitions.Count);
        Assert.Equal(Created.AddHours(3), reservation.HoldExpiresAt);
    }

    [Theory]
    [InlineData(null, "ref", true)]
    [InlineData(" ", "ref", true)]
    [InlineData("tests", null, true)]
    [InlineData("tests", " ", true)]
    [InlineData("tests", "ref", false)]
    public void ExternalHoldRequiresSourceReferenceAndRenter(string? source, string? reference, bool hasRenter)
    {
        var result = Reservation.CreateHold(Guid.NewGuid(), Guid.NewGuid(), Period, Created.AddHours(1), Created,
            source, reference, hasRenter ? Guid.NewGuid() : null);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_EXTERNAL_HOLD", result.Error.Code);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void DamageDescriptionLengthAndEvidenceAreValidated(int length, bool expectedSuccess)
    {
        var renter = Guid.NewGuid();
        var reservation = Manual(Guid.NewGuid(), renter);
        Assert.True(reservation.Deliver("staff", Delivered, null, null, Delivered).IsSuccess);
        var photos = new List<Guid> { Guid.NewGuid() };
        var result = DamageReport.Create(Guid.NewGuid(), reservation, renter, new string('x', length), true,
            photos, Delivered, 0);
        Assert.Equal(expectedSuccess, result.IsSuccess);
        if (result.IsSuccess)
        {
            photos.Clear();
            Assert.Single(result.Value.PhotoMediaIds);
            Assert.Throws<NotSupportedException>(() => ((ICollection<Guid>)result.Value.PhotoMediaIds).Clear());
        }
    }

    [Fact]
    public void CatalogAndSupplierBookingToggleRemainIndependent()
    {
        var equipment = InventoryEquipment.Create(Guid.NewGuid(), Guid.NewGuid(), CatalogLifecycleStatus.Active, true, Created).Value;
        Assert.True(equipment.UpdateCatalogLifecycle(CatalogLifecycleStatus.Inactive, Created).IsSuccess);
        Assert.True(equipment.AcceptingBookings);
        Assert.False(equipment.CanAcceptBookings());
        Assert.True(equipment.DisableBookings(null, Created).IsSuccess);
        Assert.True(equipment.EnableBookings(Created).IsSuccess);
        Assert.False(equipment.CanAcceptBookings());
        Assert.True(equipment.UpdateCatalogLifecycle(CatalogLifecycleStatus.Active, Created).IsSuccess);
        Assert.True(equipment.CanAcceptBookings());
    }
}
