using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Cart;

public sealed class GetRentalCart(
    IRentalCartRepository carts,
    RentalCartItemEvaluator evaluator)
{
    public async Task<Result<RentalCartReadModel>> ExecuteAsync(
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        if (renterId == Guid.Empty)
        {
            return Result.Success(new RentalCartReadModel(Guid.Empty, []));
        }

        RentalCart? cart = await carts.GetActiveByRenterIdAsync(renterId, cancellationToken);
        if (cart is null || cart.Status != RentalCartStatus.Active)
        {
            return Result.Success(new RentalCartReadModel(cart?.Id ?? Guid.Empty, []));
        }

        var lines = new List<RentalCartLineReadModel>();

        foreach (RentalCartItem item in cart.Items)
        {
            Result<RentalCartItemEvaluationResult> evalResult = await evaluator.EvaluateItemAsync(
                renterId,
                item.EquipmentListingId,
                item.Quantity,
                item.ExpectedStartDate,
                item.ExpectedEndDate,
                cancellationToken);

            if (evalResult.IsFailure)
            {
                return evalResult.Error;
            }

            RentalCartLineReadModel line = evaluator.ToLineReadModel(
                item.Id,
                item.EquipmentListingId,
                item.Quantity,
                item.ExpectedStartDate,
                item.ExpectedEndDate,
                evalResult.Value);

            lines.Add(line);
        }

        return Result.Success(new RentalCartReadModel(cart.Id, lines));
    }
}
