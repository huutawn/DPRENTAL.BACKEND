namespace Dprental.CatalogService.Api.Security;

/// <summary>Tên authorization policy của các API quản lý Catalog.</summary>
internal static class CatalogPolicies
{
    /// <summary>Quản lý equipment type: chỉ Admin (role adm).</summary>
    public const string EquipmentManage = "CatalogEquipmentManage";

    /// <summary>Quản lý category: chỉ Admin.</summary>
    public const string CategoryManage = "CatalogCategoryManage";

    /// <summary>Quản lý brand: chỉ Admin (role adm).</summary>
    public const string BrandManage = "CatalogBrandManage";
}
