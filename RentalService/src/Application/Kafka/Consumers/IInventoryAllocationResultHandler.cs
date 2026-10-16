using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Kafka.Consumers;

public interface IInventoryAllocationResultHandler
{
    Task<Result> HandleAllocationEventAsync(
        Guid orderId,
        RentalInboxMessage msg,
        RentalOrder? order,
        CancellationToken cancellationToken);
}
