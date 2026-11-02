using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

public sealed class OrganizationLookupService(IOrganizationService ownerService, IOrganizationLookupRepository repository,
    IMediaUrlResolver mediaUrlResolver)
{
    public async Task<Result<object>> GetByIdAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var ownerResult = await ownerService.GetByIdAsync(actorId, organizationId, cancellationToken);
        if (ownerResult.IsSuccess) return Result.Success<object>(ownerResult.Value);
        if (ownerResult.Error != AuthErrors.Organization.CrossOrganizationAccessDenied)
            return Result.Failure<object>(ownerResult.Error);
        var record = await repository.FindByIdAsync(organizationId, cancellationToken);
        if (record is null || record.Status != OrganizationStatus.Active)
            return Result.Failure<object>(AuthErrors.Organization.OrganizationNotFound);
        return Result.Success<object>(ToPublic(record));
    }

    public async Task<Result<OrganizationPublicResponse>> GetByOwnerAsync(Guid actorId, Guid ownerId, CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty || ownerId == Guid.Empty)
            return Result.Failure<OrganizationPublicResponse>(AuthErrors.Organization.OrganizationNotFound);
        var records = await repository.FindByOwnerAsync(ownerId, cancellationToken);
        if (records.Count > 1)
            return Result.Failure<OrganizationPublicResponse>(Error.Conflict("Organization.AmbiguousOwner", "Chủ tài khoản có nhiều tổ chức; không thể xác định tổ chức của tin đăng."));
        if (records.Count == 0 || (actorId != ownerId && records[0].Status != OrganizationStatus.Active))
            return Result.Failure<OrganizationPublicResponse>(AuthErrors.Organization.OrganizationNotFound);
        return Result.Success(ToPublic(records[0]));
    }

    private OrganizationPublicResponse ToPublic(OrganizationPublicRecord record) => new(record.Id, record.Name, record.Slug,
        record.Type, record.Status, record.ContactPhone, record.LogoMediaId, mediaUrlResolver.ResolvePublicUrl(record.LogoMediaId));
}
