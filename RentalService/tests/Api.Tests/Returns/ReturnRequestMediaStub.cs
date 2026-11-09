using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Dprental.RentalService.Api.Tests.Returns;

internal static class ReturnRequestMediaStub
{
    public static IRentalMediaClient ForRenter(RentalServiceDbContext db)
    {
        var renterId = db.RentalOrders.AsNoTracking().Select(order => order.RenterId).FirstOrDefault();
        return ForRenter(renterId);
    }

    public static IRentalMediaClient ForRenter(Guid renterId)
    {
        var media = new Mock<IRentalMediaClient>();
        media.Setup(client => client.VerifyMediaAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((id, _) =>
                Task.FromResult(Result.Success(new MediaVerifyDto(id, renterId.ToString(), "Ready"))));
        media.Setup(client => client.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((id, _) =>
                Task.FromResult(Result.Success(new MediaMetadataDto(id, "Private", "Ready"))));
        return media.Object;
    }
}
