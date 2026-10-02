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

    public static class Equipment
    {
        public const string NotFound = "Equipment.NotFound";
        public const string InvalidKeyword = "Equipment.InvalidKeyword";
        public const string InvalidSort = "Equipment.InvalidSort";
        public const string InvalidMinPrice = "Equipment.InvalidMinPrice";
        public const string InvalidMaxPrice = "Equipment.InvalidMaxPrice";
        public const string InvalidPriceRange = "Equipment.InvalidPriceRange";
    }

    public static class Category
    {
        public const string NotFound = "Category.NotFound";
        public const string Inactive = "Category.Inactive";
    }

    public static class EquipmentType
    {
        public const string NotFound = "EquipmentType.NotFound";
        public const string CodeRequired = "EquipmentType.CodeRequired";
        public const string CodeInvalid = "EquipmentType.CodeInvalid";
        public const string CodeAlreadyExists = "EquipmentType.CodeAlreadyExists";
        public const string CodeLocked = "EquipmentType.CodeLocked";
        public const string NameRequired = "EquipmentType.NameRequired";
        public const string NameAlreadyExists = "EquipmentType.NameAlreadyExists";
        public const string AlreadyPublished = "EquipmentType.AlreadyPublished";
        public const string NotPublished = "EquipmentType.NotPublished";
        public const string NotPublishable = "EquipmentType.NotPublishable";
        public const string ConcurrentUpdate = "EquipmentType.ConcurrentUpdate";
    }

    public static class Catalog
    {
        public const string Uncategorized = "Catalog.Uncategorized";
    }
}
