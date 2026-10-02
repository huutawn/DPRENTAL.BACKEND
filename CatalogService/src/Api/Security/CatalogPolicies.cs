namespace Dprental.CatalogService.Api.Security;

/// <summary>Tên authorization policy của các API quản lý Catalog.</summary>
internal static class CatalogPolicies
{
    /// <summary>Quản lý equipment type: Admin hoặc Owner; phạm vi organization được kiểm tra ở service.</summary>
    public const string EquipmentManage = "CatalogEquipmentManage";

    /// <summary>Quản lý category: chỉ Admin.</summary>
    public const string CategoryManage = "CatalogCategoryManage";
}
