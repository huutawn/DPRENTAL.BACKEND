using Dprental.InventoryService.Application.Validation;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

// namespace: copy dòng namespace từ file EquipmentListingServiceTests.cs cùng thư mục

public class ListingDepositValidatorTests
{
    [Theory]
    [InlineData(300_001, 300_000, true)]   // lớn hơn 1 đồng: hợp lệ
    [InlineData(2_000_000, 300_000, true)]
    [InlineData(300_000, 300_000, false)]  // bằng giá thuê: lỗi
    [InlineData(299_999, 300_000, false)]
    [InlineData(0, 300_000, false)]        // cọc mặc định 0: lỗi
    public void DepositMustBeGreaterThanDailyRate(decimal deposit, decimal dailyRate, bool expectedValid)
    {
        var result = ListingDepositValidator.ValidateDepositExceedsDailyRate(deposit, dailyRate);

        Assert.Equal(expectedValid, result.IsSuccess);
        if (!expectedValid)
        {
            Assert.Equal("DEPOSIT_NOT_GREATER_THAN_DAILY_RATE", result.Error.Code);
        }
    }
}