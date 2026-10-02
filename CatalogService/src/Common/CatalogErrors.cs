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

    public static class EquipmentType
    {
        public static readonly Error NotFound =
            Error.NotFound(CatalogErrorCode.EquipmentType.NotFound, "Loại thiết bị không tồn tại hoặc chưa được công khai.");

        public static readonly Error InvalidKeyword =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidKeyword, "Từ khóa tối đa 100 ký tự.");

        public static readonly Error InvalidSort =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidSort, "sort chỉ nhận: newest, name_asc, name_desc.");

        public static readonly Error InvalidQuery =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidQuery, "Tham số truy vấn không hợp lệ.");

        public static readonly Error DuplicateCode =
            Error.Conflict(CatalogErrorCode.EquipmentType.DuplicateCode, "Mã thiết bị đã tồn tại trong Catalog.");

        public static readonly Error InvalidStateTransition =
            Error.Conflict(CatalogErrorCode.EquipmentType.InvalidStateTransition, "Chuyển đổi trạng thái không hợp lệ.");

        public static readonly Error ConcurrencyConflict =
            Error.Conflict(CatalogErrorCode.EquipmentType.ConcurrencyConflict, "Dữ liệu đã bị thay đổi bởi tác vụ khác. Vui lòng tải lại trang.");

        public static readonly Error CategoryNotFound =
            Error.Validation(CatalogErrorCode.EquipmentType.CategoryNotFound, "Danh mục không tồn tại.");

        public static readonly Error CategoryInactive =
            Error.Validation(CatalogErrorCode.EquipmentType.CategoryInactive, "Danh mục đang ở trạng thái không hoạt động.");

        public static readonly Error InvalidMedia =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidMedia, "Danh sách media không hợp lệ.");

        public static readonly Error InvalidPayload =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidPayload, "Dữ liệu yêu cầu không hợp lệ.");

        public static readonly Error InvalidCode =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidCode, "Mã thiết bị không được để trống và tối đa 50 ký tự.");

        public static readonly Error InvalidName =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidName, "Tên thiết bị không được để trống và tối đa 200 ký tự.");

        public static readonly Error InvalidDescription =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidDescription, "Mô tả tối đa 4000 ký tự.");

        public static readonly Error InvalidCategory =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidCategory, "CategoryId không hợp lệ.");
    }

    public static class Category
    {
        public static readonly Error NotFound =
            Error.NotFound(CatalogErrorCode.Category.NotFound, "Danh mục không tồn tại hoặc đang ở trạng thái không hoạt động.");

        public static readonly Error InvalidName =
            Error.Validation(CatalogErrorCode.Category.InvalidName, "Tên danh mục không được để trống và tối đa 150 ký tự.");

        public static readonly Error InvalidDescription =
            Error.Validation(CatalogErrorCode.Category.InvalidDescription, "Mô tả danh mục tối đa 1000 ký tự.");
    }

    public static class Catalog
    {
        public static readonly Error Uncategorized =
            Error.Failure(CatalogErrorCode.Catalog.Uncategorized, "Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.");

        public static readonly Error InvalidRequest =
            Error.Validation(CatalogErrorCode.Catalog.InvalidRequest, "Dữ liệu yêu cầu không đúng định dạng.");

        public static readonly Error Unauthorized =
            Error.Unauthorized(CatalogErrorCode.Catalog.Unauthorized, "Yêu cầu chưa được xác thực hoặc token không hợp lệ.");

        public static readonly Error Forbidden =
            Error.Forbidden(CatalogErrorCode.Catalog.Forbidden, "Bạn không có quyền thực hiện thao tác này.");
    }
}
