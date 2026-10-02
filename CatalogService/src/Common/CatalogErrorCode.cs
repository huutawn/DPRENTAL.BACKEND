namespace Dprental.CatalogService.Common;

/// <summary>Catalog mã lỗi tập trung của CatalogService.</summary>
public static class CatalogErrorCode
{
    public static class Paging
    {
        public const string InvalidPage = "Paging.InvalidPage";
        public const string InvalidPageSize = "Paging.InvalidPageSize";
        public const string Overflow = "Paging.Overflow";
    }

    public static class EquipmentType
    {
        public const string NotFound = "EquipmentType.NotFound";
        public const string InvalidKeyword = "EquipmentType.InvalidKeyword";
        public const string InvalidSort = "EquipmentType.InvalidSort";
        public const string InvalidQuery = "EquipmentType.InvalidQuery";
        public const string DuplicateCode = "EquipmentType.DuplicateCode";
        public const string InvalidStateTransition = "EquipmentType.InvalidStateTransition";
        public const string ConcurrencyConflict = "EquipmentType.ConcurrencyConflict";
        public const string CategoryNotFound = "EquipmentType.CategoryNotFound";
        public const string CategoryInactive = "EquipmentType.CategoryInactive";
        public const string InvalidMedia = "EquipmentType.InvalidMedia";
        public const string MediaNotFound = "EquipmentType.MediaNotFound";
        public const string MediaNotReady = "EquipmentType.MediaNotReady";
        public const string InvalidPayload = "EquipmentType.InvalidPayload";
        public const string InvalidCode = "EquipmentType.InvalidCode";
        public const string InvalidName = "EquipmentType.InvalidName";
        public const string InvalidDescription = "EquipmentType.InvalidDescription";
        public const string InvalidCategory = "EquipmentType.InvalidCategory";
    }

    public static class Category
    {
        public const string NotFound = "Category.NotFound";
        public const string InvalidName = "Category.InvalidName";
        public const string InvalidDescription = "Category.InvalidDescription";
    }

    public static class Catalog
    {
        public const string Uncategorized = "Catalog.Uncategorized";
        public const string InvalidRequest = "Catalog.InvalidRequest";
        public const string Unauthorized = "Catalog.Unauthorized";
        public const string Forbidden = "Catalog.Forbidden";
        public const string MediaServiceUnavailable = "Catalog.MediaServiceUnavailable";
    }
}
