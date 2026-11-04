using System.Security.Cryptography;
using System.Text;
using Dprental.AuthService.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Tra cứu nội bộ tổ chức theo chủ listing.</summary>
[ApiController]
[Route("internal/supplier-organizations")]
[AllowAnonymous]
public sealed class InternalSupplierLookupController(SupplierContactService service, IConfiguration configuration) : ControllerBase
{
    /// <summary>Chỉ trả ID của tổ chức duy nhất đang hoạt động.</summary>
    [HttpGet("by-owner/{ownerUserId:guid}")]
    public async Task<IActionResult> Get(Guid ownerUserId, CancellationToken cancellationToken)
    {
        var expected = configuration["InventorySupplierLookup:ApiKey"];
        var provided = Request.Headers["X-Inventory-Lookup-Key"];
        if (string.IsNullOrWhiteSpace(expected) || provided.Count != 1 || string.IsNullOrWhiteSpace(provided[0]))
            return Unauthorized();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided[0]!);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes)) return Unauthorized();
        if (ownerUserId == Guid.Empty) return NotFound();
        var ids = await service.FindActiveSupplierIdsByOwnerAsync(ownerUserId, cancellationToken);
        if (ids.Count == 0) return NotFound();
        if (ids.Count != 1) return Conflict();
        return Ok(new { organizationId = ids[0] });
    }
}
