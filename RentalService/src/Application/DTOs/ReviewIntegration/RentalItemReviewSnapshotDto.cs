using System;

namespace Dprental.RentalService.Application.DTOs.ReviewIntegration;

/// <summary>
/// DTO chứa snapshot thông tin mục thuê phục vụ kiểm tra điều kiện đánh giá từ ReviewService.
/// Tuân thủ nguyên tắc Data Minimization (8 trường authoritative).
/// </summary>
public sealed record RentalItemReviewSnapshotDto(
    Guid OrderId,
    Guid ItemId,
    Guid RenterId,
    Guid SupplierId,
    Guid EquipmentListingId,
    string Status,
    DateTimeOffset? CompletedAt,
    long OrderVersion);
