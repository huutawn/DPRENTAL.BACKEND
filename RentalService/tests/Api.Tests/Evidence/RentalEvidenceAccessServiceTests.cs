using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Security.Jwt;
using Dprental.RentalService.Application.Services.Evidence;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Evidence;

public sealed class RentalEvidenceAccessServiceTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly Guid _handoverMediaId = Guid.NewGuid();
    private readonly RentalOrder _order;
    private readonly Mock<IRentalOrderRepository> _orders = new();
    private readonly Mock<IRentalMediaClient> _media = new();
    private readonly RentalEvidenceAccessService _service;

    public RentalEvidenceAccessServiceTests()
    {
        _order = CreateOrder(_renterId, _supplierId);
        _order.Items.First().UnitAssignments.First()
            .RecordHandover(Guid.NewGuid(), UnitCondition.Normal, "Máy chạy tốt", [_handoverMediaId]);

        _orders.Setup(r => r.GetByItemIdWithDetailsAsync(ItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_order);
        _media.Setup(m => m.CreatePrivateReadAccessAsync(It.IsAny<Guid>(), It.IsAny<CreateMediaAccessRequest>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, CreateMediaAccessRequest, CancellationToken>((id, _, _) => Task.FromResult(
                Result.Success(new MediaAccessDto(id, $"https://media.test/{id}?sig=x", DateTimeOffset.UtcNow.AddMinutes(5)))));

        _service = new RentalEvidenceAccessService(_orders.Object, _media.Object);
    }

    private Guid ItemId => _order.Items.First().Id;

    public static TheoryData<string> Participants => ["renter", "supplier", "admin"];

    [Theory]
    [MemberData(nameof(Participants))]
    public async Task Participant_GetsSignedReadUrl(string who)
    {
        IRentalCallerContext caller = who switch
        {
            "renter" => UserCaller(_renterId),
            "supplier" => UserCaller(_supplierId),
            _ => UserCaller(Guid.NewGuid(), roles: ["admin"])
        };

        var result = await _service.CreateReadAccessAsync(ItemId, _handoverMediaId, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(_handoverMediaId, result.Value.MediaId);
        _media.Verify(m => m.CreatePrivateReadAccessAsync(_handoverMediaId,
            It.Is<CreateMediaAccessRequest>(r => r.Action == "read" && !r.Download), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stranger_UnrelatedMedia_AndMissingItem_AllGetSameNotFound_WithoutCallingMedia()
    {
        _orders.Setup(r => r.GetByItemIdWithDetailsAsync(It.Is<Guid>(id => id != ItemId), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RentalOrder?)null);

        var stranger = await _service.CreateReadAccessAsync(ItemId, _handoverMediaId, UserCaller(Guid.NewGuid()));
        var unrelatedMedia = await _service.CreateReadAccessAsync(ItemId, Guid.NewGuid(), UserCaller(_renterId));
        var missingItem = await _service.CreateReadAccessAsync(Guid.NewGuid(), _handoverMediaId, UserCaller(_renterId));

        Assert.Equal("Media.EvidenceNotFound", stranger.Error.Code);
        Assert.Equal(ErrorType.NotFound, stranger.Error.Type);
        Assert.Equal(stranger.Error, unrelatedMedia.Error);
        Assert.Equal(stranger.Error, missingItem.Error);
        _media.Verify(m => m.CreatePrivateReadAccessAsync(It.IsAny<Guid>(), It.IsAny<CreateMediaAccessRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ServiceToken_IsRejected_WithoutCallingMedia()
    {
        var result = await _service.CreateReadAccessAsync(ItemId, _handoverMediaId, ServiceCaller());

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.ServiceTokenNotAllowed", result.Error.Code);
        _media.Verify(m => m.CreatePrivateReadAccessAsync(It.IsAny<Guid>(), It.IsAny<CreateMediaAccessRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MediaFailure_IsReturnedUnchanged()
    {
        Error mediaError = Error.Failure("Media.Unavailable", "MediaService không phản hồi.");
        _media.Setup(m => m.CreatePrivateReadAccessAsync(It.IsAny<Guid>(), It.IsAny<CreateMediaAccessRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<MediaAccessDto>(mediaError));

        var result = await _service.CreateReadAccessAsync(ItemId, _handoverMediaId, UserCaller(_renterId));

        Assert.True(result.IsFailure);
        Assert.Equal(mediaError, result.Error);
    }

    private static RentalCallerContext UserCaller(Guid userId, IEnumerable<string>? roles = null)
    {
        List<KeyValuePair<string, string>> claims =
        [
            new(RentalAuthClaims.Subject, userId.ToString("D")),
            new(RentalAuthClaims.ProfileCompleted, "true")
        ];
        if (roles is not null)
        {
            claims.Add(new(RentalAuthClaims.Roles, string.Join(",", roles)));
        }
        return RentalCallerContext.FromClaims(claims);
    }

    private static RentalCallerContext ServiceCaller() => RentalCallerContext.FromClaims(
    [
        new(RentalAuthClaims.Subject, Guid.NewGuid().ToString("D")),
        new(RentalAuthClaims.UserTypeOrAlias, RentalAuthClaims.ServiceIndicator)
    ]);

    private static RentalOrder CreateOrder(Guid renterId, Guid supplierId)
    {
        InitialRentalUnitAssignmentDto unit = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InitialRentalItemDto item = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Máy phát điện xách tay Honda EU10i",
            "Máy phát điện công trường",
            "Kho quận 9",
            1_000m,
            2_000m,
            "grp-01",
            [unit],
            TestListingSnapshots.Default);

        return RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-TEST-EVIDENCE",
            renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            null,
            "v1.0",
            "doc-01",
            "hash-01",
            "op-create-evidence",
            DateTimeOffset.UtcNow,
            [item]).Value;
    }
}
