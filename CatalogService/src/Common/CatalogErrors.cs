namespace Dprental.CatalogService.Common;

/// <summary>Catalog lỗi tập trung authoritative của CatalogService, đảm bảo toàn bộ title, detail và HTTP status đồng nhất.</summary>
public static class CatalogErrors
{
    public static class Paging
    {
        public static readonly Error InvalidPage =
            Error.Validation(CatalogErrorCode.Paging.InvalidPage, "Số trang phải lớn hơn 0.");

        public static readonly Error InvalidPageSize =
            Error.Validation(CatalogErrorCode.Paging.InvalidPageSize, "Kích thước trang phải từ 1 đến 100.");

        public static readonly Error Overflow =
            Error.Validation(CatalogErrorCode.Paging.Overflow, "Trang yêu cầu vượt quá giới hạn hỗ trợ.");
    }

    public static class Equipment
    {
        public static readonly Error NotFound =
            Error.NotFound(CatalogErrorCode.Equipment.NotFound, "Thiết bị không tồn tại hoặc chưa được công khai.");

        public static readonly Error InvalidKeyword =
            Error.Validation(CatalogErrorCode.Equipment.InvalidKeyword, "Từ khóa tối đa 100 ký tự.");

        public static readonly Error InvalidSort =
            Error.Validation(CatalogErrorCode.Equipment.InvalidSort, "sort chỉ nhận: newest, price_asc, price_desc.");

        public static readonly Error InvalidMinPrice =
            Error.Validation(CatalogErrorCode.Equipment.InvalidMinPrice, "minPrice không được âm.");

        public static readonly Error InvalidMaxPrice =
            Error.Validation(CatalogErrorCode.Equipment.InvalidMaxPrice, "maxPrice không được âm.");

        public static readonly Error InvalidPriceRange =
            Error.Validation(CatalogErrorCode.Equipment.InvalidPriceRange, "minPrice không được lớn hơn maxPrice.");
    }

    public static class Catalog
    {
        public static readonly Error Uncategorized =
            Error.Failure(CatalogErrorCode.Catalog.Uncategorized, "Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.");
    }
}
