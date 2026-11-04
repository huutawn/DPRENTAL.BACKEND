using System.Diagnostics.CodeAnalysis;

namespace Dprental.ReviewService.Common.Enums;

/// <summary>
/// Chiều đánh giá giữa người thuê và nhà cung cấp/thiết bị.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Matches API specification wire format.")]
public enum ReviewDirection
{
    /// <summary>Người thuê đánh giá thiết bị và nhà cung cấp.</summary>
    renter_to_supplier_listing = 1,

    /// <summary>Nhà cung cấp đánh giá người thuê.</summary>
    supplier_to_renter = 2,

    /// <summary>PascalCase alias for renter_to_supplier_listing.</summary>
    RenterToSupplierListing = renter_to_supplier_listing,

    /// <summary>PascalCase alias for supplier_to_renter.</summary>
    SupplierToRenter = supplier_to_renter
}
