using Dprental.InventoryService.Api.Controllers;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Allocations;

public sealed class InventoryAllocationsControllerTests
{
    private sealed class FakeCallerContext : IInventoryCallerContext
    {
        public bool IsAuthenticated => true;
        public bool IsService => true;
        public bool IsAdmin => false;
        public Guid? UserId => null;
        public IReadOnlySet<string> Permissions => new HashSet<string>();
        public IReadOnlySet<string> Scopes => new HashSet<string> { InventoryAccessEvaluator.ScopeAllocationWrite };
        public IReadOnlySet<string> Roles => new HashSet<string>();
        public Guid? OrganizationId => null;
    }

    private sealed class FakeAllocationService : IInventoryAllocationService
    {
        public Func<Guid, CreateHoldRequest, Task<Result<CreateHoldResult>>>? CreateHoldHandler { get; set; }
        public Func<Guid, AllocateRequest, Task<Result<AllocationResponse>>>? AllocateHandler { get; set; }

        public Task<Result<CreateHoldResult>> CreateHoldAsync(
            Guid assetId,
            CreateHoldRequest request,
            IInventoryCallerContext caller,
            CancellationToken cancellationToken = default)
        {
            return CreateHoldHandler is not null
                ? CreateHoldHandler(assetId, request)
                : Task.FromResult(Result.Success(new CreateHoldResult(CreateSampleResponse(assetId, "Held"), false)));
        }

        public Task<Result<AllocationResponse>> AllocateAsync(
            Guid allocationId,
            AllocateRequest request,
            IInventoryCallerContext caller,
            CancellationToken cancellationToken = default)
        {
            return AllocateHandler is not null
                ? AllocateHandler(allocationId, request)
                : Task.FromResult(Result.Success(CreateSampleResponse(Guid.NewGuid(), "Allocated")));
        }

        private static AllocationResponse CreateSampleResponse(Guid assetId, string status) =>
            new(
                Id: Guid.NewGuid(),
                AssetId: assetId,
                SourceSystem: "rental",
                ExternalReference: "REF-123",
                StartDate: new DateOnly(2026, 11, 1),
                EndDate: new DateOnly(2026, 11, 5),
                Status: status,
                HoldExpiresAt: DateTimeOffset.UtcNow.AddHours(2),
                Notes: "Sample note",
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
    }

    private static (InventoryAllocationsController Controller, FakeAllocationService Service) CreateController()
    {
        var service = new FakeAllocationService();
        var caller = new FakeCallerContext();
        var controller = new InventoryAllocationsController(service, caller)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return (controller, service);
    }

    [Fact]
    public async Task CreateHold_NewHold_Returns201Created()
    {
        var (controller, service) = CreateController();
        var assetId = Guid.NewGuid();
        var sampleResponse = new AllocationResponse(
            Guid.NewGuid(), assetId, "sys", "ref",
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5),
            "Held", DateTimeOffset.UtcNow.AddHours(2), null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        service.CreateHoldHandler = (_, _) =>
            Task.FromResult(Result.Success(new CreateHoldResult(sampleResponse, IsReplay: false)));

        var req = new CreateHoldRequest("sys", "ref", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), DateTimeOffset.UtcNow.AddHours(2));
        var actionResult = await controller.CreateHold(assetId, req, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status201Created, objectResult.StatusCode);
        Assert.Equal(sampleResponse, objectResult.Value);
    }

    [Fact]
    public async Task CreateHold_Replay_Returns200OK()
    {
        var (controller, service) = CreateController();
        var assetId = Guid.NewGuid();
        var sampleResponse = new AllocationResponse(
            Guid.NewGuid(), assetId, "sys", "ref",
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5),
            "Held", DateTimeOffset.UtcNow.AddHours(2), null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        service.CreateHoldHandler = (_, _) =>
            Task.FromResult(Result.Success(new CreateHoldResult(sampleResponse, IsReplay: true)));

        var req = new CreateHoldRequest("sys", "ref", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), DateTimeOffset.UtcNow.AddHours(2));
        var actionResult = await controller.CreateHold(assetId, req, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
        Assert.Equal(sampleResponse, okResult.Value);
    }

