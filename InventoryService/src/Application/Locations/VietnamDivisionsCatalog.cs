using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.Locations;

public sealed class VietnamDivisionsCatalog : IVietnamDivisionsCatalog
{
    private readonly Dictionary<string, ProvinceDivision> _provincesByCode;
    private readonly Dictionary<(string ProvinceCode, string WardCode), (string ProvinceName, string WardName)> _wardsByProvinceAndWardCode;
    private readonly List<ProvinceDivision> _allProvinces;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };


    public VietnamDivisionsCatalog()
    {
        var assembly = typeof(VietnamDivisionsCatalog).Assembly;
        var resourceName = "InventoryService.Application.Locations.vietnam-divisions.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Không tìm thấy tài nguyên nhúng '{resourceName}'.");

        var model = JsonSerializer.Deserialize<JsonDivisionsRoot>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("Không thể đọc dữ liệu phân cấp hành chính Việt Nam.");

        _provincesByCode = new Dictionary<string, ProvinceDivision>(StringComparer.OrdinalIgnoreCase);
        _wardsByProvinceAndWardCode = new Dictionary<(string, string), (string, string)>(new ProvinceWardEqualityComparer());
        _allProvinces = new List<ProvinceDivision>();

        if (model.Provinces != null)
        {
            foreach (var p in model.Provinces)
            {
                if (string.IsNullOrWhiteSpace(p.Code) || string.IsNullOrWhiteSpace(p.Name))
                {
                    continue;
                }

                var pCode = p.Code.Trim();
                var pName = p.Name.Trim();
                var wards = new List<WardDivision>();

                if (p.Wards != null)
                {
                    foreach (var w in p.Wards)
                    {
                        if (string.IsNullOrWhiteSpace(w.Code) || string.IsNullOrWhiteSpace(w.Name))
                        {
                            continue;
                        }

                        var wCode = w.Code.Trim();
                        var wName = w.Name.Trim();
                        wards.Add(new WardDivision(wCode, wName));
                        _wardsByProvinceAndWardCode[(pCode, wCode)] = (pName, wName);
                    }
                }

                var provinceDivision = new ProvinceDivision(pCode, pName, wards.AsReadOnly());
                _provincesByCode[pCode] = provinceDivision;
                _allProvinces.Add(provinceDivision);
            }
        }
    }

    public bool IsValidPair(string? provinceCode, string? wardCode, out string canonicalProvinceName, out string canonicalWardName)
    {
        canonicalProvinceName = string.Empty;
        canonicalWardName = string.Empty;

        if (string.IsNullOrWhiteSpace(provinceCode) || string.IsNullOrWhiteSpace(wardCode))
        {
            return false;
        }

        if (_wardsByProvinceAndWardCode.TryGetValue((provinceCode.Trim(), wardCode.Trim()), out var names))
        {
            canonicalProvinceName = names.ProvinceName;
            canonicalWardName = names.WardName;
            return true;
        }

        return false;
    }

    public bool IsValidProvince(string? provinceCode, out string canonicalProvinceName)
    {
        canonicalProvinceName = string.Empty;

        if (string.IsNullOrWhiteSpace(provinceCode))
        {
            return false;
        }

        if (_provincesByCode.TryGetValue(provinceCode.Trim(), out var province))
        {
            canonicalProvinceName = province.Name;
            return true;
        }

        return false;
    }

    public IReadOnlyList<ProvinceDivision> GetAllProvinces() => _allProvinces.AsReadOnly();

    private sealed class JsonDivisionsRoot
    {
        [JsonPropertyName("provinces")]
        public List<JsonProvince>? Provinces { get; set; }
    }

    private sealed class JsonProvince
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("wards")]
        public List<JsonWard>? Wards { get; set; }
    }

    private sealed class JsonWard
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class ProvinceWardEqualityComparer : IEqualityComparer<(string ProvinceCode, string WardCode)>
    {
        public bool Equals((string ProvinceCode, string WardCode) x, (string ProvinceCode, string WardCode) y) =>
            string.Equals(x.ProvinceCode, y.ProvinceCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.WardCode, y.WardCode, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ProvinceCode, string WardCode) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProvinceCode),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.WardCode));
    }
}
