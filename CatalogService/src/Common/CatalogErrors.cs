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

    public static class Category
    {
        /// <summary>Category là dữ liệu trong body, nên sai thì là 400 chứ không phải 404.</summary>
        public static readonly Error NotFound =
            Error.Validation(CatalogErrorCode.Category.NotFound, "Category không tồn tại.");

        public static readonly Error Inactive =
            Error.Validation(CatalogErrorCode.Category.Inactive, "Category đã ngừng hoạt động.");
    }

    public static class EquipmentType
    {
        public static readonly Error NotFound =
            Error.NotFound(CatalogErrorCode.EquipmentType.NotFound, "Equipment type không tồn tại.");

        public static readonly Error CodeRequired =
            Error.Validation(CatalogErrorCode.EquipmentType.CodeRequired, "Mã equipment type là bắt buộc.");

        public static readonly Error CodeInvalid =
            Error.Validation(CatalogErrorCode.EquipmentType.CodeInvalid, "Mã chỉ gồm chữ cái không dấu, chữ số, dấu gạch ngang, gạch dưới và dấu chấm; bắt đầu bằng chữ hoặc số.");

        public static readonly Error CodeAlreadyExists =
            Error.Conflict(CatalogErrorCode.EquipmentType.CodeAlreadyExists, "Mã equipment type đã tồn tại.");

        public static readonly Error CodeLocked =
            Error.Conflict(CatalogErrorCode.EquipmentType.CodeLocked, "Không thể đổi mã của equipment type đã từng được publish.");

        public static readonly Error NameRequired =
            Error.Validation(CatalogErrorCode.EquipmentType.NameRequired, "Tên equipment type là bắt buộc.");

        public static readonly Error NameAlreadyExists =
            Error.Conflict(CatalogErrorCode.EquipmentType.NameAlreadyExists, "Đã có equipment type cùng tên trong category này.");

        public static readonly Error AlreadyPublished =
            Error.Conflict(CatalogErrorCode.EquipmentType.AlreadyPublished, "Equipment type đang ở trạng thái Published.");

        public static readonly Error NotPublished =
            Error.Conflict(CatalogErrorCode.EquipmentType.NotPublished, "Chỉ có thể disable equipment type đang ở trạng thái Published.");

        public static readonly Error ConcurrentUpdate =
            Error.Conflict(CatalogErrorCode.EquipmentType.ConcurrentUpdate, "Equipment type vừa được thay đổi bởi một thao tác khác. Hãy tải lại và thử lại.");

        /// <summary>Liệt kê các điều kiện publish chưa đạt để Admin sửa một lượt.</summary>
        public static Error NotPublishable(IEnumerable<string> reasons) =>
            Error.Unprocessable(
                CatalogErrorCode.EquipmentType.NotPublishable,
                "Equipment type chưa đủ điều kiện publish: " + string.Join("; ", reasons) + ".");
    }

    public static class Catalog
    {
        public static readonly Error Uncategorized =
            Error.Failure(CatalogErrorCode.Catalog.Uncategorized, "Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.");
    }
}
