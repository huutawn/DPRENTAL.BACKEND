using Dprental.AuthService.Api.Controllers;
using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class InternalSupplierLookupTests
{
    private sealed class Repository : IOrganizationLookupRepository
    {
        public IReadOnlyList<Guid> Values { get; set; } = [];
        public Task<OrganizationPublicRecord?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<OrganizationPublicRecord?>(null);
        public Task<IReadOnlyList<Guid>> FindActiveSupplierIdsByOwnerAsync(Guid ownerUserId, CancellationToken ct = default) => Task.FromResult(Values);
    }
    private sealed class Media : IMediaUrlResolver
    {
        public string? ResolvePublicUrl(Guid? id) => null;
        public Task<Result<MediaReadUrlDto>> ResolvePrivateAccessUrlAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
    }
    private static InternalSupplierLookupController Create(Repository repository, string? suppliedKey)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["InventorySupplierLookup:ApiKey"] = "test-secret" }).Build();
        var controller = new InternalSupplierLookupController(new SupplierContactService(repository, new Media()), config)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        if (suppliedKey != null) controller.Request.Headers["X-Inventory-Lookup-Key"] = suppliedKey;
        return controller;
    }
    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task MissingOrInvalidKeyIsRejected(string? key)
    {
        Assert.IsType<UnauthorizedResult>(await Create(new Repository(), key).Get(Guid.NewGuid(), default));
    }
    [Fact]
    public async Task OnlyUniqueOrganizationIsReturned()
    {
        var repository = new Repository();
        var controller = Create(repository, "test-secret");
        Assert.IsType<NotFoundResult>(await controller.Get(Guid.NewGuid(), default));
        repository.Values = [Guid.NewGuid(), Guid.NewGuid()];
        Assert.IsType<ConflictResult>(await controller.Get(Guid.NewGuid(), default));
        repository.Values = [Guid.NewGuid()];
        Assert.IsType<OkObjectResult>(await controller.Get(Guid.NewGuid(), default));
    }
}
