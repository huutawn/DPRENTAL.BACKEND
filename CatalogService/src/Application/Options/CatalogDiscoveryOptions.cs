using System.ComponentModel.DataAnnotations;

namespace Dprental.CatalogService.Application.Options;

public sealed class CatalogDiscoveryOptions {
    public const string SectionName = "CatalogDiscovery";

    [Range(1, 100)] public int DefaultPageSize { get; set; } = 20;
    [Range(1, 500)] public int MaxPageSize { get; set; } = 100;
    [Range(1, 200)] public int MaxKeywordLength { get; set; } = 100;
}


