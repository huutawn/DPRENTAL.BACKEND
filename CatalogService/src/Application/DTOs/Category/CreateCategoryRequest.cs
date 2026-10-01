using System;
using System.Collections.Generic;
using System.Text;

namespace CatalogService.Application.DTOs.Category
{
    public sealed record CreateCategoryRequest(
    string Name,
    string? Description
);
}
