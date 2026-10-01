using System.ComponentModel.DataAnnotations;

namespace Dprental.CatalogService.Application.Options;

public sealed class CatalogDiscoveryOptions
{
    public const string SectionName = "CatalogDiscovery";

    [Range(1, 200)]
    public int MaxKeywordLength { get; set; } = 100;
}
