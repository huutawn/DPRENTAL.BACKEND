namespace Dprental.CatalogService.Domain.ValueObjects;

/// <summary>Contract dùng "integer", ánh xạ ở tầng DTO. Đặt WholeNumber để không dính quy tắc CA1720.</summary>
public enum SpecDataType { Number = 1, WholeNumber = 2, Boolean = 3, Enum = 4, Text = 5 }