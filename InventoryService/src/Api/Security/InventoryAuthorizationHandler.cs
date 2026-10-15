using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Dprental.InventoryService.Api.Security;

public sealed class AssetManageRequirement : IAuthorizationRequirement { }
public sealed class CalendarReadRequirement : IAuthorizationRequirement { }
public sealed class AvailabilityReadRequirement : IAuthorizationRequirement { }
public sealed class AllocationWriteRequirement : IAuthorizationRequirement { }
public sealed class ManualAllocationRequirement : IAuthorizationRequirement { }
public sealed class EquipmentListingBatchReadRequirement : IAuthorizationRequirement { }

/// <summary>
/// Authorization Handler tập trung xử lý toàn bộ 5 chính sách phân quyền của Inventory Service.
/// Sử dụng InventoryAccessEvaluator và IInventoryCallerContext để quyết định phân quyền.
/// </summary>
public sealed class InventoryAuthorizationHandler : IAuthorizationHandler
{
    private readonly IInventoryCallerContext _caller;

    public InventoryAuthorizationHandler(IInventoryCallerContext caller)
    {
        _caller = caller ?? throw new ArgumentNullException(nameof(caller));
    }

    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (var req in context.Requirements)
        {
            switch (req)
            {
                case AssetManageRequirement manageReq:
                    HandleAssetManage(context, manageReq);
                    break;

                case CalendarReadRequirement calReq:
                    HandleCalendarRead(context, calReq);
                    break;

                case AvailabilityReadRequirement availReq:
                    HandleAvailabilityRead(context, availReq);
                    break;

                case AllocationWriteRequirement allocReq:
                    HandleAllocationWrite(context, allocReq);
                    break;

                case ManualAllocationRequirement manualReq:
                    HandleManualAllocation(context, manualReq);
                    break;

                case EquipmentListingBatchReadRequirement batchReq:
                    HandleEquipmentListingBatchRead(context, batchReq);
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private void HandleAssetManage(AuthorizationHandlerContext context, AssetManageRequirement req)
    {
        Guid? ownerUserId = ExtractOwnerUserId(context.Resource);
        if (InventoryAccessEvaluator.CanManageAsset(_caller, ownerUserId))
        {
            context.Succeed(req);
        }
        else
        {
            context.Fail();
        }
    }

    private void HandleCalendarRead(AuthorizationHandlerContext context, CalendarReadRequirement req)
    {
        Guid? ownerUserId = ExtractOwnerUserId(context.Resource);
        if (InventoryAccessEvaluator.CanAccessCalendar(_caller, ownerUserId))
        {
            context.Succeed(req);
        }
        else
        {
            context.Fail();
        }
    }

    private void HandleAvailabilityRead(AuthorizationHandlerContext context, AvailabilityReadRequirement req)
    {
        if (InventoryAccessEvaluator.CanReadAvailability(_caller))
        {
            context.Succeed(req);
        }
        else
        {
            context.Fail();
        }
    }

    private void HandleAllocationWrite(AuthorizationHandlerContext context, AllocationWriteRequirement req)
    {
        if (InventoryAccessEvaluator.CanWriteAllocations(_caller))
        {
            context.Succeed(req);
        }
        else
        {
            context.Fail();
        }
    }

    private void HandleEquipmentListingBatchRead(AuthorizationHandlerContext context, EquipmentListingBatchReadRequirement req)
    {
        if (InventoryAccessEvaluator.CanReadEquipmentListingBatch(_caller))
        {
            context.Succeed(req);
        }
        else
        {
            context.Fail();
        }
    }

    private void HandleManualAllocation(AuthorizationHandlerContext context, ManualAllocationRequirement req)
    {
        Guid? ownerUserId = ExtractOwnerUserId(context.Resource);
        if (InventoryAccessEvaluator.CanManualAllocate(_caller, ownerUserId))
        {
            context.Succeed(req);
        }
        else
        {
            context.Fail();
        }
    }

    private static Guid? ExtractOwnerUserId(object? resource)
    {
        return resource switch
        {
            EquipmentListing listing => listing.OwnerUserId,
            InventoryAsset asset => asset.Listing?.OwnerUserId,
            _ => null
        };
    }
}
