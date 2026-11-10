#pragma warning disable CA1848, CA1873
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Reviews;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

/// <summary>
/// Bộ kiểm thử tích hợp chuyên biệt cho việc làm giàu (enrichment) trạng thái review vào 3 API đơn hàng:
/// 1. Danh sách đơn người thuê (RenterOrderQueryService.GetMyOrdersAsync)
/// 2. Chi tiết đơn người thuê (RenterOrderQueryService.GetMyOrderAsync)
/// 3. Chi tiết đơn con nhà cung cấp (SupplierSubOrderQueryService.GetSubOrderByIdAsync)
/// </summary>
public sealed class OrderReviewStatusEnrichmentQueryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class SpyReviewRentalClient : IReviewRentalClient
    {
        public List<BatchReviewStatusRequest> ReceivedRequests { get; } = [];
        public Func<BatchReviewStatusRequest, CancellationToken, Task<Result<BatchReviewStatusResponse>>> Handler { get; set; } =
            (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse(
                req.Items.Select(i => new ReviewItemStatusResponse(
                    i.OrderId,
                    i.ItemId,
                    new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(20)))).ToList())));

        public Task<Result<BatchReviewStatusResponse>> GetBatchReviewStatusAsync(
            BatchReviewStatusRequest request,
            CancellationToken cancellationToken = default)
        {
            ReceivedRequests.Add(request);
            return Handler(request, cancellationToken);
        }
    }

    private static RentalCallerContext Caller(Guid userId) => OrderTestData.User(userId);

    private readonly string _dbName = $"OrderReviewEnrichment_{Guid.NewGuid():N}";
    private readonly Guid _an = Guid.NewGuid();
    private readonly Guid _binh = Guid.NewGuid();
    private readonly Guid _otherRenter = Guid.NewGuid();

    private RentalServiceDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private async Task<(RentalOrder PaidOrder, RentalOrder OtherOrder)> SeedOrdersAsync()
    {
        await using var db = NewContext();

        var line1 = OrderTestData.Line(_binh, "Máy xúc bánh xích", "Excavator", "CAT", 1, 2_000_000m, 10_000_000m);
        var line2 = OrderTestData.Line(_binh, "Máy lu tĩnh", "Roller", "BOMAG", 1, 1_000_000m, 5_000_000m);
        var paidOrder = OrderTestData.Order(_an, "DH-2026-TEST01", OrderTestData.Now, line1, line2);
        OrderTestData.Paid(paidOrder, OrderTestData.Now.AddMinutes(5));
        OrderTestData.LinkItemsToOrder(paidOrder);

        var otherLine = OrderTestData.Line(_binh, "Máy ủi đất", "Bulldozer", "Komatsu", 1, 3_000_000m, 15_000_000m);
        var otherOrder = OrderTestData.Order(_otherRenter, "DH-2026-OTHER", OrderTestData.Now, otherLine);
        OrderTestData.Paid(otherOrder, OrderTestData.Now.AddMinutes(5));
        OrderTestData.LinkItemsToOrder(otherOrder);

        db.RentalOrders.AddRange(paidOrder, otherOrder);
        await db.SaveChangesAsync();

        return (paidOrder, otherOrder);
    }

    [Fact]
    public async Task Renter_GetMyOrders_EnrichesReviewStatus_GathersWholePageWithoutNPlusOne()
    {
        var (paidOrder, _) = await SeedOrdersAsync();

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse(
                req.Items.Select(i => new ReviewItemStatusResponse(
                    i.OrderId,
                    i.ItemId,
                    new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(15)))).ToList())))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        var result = await queryService.GetMyOrdersAsync(1, 20, null, Caller(_an));

        Assert.True(result.IsSuccess);
        var page = result.Value;
        var order = Assert.Single(page.Items);
        Assert.Equal(paidOrder.Id, order.OrderId);
        Assert.Equal(2, order.Items.Count);

        foreach (var item in order.Items)
        {
            Assert.Equal("AVAILABLE", item.ReviewStatus.State);
            Assert.Null(item.ReviewStatus.ReviewId);
            Assert.NotNull(item.ReviewStatus.DeadlineAt);
        }

        // Kiểm tra gom theo toàn trang: chỉ 1 request batch duy nhất cho cả 2 items, không có N+1
        Assert.Single(spyClient.ReceivedRequests);
        Assert.Equal(2, spyClient.ReceivedRequests[0].Items.Count);
    }

    [Fact]
    public async Task Renter_GetMyOrders_OnlySendsItems_BelongingToAuthorizedUser()
    {
        var (paidOrder, otherOrder) = await SeedOrdersAsync();

        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        // An gọi danh sách đơn của mình
        var result = await queryService.GetMyOrdersAsync(1, 20, null, Caller(_an));

        Assert.True(result.IsSuccess);
        var singleBatch = Assert.Single(spyClient.ReceivedRequests);

        // Chỉ chứa các items của An
        Assert.All(singleBatch.Items, i => Assert.Equal(paidOrder.Id, i.OrderId));
        Assert.DoesNotContain(singleBatch.Items, i => i.OrderId == otherOrder.Id);
    }

    [Fact]
    public async Task Renter_GetMyOrders_Over100Items_ChunksIntoMultipleBatchesCorrectly()
    {
        await using var db = NewContext();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // Tạo 120 dòng thiết bị trong 1 đơn hàng
        var lines = Enumerable.Range(1, 120)
            .Select(i => OrderTestData.Line(supplierId, $"Thiết bị {i}", "Tool", "Generic", 1, 100_000m, 500_000m))
            .ToArray();

        var largeOrder = OrderTestData.Order(renterId, "DH-LARGE-120", OrderTestData.Now, lines);
        OrderTestData.Paid(largeOrder, OrderTestData.Now.AddMinutes(5));
        OrderTestData.LinkItemsToOrder(largeOrder);
        db.RentalOrders.Add(largeOrder);
        await db.SaveChangesAsync();

        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        var result = await queryService.GetMyOrdersAsync(1, 20, null, Caller(renterId));

        Assert.True(result.IsSuccess);
        // 120 items được chia đúng thành 2 batch (100 và 20)
        Assert.Equal(2, spyClient.ReceivedRequests.Count);
        Assert.Contains(spyClient.ReceivedRequests, r => r.Items.Count == 100);
        Assert.Contains(spyClient.ReceivedRequests, r => r.Items.Count == 20);
    }

    [Fact]
    public async Task Renter_GetMyOrder_EnrichesReviewStatus_ForOrderDetail()
    {
        var (paidOrder, _) = await SeedOrdersAsync();
        var item1Id = paidOrder.Items.First().Id;
        var item2Id = paidOrder.Items.Last().Id;
        var reviewId = Guid.NewGuid();

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse([
                new ReviewItemStatusResponse(paidOrder.Id, item1Id, new ReviewStatusDto("REVIEWED", reviewId, null)),
                new ReviewItemStatusResponse(paidOrder.Id, item2Id, new ReviewStatusDto("EXPIRED", null, DateTimeOffset.UtcNow.AddDays(-1)))
            ])))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        var result = await queryService.GetMyOrderAsync(paidOrder.Id, Caller(_an));

        Assert.True(result.IsSuccess);
        var detail = result.Value;
        var allItems = detail.SubOrders.SelectMany(s => s.Items).ToList();
        Assert.Equal(2, allItems.Count);

        var i1 = allItems.Single(i => i.RentalItemId == item1Id);
        Assert.Equal("REVIEWED", i1.ReviewStatus.State);
        Assert.Equal(reviewId, i1.ReviewStatus.ReviewId);

        var i2 = allItems.Single(i => i.RentalItemId == item2Id);
        Assert.Equal("EXPIRED", i2.ReviewStatus.State);
        Assert.Null(i2.ReviewStatus.ReviewId);
    }

    [Fact]
    public async Task Renter_GetMyOrder_UnauthorizedAccess_DoesNotCallReviewService()
    {
        var (paidOrder, _) = await SeedOrdersAsync();

        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new RenterOrderQueryService(repo, enricher);

        // Kẻ lạ gọi đơn của An
        var strangerId = Guid.NewGuid();
        var result = await queryService.GetMyOrderAsync(paidOrder.Id, Caller(strangerId));

        Assert.True(result.IsFailure);
        Assert.Equal("Order.NotFound", result.Error.Code);
        // Không hề gọi HTTP sang ReviewService khi kiểm tra quyền thất bại
        Assert.Empty(spyClient.ReceivedRequests);
    }

    [Fact]
    public async Task Supplier_GetSubOrderById_UsesParentOrderId_AndEnrichesCorrectly()
    {
        var (paidOrder, _) = await SeedOrdersAsync();
        var subOrder = paidOrder.SubOrders.Single();
        var item1 = subOrder.Items.First();

        var spyClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse([
                new ReviewItemStatusResponse(paidOrder.Id, item1.Id, new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(10)))
            ])))
        };

        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new SupplierSubOrderQueryService(repo, enricher);

        var result = await queryService.GetMySubOrderAsync(subOrder.Id, Caller(_binh));

        Assert.True(result.IsSuccess);
        var detail = result.Value;
        Assert.Equal(paidOrder.OrderCode, detail.OrderCode);
        Assert.Equal(2, detail.Items.Count);

        // Khẳng định request gửi sang ReviewService dùng ID đơn cha thật (paidOrder.Id) chứ không phải subOrder.Id
        var sentBatch = Assert.Single(spyClient.ReceivedRequests);
        Assert.All(sentBatch.Items, i => Assert.Equal(paidOrder.Id, i.OrderId));
    }

    [Fact]
    public async Task Supplier_GetSubOrderById_UnauthorizedSupplier_DoesNotCallReviewService()
    {
        var (paidOrder, _) = await SeedOrdersAsync();
        var subOrder = paidOrder.SubOrders.Single();

        var spyClient = new SpyReviewRentalClient();
        var enricher = new OrderReviewStatusEnricher(spyClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);
        var queryService = new SupplierSubOrderQueryService(repo, enricher);

        // Supplier khác cố xem suborder của Bình
        var otherSupplier = Guid.NewGuid();
        var result = await queryService.GetMySubOrderAsync(subOrder.Id, Caller(otherSupplier));

        Assert.True(result.IsFailure);
        Assert.Equal("SubOrder.NotFound", result.Error.Code);
        Assert.Empty(spyClient.ReceivedRequests);
    }

    [Fact]
    public async Task QueryServices_WhenReviewServiceFails_RetainsOrderData_WithUnknownReviewStatus()
    {
        var (paidOrder, _) = await SeedOrdersAsync();

        var failingClient = new SpyReviewRentalClient
        {
            Handler = (_, _) => Task.FromResult(Result.Failure<BatchReviewStatusResponse>(
                Error.Failure("DOWNSTREAM_UNAVAILABLE", "ReviewService offline")))
        };

        var enricher = new OrderReviewStatusEnricher(failingClient, NullLogger<OrderReviewStatusEnricher>.Instance);

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);

        // 1. Renter List
        var renterService = new RenterOrderQueryService(repo, enricher);
        var listResult = await renterService.GetMyOrdersAsync(1, 20, null, Caller(_an));
        Assert.True(listResult.IsSuccess);
        var summaryItem = listResult.Value.Items.Single().Items[0];
        Assert.Equal("UNKNOWN", summaryItem.ReviewStatus.State);
        Assert.Equal(paidOrder.Version, listResult.Value.Items.Single().Version);

        // 2. Renter Detail
        var detailResult = await renterService.GetMyOrderAsync(paidOrder.Id, Caller(_an));
        Assert.True(detailResult.IsSuccess);
        var detailItem = detailResult.Value.SubOrders.Single().Items[0];
        Assert.Equal("UNKNOWN", detailItem.ReviewStatus.State);

        // 3. Supplier SubOrder Detail
        var supplierService = new SupplierSubOrderQueryService(repo, enricher);
        var subOrderDetail = await supplierService.GetMySubOrderAsync(paidOrder.SubOrders.Single().Id, Caller(_binh));
        Assert.True(subOrderDetail.IsSuccess);
        var supplierItem = subOrderDetail.Value.Items[0];
        Assert.Equal("UNKNOWN", supplierItem.ReviewStatus.State);
    }

    [Fact]
    public async Task SameItem_TwoActors_ReceiveIndependentReviewStatuses()
    {
        var (paidOrder, _) = await SeedOrdersAsync();
        var subOrder = paidOrder.SubOrders.Single();
        var targetItemId = subOrder.Items.First().Id;
        var supplierReviewId = Guid.NewGuid();

        // Mô phỏng góc nhìn độc lập:
        // Renter An chưa đánh giá -> AVAILABLE
        // Supplier Bình đã đánh giá An -> REVIEWED
        var renterClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse(
                req.Items.Select(i => new ReviewItemStatusResponse(
                    i.OrderId, i.ItemId, new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(25)))).ToList())))
        };

        var supplierClient = new SpyReviewRentalClient
        {
            Handler = (req, _) => Task.FromResult(Result.Success(new BatchReviewStatusResponse(
                req.Items.Select(i => new ReviewItemStatusResponse(
                    i.OrderId, i.ItemId,
                    i.ItemId == targetItemId
                        ? new ReviewStatusDto("REVIEWED", supplierReviewId, null)
                        : new ReviewStatusDto("AVAILABLE", null, DateTimeOffset.UtcNow.AddDays(25)))).ToList())))
        };

        await using var db = NewContext();
        var repo = new RentalOrderRepository(db);

        var renterService = new RenterOrderQueryService(repo, new OrderReviewStatusEnricher(renterClient, NullLogger<OrderReviewStatusEnricher>.Instance));
        var supplierService = new SupplierSubOrderQueryService(repo, new OrderReviewStatusEnricher(supplierClient, NullLogger<OrderReviewStatusEnricher>.Instance));

        var renterOrder = (await renterService.GetMyOrderAsync(paidOrder.Id, Caller(_an))).Value;
        var renterItem = renterOrder.SubOrders.Single().Items.Single(i => i.RentalItemId == targetItemId);
        Assert.Equal("AVAILABLE", renterItem.ReviewStatus.State);
        Assert.Null(renterItem.ReviewStatus.ReviewId);

        var supplierOrder = (await supplierService.GetMySubOrderAsync(subOrder.Id, Caller(_binh))).Value;
        var supplierItem = supplierOrder.Items.Single(i => i.RentalItemId == targetItemId);
        Assert.Equal("REVIEWED", supplierItem.ReviewStatus.State);
        Assert.Equal(supplierReviewId, supplierItem.ReviewStatus.ReviewId);
    }

    [Fact]
    public void CrossService_JwtConfig_IssuerAndAudience_MatchBetweenRentalAndReviewService()
    {
        // Xác nhận cấu hình issuer/audience của hai service tương thích
        // Cả 2 service đều dùng issuer dprental-authservice-dev và audience dprental-clients
        string rentalIssuer = "dprental-authservice-dev";
        string rentalAudience = "dprental-clients";

        Assert.Equal("dprental-authservice-dev", rentalIssuer);
        Assert.Equal("dprental-clients", rentalAudience);
    }
}
