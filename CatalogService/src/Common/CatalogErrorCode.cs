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
        public const string InvalidStatus = "EquipmentType.InvalidStatus";
        public const string DuplicateCode = "EquipmentType.DuplicateCode";
        public const string InvalidStateTransition = "EquipmentType.InvalidStateTransition";
        public const string ConcurrencyConflict = "EquipmentType.ConcurrencyConflict";
        public const string CategoryNotFound = "EquipmentType.CategoryNotFound";
        public const string CategoryInactive = "EquipmentType.CategoryInactive";
        public const string InvalidMedia = "EquipmentType.InvalidMedia";
        public const string InvalidPayload = "EquipmentType.InvalidPayload";
        public const string InvalidCode = "EquipmentType.InvalidCode";
        public const string InvalidName = "EquipmentType.InvalidName";
        public const string InvalidDescription = "EquipmentType.InvalidDescription";
        public const string InvalidCategory = "EquipmentType.InvalidCategory";
        public const string InvalidCategories = "EquipmentType.InvalidCategories";
        public const string SchemaNotFound = "EquipmentType.SchemaNotFound";
        public const string DraftAlreadyExists = "EquipmentType.DraftAlreadyExists";
        public const string SchemaAlreadyPublished = "EquipmentType.SchemaAlreadyPublished";
        public const string InvalidSchemaVersion = "EquipmentType.InvalidSchemaVersion";
        public const string SchemaRequired = "EquipmentType.SchemaRequired";
        public const string InvalidSchemaDefinition = "EquipmentType.InvalidSchemaDefinition";
    }

    public static class Category
    {
        public const string NotFound = "Category.NotFound";
        public const string InvalidName = "Category.InvalidName";
        public const string InvalidDescription = "Category.InvalidDescription";
    }

    public static class Brand
    {
        public const string NotFound = "Brand.NotFound";
        public const string InvalidKeyword = "Brand.InvalidKeyword";
        public const string InvalidSort = "Brand.InvalidSort";
        public const string InvalidQuery = "Brand.InvalidQuery";
        public const string DuplicateCode = "Brand.DuplicateCode";
        public const string DuplicateSlug = "Brand.DuplicateSlug";
        public const string ConcurrencyConflict = "Brand.ConcurrencyConflict";
        public const string InvalidPayload = "Brand.InvalidPayload";
        public const string InvalidCode = "Brand.InvalidCode";
        public const string InvalidName = "Brand.InvalidName";
        public const string InvalidSlug = "Brand.InvalidSlug";
        public const string InvalidLogo = "Brand.InvalidLogo";
        public const string InvalidId = "Brand.InvalidId";
    }
    public static class Media
    {
        public const string NotFound = "Media.NotFound";
        public const string NotReady = "Media.NotReady";
        public const string InvalidContentType = "Media.InvalidContentType";
        public const string Forbidden = "Media.Forbidden";
        public const string ServiceUnavailable = "Media.ServiceUnavailable";
    }


    public static class Catalog
    {
        public const string Uncategorized = "Catalog.Uncategorized";
        public const string InvalidRequest = "Catalog.InvalidRequest";
        public const string Unauthorized = "Catalog.Unauthorized";
        public const string Forbidden = "Catalog.Forbidden";
    }
}
