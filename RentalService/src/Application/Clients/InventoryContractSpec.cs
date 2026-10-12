namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Frozen specification metadata and wire constants for Rental–Inventory Contract v1.
/// </summary>
public static class InventoryContractSpec
{
    public const string Version = "v1";
    public const string SourceSystem = "rental";
    public const string TimeZoneId = "Asia/Ho_Chi_Minh";

    public static class Routes
    {
        public static string ListingRentalSnapshot(Guid listingId) =>
            $"/equipment-listings/{listingId}/rental-snapshot";

        public static string CheckAvailability(Guid listingId) =>
            $"/equipment-listings/{listingId}/availability/check";

        public static string CreateHold(Guid listingId) =>
            $"/equipment-listings/{listingId}/holds";

        public static string ExtendHold(string allocationGroupId) =>
            $"/allocation-groups/{allocationGroupId}/extend-hold";

        public static string ConfirmAllocation(string allocationGroupId) =>
            $"/allocation-groups/{allocationGroupId}/confirm";

        public static string ReleaseAllocation(string allocationGroupId) =>
            $"/allocation-groups/{allocationGroupId}/release";

        public static string CancelAllocation(string allocationGroupId) =>
            $"/allocation-groups/{allocationGroupId}/cancel";

        public static string MarkOverdue(string allocationGroupId) =>
            $"/allocation-groups/{allocationGroupId}/mark-overdue";

        public static string MarkReturned(string allocationGroupId) =>
            $"/allocation-groups/{allocationGroupId}/mark-returned";

        public static string BlockDamaged(string allocationGroupId) =>
            $"/allocation-groups/{allocationGroupId}/block-damaged";
    }

    public static class ErrorCodes
    {
        public const string HoldExpired = "HOLD_EXPIRED";
        public const string InsufficientAvailability = "INSUFFICIENT_AVAILABILITY";
        public const string ExternalReferenceReused = "EXTERNAL_REFERENCE_REUSED";
        public const string SnapshotVersionMismatch = "SNAPSHOT_VERSION_MISMATCH";
        public const string InvalidGroupMembership = "INVALID_GROUP_MEMBERSHIP";
        public const string IncompatibleAllocationState = "INCOMPATIBLE_ALLOCATION_STATE";
        public const string InvalidReceipt = "INVALID_RECEIPT";
        public const string InvalidHoldExpiry = "INVALID_HOLD_EXPIRY";
        public const string ListingNotFound = "LISTING_NOT_FOUND";
        public const string AllocationGroupNotFound = "ALLOCATION_GROUP_NOT_FOUND";
    }

    public static class Scopes
    {
        public const string ReadAvailability = "inventory.availability.read";
        public const string WriteAllocation = "inventory.allocation.write";
    }
}
