using System;
using System.Collections.Generic;
using System.Text;

namespace CatalogService.Application.DTOs.Category
{
    public sealed record UpdateCategoryRequest(
    string? Name,
    string? Description,
    bool? IsActive
);
}