    [Fact]
    public async Task CreateHold_Failure_ReturnsProblemDetails()
    {
        var (controller, service) = CreateController();
        service.CreateHoldHandler = (_, _) =>
            Task.FromResult(Result.Failure<CreateHoldResult>(Error.Conflict("ASSET_UNAVAILABLE", "Unavailable")));

        var req = new CreateHoldRequest("sys", "ref", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), DateTimeOffset.UtcNow.AddHours(2));
        var actionResult = await controller.CreateHold(Guid.NewGuid(), req, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("ASSET_UNAVAILABLE", problem.Title);
    }

    [Fact]
    public async Task Allocate_Success_Returns200OK()
    {
        var (controller, service) = CreateController();
        var allocId = Guid.NewGuid();
        var sampleResponse = new AllocationResponse(
            allocId, Guid.NewGuid(), "sys", "ref",
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5),
            "Allocated", null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        service.AllocateHandler = (_, _) =>
            Task.FromResult(Result.Success(sampleResponse));

        var actionResult = await controller.Allocate(allocId, new AllocateRequest("op-key"), CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
        Assert.Equal(sampleResponse, okResult.Value);
    }

    [Fact]
    public async Task Allocate_ConflictError_Returns409Conflict()
    {
        var (controller, service) = CreateController();
        service.AllocateHandler = (_, _) =>
            Task.FromResult(Result.Failure<AllocationResponse>(Error.Conflict("HOLD_EXPIRED", "Lượt giữ chỗ đã hết hạn.")));

        var actionResult = await controller.Allocate(Guid.NewGuid(), new AllocateRequest("op-key"), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("HOLD_EXPIRED", problem.Title);
    }

    [Fact]
    public async Task Allocate_InvalidStatusTransition_Returns409Conflict()
    {
        var (controller, service) = CreateController();
        service.AllocateHandler = (_, _) =>
            Task.FromResult(Result.Failure<AllocationResponse>(Error.Conflict("INVALID_STATUS_TRANSITION", "Chỉ có thể phân bổ khi đang ở trạng thái Held.")));

        var actionResult = await controller.Allocate(Guid.NewGuid(), new AllocateRequest("op-key"), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("INVALID_STATUS_TRANSITION", problem.Title);
    }

    [Fact]
    public async Task Allocate_TerminalState_Returns409Conflict()
    {
        var (controller, service) = CreateController();
        service.AllocateHandler = (_, _) =>
            Task.FromResult(Result.Failure<AllocationResponse>(Error.Conflict("ALLOCATION_IN_TERMINAL_STATE", "Đã ở trạng thái kết thúc.")));

        var actionResult = await controller.Allocate(Guid.NewGuid(), new AllocateRequest("op-key"), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("ALLOCATION_IN_TERMINAL_STATE", problem.Title);
    }

    [Fact]
    public async Task Allocate_IdempotencyKeyReused_Returns409Conflict()
    {
        var (controller, service) = CreateController();
        service.AllocateHandler = (_, _) =>
            Task.FromResult(Result.Failure<AllocationResponse>(Error.Conflict("IDEMPOTENCY_KEY_REUSED", "Key reused.")));

        var actionResult = await controller.Allocate(Guid.NewGuid(), new AllocateRequest("op-key"), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", problem.Title);
    }

    [Fact]
    public async Task Allocate_NotFound_Returns404NotFound()
    {
        var (controller, service) = CreateController();
        service.AllocateHandler = (_, _) =>
            Task.FromResult(Result.Failure<AllocationResponse>(Error.NotFound("ALLOCATION_NOT_FOUND", "Not found.")));

        var actionResult = await controller.Allocate(Guid.NewGuid(), new AllocateRequest("op-key"), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("ALLOCATION_NOT_FOUND", problem.Title);
    }
}
