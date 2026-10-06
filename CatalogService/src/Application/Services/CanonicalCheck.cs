namespace Dprental.CatalogService.Application.Services;

public enum CanonicalCheck
{
    Ok,
    CategoryRequired,
    CategoryNotFound,
    CategoryInactive,
    DuplicateName
}