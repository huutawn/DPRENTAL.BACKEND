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

        public static readonly Error InvalidStatus =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidStatus, "status chỉ nhận: Draft, Published, Disabled.");

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
        public static readonly Error CategoryRequired =
    Error.Validation(
        CatalogErrorCode.EquipmentType.CategoryRequired,
        "Loại thiết bị phải có ít nhất một danh mục.");

        public static readonly Error DuplicateCanonicalName =
            Error.Conflict(
                CatalogErrorCode.EquipmentType.DuplicateCanonicalName,
                "Tên loại thiết bị đã tồn tại trong cùng nhóm danh mục.");

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
        public static readonly Error InvalidCategories =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidCategories, "Danh sách categoryIds không hợp lệ.");

        public static readonly Error SchemaNotFound =
            Error.NotFound(CatalogErrorCode.EquipmentType.SchemaNotFound, "Specification schema không tồn tại.");

        public static readonly Error DraftAlreadyExists =
            Error.Conflict(CatalogErrorCode.EquipmentType.DraftAlreadyExists, "Loại thiết bị đã có một bản nháp schema. Vui lòng cập nhật hoặc publish bản nháp hiện tại.");

        public static readonly Error SchemaAlreadyPublished =
            Error.Conflict(CatalogErrorCode.EquipmentType.SchemaAlreadyPublished, "Schema đã được publish, không thể chỉnh sửa hoặc publish lại.");

        public static readonly Error InvalidSchemaVersion =
            Error.Conflict(CatalogErrorCode.EquipmentType.InvalidSchemaVersion, "Version publish phải lớn hơn version hiện tại.");

        public static readonly Error SchemaRequired =
            Error.Conflict(CatalogErrorCode.EquipmentType.SchemaRequired, "Loại thiết bị cần có specification schema đã publish trước khi publish.");

        public static readonly Error InvalidSchemaDefinition =
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidSchemaDefinition, "Định nghĩa specification schema không hợp lệ.");

        public static Error InvalidSchemaDefinitionWithDetail(string detail) =>
            Error.Validation(CatalogErrorCode.EquipmentType.InvalidSchemaDefinition, detail);

    }

    public static class Category
    {
        public static readonly Error NotFound =
            Error.NotFound(CatalogErrorCode.Category.NotFound, "Danh mục không tồn tại hoặc đang ở trạng thái không hoạt động.");

        public static readonly Error AdminNotFound =
            Error.NotFound(CatalogErrorCode.Category.NotFound, "Danh mục không tồn tại.");

        public static readonly Error InvalidKeyword =
            Error.Validation(CatalogErrorCode.Category.InvalidKeyword, "Từ khóa không được chứa ký tự không hợp lệ và tối đa 100 ký tự.");

        public static readonly Error InvalidSort =
            Error.Validation(CatalogErrorCode.Category.InvalidSort, "sort chỉ nhận: newest, name_asc, name_desc.");

        public static readonly Error InvalidQuery =
            Error.Validation(CatalogErrorCode.Category.InvalidQuery, "Tham số truy vấn không hợp lệ.");

        public static readonly Error InvalidId =
            Error.Validation(CatalogErrorCode.Category.InvalidId, "ID danh mục không hợp lệ.");

        public static readonly Error InvalidName =
            Error.Validation(CatalogErrorCode.Category.InvalidName, "Tên danh mục không được để trống và tối đa 150 ký tự.");

        public static readonly Error InvalidDescription =
            Error.Validation(CatalogErrorCode.Category.InvalidDescription, "Mô tả danh mục tối đa 1000 ký tự.");

        public static readonly Error InvalidImage =
            Error.Validation(CatalogErrorCode.Category.InvalidImage, "Ảnh đại diện danh mục không hợp lệ.");
    }

    public static class Brand
    {
        public static readonly Error NotFound =
            Error.NotFound(CatalogErrorCode.Brand.NotFound, "Thương hiệu không tồn tại hoặc chưa được kích hoạt.");

        public static readonly Error AdminNotFound =
            Error.NotFound(CatalogErrorCode.Brand.NotFound, "Thương hiệu không tồn tại.");

        public static readonly Error InvalidKeyword =
            Error.Validation(CatalogErrorCode.Brand.InvalidKeyword, "Từ khóa không được chứa ký tự không hợp lệ và tối đa 100 ký tự.");
        public static readonly Error InvalidSort =
            Error.Validation(CatalogErrorCode.Brand.InvalidSort, "sort chỉ nhận: newest, name_asc, name_desc.");

        public static readonly Error InvalidQuery =
            Error.Validation(CatalogErrorCode.Brand.InvalidQuery, "Tham số truy vấn không hợp lệ.");

        public static readonly Error DuplicateCode =
            Error.Conflict(CatalogErrorCode.Brand.DuplicateCode, "Mã thương hiệu đã tồn tại trong Catalog.");

        public static readonly Error DuplicateSlug =
            Error.Conflict(CatalogErrorCode.Brand.DuplicateSlug, "Slug thương hiệu đã tồn tại trong Catalog.");

        public static readonly Error ConcurrencyConflict =
            Error.Conflict(CatalogErrorCode.Brand.ConcurrencyConflict, "Dữ liệu đã bị thay đổi bởi tác vụ khác. Vui lòng tải lại trang.");

        public static readonly Error InvalidPayload =
            Error.Validation(CatalogErrorCode.Brand.InvalidPayload, "Dữ liệu yêu cầu không hợp lệ.");

        public static readonly Error InvalidCode =
            Error.Validation(CatalogErrorCode.Brand.InvalidCode, "Mã thương hiệu không được để trống, không chứa ký tự không hợp lệ và tối đa 50 ký tự.");

        public static readonly Error InvalidName =
            Error.Validation(CatalogErrorCode.Brand.InvalidName, "Tên thương hiệu không được để trống, không chứa ký tự không hợp lệ và tối đa 200 ký tự.");
        public static readonly Error InvalidSlug =
            Error.Validation(CatalogErrorCode.Brand.InvalidSlug, "Slug thương hiệu không hợp lệ. Chỉ chấp nhận chữ cái thường, số ASCII phân cách bằng dấu gạch ngang và tối đa 150 ký tự.");

        public static readonly Error InvalidLogo =
            Error.Validation(CatalogErrorCode.Brand.InvalidLogo, "Logo thương hiệu không hợp lệ.");

        public static readonly Error InvalidId =
            Error.Validation(CatalogErrorCode.Brand.InvalidId, "ID thương hiệu không hợp lệ.");
    }
    public static class Media
    {
        public static readonly Error NotFound =
            Error.Validation(CatalogErrorCode.Media.NotFound, "Media không tồn tại.");

        public static readonly Error NotReady =
            Error.Validation(CatalogErrorCode.Media.NotReady, "Media chưa sẵn sàng để sử dụng.");

        public static readonly Error InvalidContentType =
            Error.Validation(CatalogErrorCode.Media.InvalidContentType, "Định dạng media không hợp lệ. Chỉ chấp nhận image/jpeg, image/png, image/webp.");

        public static readonly Error Forbidden =
            Error.Forbidden(CatalogErrorCode.Media.Forbidden, "Người dùng không sở hữu media này.");

        public static readonly Error ServiceUnavailable =
            Error.Unavailable(CatalogErrorCode.Media.ServiceUnavailable, "Dịch vụ MediaService tạm thời không khả dụng.");
    }

    public static class Inventory
    {
        public static readonly Error ServiceUnavailable =
            Error.Unavailable(CatalogErrorCode.Inventory.ServiceUnavailable, "InventoryService không khả dụng hoặc hết thời gian chờ.");

        public static readonly Error Unauthorized =
            Error.Unauthorized(CatalogErrorCode.Inventory.Unauthorized, "Yêu cầu không được xác thực bởi InventoryService.");

        public static readonly Error Forbidden =
            Error.Forbidden(CatalogErrorCode.Inventory.Forbidden, "Truy cập bị từ chối bởi InventoryService.");
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
