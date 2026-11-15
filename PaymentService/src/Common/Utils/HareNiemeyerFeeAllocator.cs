namespace Dprental.PaymentService.Common.Utils;

/// <summary>
/// Implements the Hare-Niemeyer (Largest Remainder) allocation method for distributing
/// group delivery and return fees down to individual line items proportionally to rental amount.
/// Guarantees exact VND allocation with zero remainder leakage (sum(shares) == totalFee).
/// Ties are resolved stably by fractional remainder descending, then index / item ID ascending.
/// </summary>
public static class HareNiemeyerFeeAllocator
{
    public sealed record AllocatableLine(Guid RentalItemId, decimal RentalAmount);

    /// <summary>
    /// Allocates totalFee across items based on their RentalAmount array.
    /// </summary>
    public static decimal[] Allocate(IReadOnlyList<decimal> rentalAmounts, decimal totalFee)
    {
        ArgumentNullException.ThrowIfNull(rentalAmounts);

        int count = rentalAmounts.Count;
        decimal[] shares = new decimal[count];
        if (totalFee <= 0m || count == 0)
        {
            return shares;
        }

        decimal totalRental = 0m;
        for (int i = 0; i < count; i++)
        {
            if (rentalAmounts[i] > 0m)
            {
                totalRental += rentalAmounts[i];
            }
        }

        if (totalRental <= 0m)
        {
            decimal baseShare = Math.Floor(totalFee / count);
            decimal rem = totalFee - (baseShare * count);
            for (int i = 0; i < count; i++)
            {
                shares[i] = baseShare + (i < rem ? 1m : 0m);
            }
            return shares;
        }

        decimal[] exactShares = new decimal[count];
        decimal allocatedSum = 0m;
        for (int i = 0; i < count; i++)
        {
            decimal rental = rentalAmounts[i] > 0m ? rentalAmounts[i] : 0m;
            exactShares[i] = (rental / totalRental) * totalFee;
            shares[i] = Math.Floor(exactShares[i]);
            allocatedSum += shares[i];
        }

        decimal remainder = totalFee - allocatedSum;
        if (remainder > 0m)
        {
            var remaindersWithIndex = exactShares
                .Select((exact, idx) => (Fraction: exact - shares[idx], Index: idx))
                .OrderByDescending(x => x.Fraction)
                .ThenBy(x => x.Index)
                .ToList();

            for (int i = 0; i < (int)remainder && i < count; i++)
            {
                shares[remaindersWithIndex[i].Index] += 1m;
            }
        }

        return shares;
    }

    /// <summary>
    /// Allocates totalFee across named items, returning a dictionary of RentalItemId -> Fee Share.
    /// </summary>
    public static Dictionary<Guid, decimal> Allocate(IReadOnlyList<AllocatableLine> lines, decimal totalFee)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var result = new Dictionary<Guid, decimal>(lines.Count);
        if (lines.Count == 0)
        {
            return result;
        }

        var rentalAmounts = lines.Select(l => l.RentalAmount).ToArray();
        var shares = Allocate(rentalAmounts, totalFee);

        for (int i = 0; i < lines.Count; i++)
        {
            result[lines[i].RentalItemId] = shares[i];
        }

        return result;
    }
}
