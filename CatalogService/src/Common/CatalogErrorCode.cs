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

    public static class Catalog
    {
        public const string Uncategorized = "Catalog.Uncategorized";
    }
}
