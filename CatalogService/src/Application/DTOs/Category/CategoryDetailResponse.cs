using System;
using System.Collections.Generic;
using System.Text;

namespace CatalogService.Application.DTOs.Category
{
    public sealed record CategoryDetailResponse(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    bool IsActive
);
}
