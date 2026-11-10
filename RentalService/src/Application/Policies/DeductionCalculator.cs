using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Policies;

public sealed record DeductionCalculation(decimal Total, IReadOnlyList<Guid> AffectedAssetIds);

/// <summary>
/// Công thức khấu trừ: mỗi máy = % mức hư hại (theo snapshot của item) × DepositPerUnitSnapshot,
/// tối đa 100% cọc của máy đó. Tổng là TotalProposedAmount của đề nghị.
/// </summary>
public static class DeductionCalculator
{
    public static Result<DeductionCalculation> Calculate(RentalItem item, IReadOnlyList<DamageAssessment> assessments)
    {
        var levels = DamageLevelSnapshot.Deserialize(item.DamageLevelsSnapshot);
        if (levels is null)
            return Error.Conflict("DAMAGE_LEVELS_SNAPSHOT_MISSING", "Mục thuê chưa có snapshot bảng mức hư hại.");

        if (assessments.Count == 0 || assessments.Select(a => a.AssetId).Distinct().Count() != assessments.Count)
            return Error.Validation("INVALID_DAMAGE_ASSESSMENTS", "Cần đánh giá ít nhất một máy, mỗi máy một mức.");

        // Mọi máy được đánh giá, kể cả mức 0%, phải là máy trả hỏng của chính mục thuê này.
        var damagedAssetIds = item.UnitAssignments
            .Where(u => u.Status == RentalUnitAssignmentStatus.ReturnedDamaged)
            .Select(u => u.AssetId).ToHashSet();
        if (assessments.Any(a => !damagedAssetIds.Contains(a.AssetId)))
            return Error.Validation("ASSET_NOT_DAMAGED_IN_ITEM",
                "Chỉ được chọn mức cho máy trả về bị hỏng của mục thuê này.");

        decimal total = 0m;
        foreach (var assessment in assessments)
        {
            var level = levels.FirstOrDefault(l => l.Level == assessment.Level);
            if (level is null)
                return Error.Validation("INVALID_DAMAGE_LEVEL", $"Mức hư hại {assessment.Level} không có trong bảng của đơn.");

            decimal perUnit = decimal.Round(
                item.DepositPerUnitSnapshot * level.Percent / 100m, 2, MidpointRounding.AwayFromZero);
            total += Math.Min(perUnit, item.DepositPerUnitSnapshot);
        }

        if (total == 0m)
            return Error.Validation("NO_DAMAGE_SELECTED",
                "Tất cả máy đều chọn mức 0%, không có khoản nào để khấu trừ. Chọn mức có phần trăm lớn hơn 0 cho máy bị hỏng.");

        var affected = assessments
            .Where(a => levels.First(l => l.Level == a.Level).Percent > 0)
            .Select(a => a.AssetId).ToList();
        return new DeductionCalculation(total, affected);
    }
}
