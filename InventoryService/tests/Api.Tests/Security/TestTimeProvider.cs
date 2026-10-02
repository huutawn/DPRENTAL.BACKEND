namespace Dprental.InventoryService.Api.Tests.Security;

/// <summary>
/// TimeProvider tùy chỉnh hỗ trợ dịch chuyển hoặc cố định thời gian UTC phục vụ kiểm thử.
/// </summary>
public sealed class TestTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = initialUtcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);

    public void Set(DateTimeOffset newUtcNow) => UtcNow = newUtcNow;
}
