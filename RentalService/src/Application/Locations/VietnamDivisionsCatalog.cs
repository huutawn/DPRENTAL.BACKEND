using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.RentalService.Application.Locations;

public sealed class VietnamDivisionsCatalog : IVietnamDivisionsCatalog
{
    private readonly Dictionary<string, ProvinceDivision> _provincesByCode;
    private readonly Dictionary<(string ProvinceCode, string WardCode), (string ProvinceName, string WardName)> _wardsByProvinceAndWardCode;
    private readonly List<ProvinceDivision> _allProvinces;
    private readonly Dictionary<string, string> _codeByNormalizedName;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public VietnamDivisionsCatalog()
    {
        var assembly = typeof(VietnamDivisionsCatalog).Assembly;
        var resourceName = "RentalService.Application.Locations.vietnam-divisions.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Không tìm thấy tài nguyên nhúng '{resourceName}'.");

        var model = JsonSerializer.Deserialize<JsonDivisionsRoot>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("Không thể đọc dữ liệu phân cấp hành chính Việt Nam.");

        _provincesByCode = new Dictionary<string, ProvinceDivision>(StringComparer.OrdinalIgnoreCase);
        _wardsByProvinceAndWardCode = new Dictionary<(string, string), (string, string)>(new ProvinceWardEqualityComparer());
        _allProvinces = new List<ProvinceDivision>();
        _codeByNormalizedName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

                string normFullName = NormalizeName(pName);
                if (!string.IsNullOrEmpty(normFullName))
                {
                    _codeByNormalizedName[normFullName] = pCode;
                }

                string normCleanName = CleanPrefixes(normFullName);
                if (!string.IsNullOrEmpty(normCleanName))
                {
                    _codeByNormalizedName[normCleanName] = pCode;
                }
            }
        }

        // Bổ sung mã tỉnh Bình Dương nếu chưa có trong json phân cấp
        if (!_provincesByCode.ContainsKey("74"))
        {
            var bdDivision = new ProvinceDivision("74", "Tỉnh Bình Dương", []);
            _provincesByCode["74"] = bdDivision;
            _allProvinces.Add(bdDivision);
            _codeByNormalizedName[NormalizeName("Bình Dương")] = "74";
            _codeByNormalizedName[NormalizeName("Tỉnh Bình Dương")] = "74";
            _codeByNormalizedName[CleanPrefixes(NormalizeName("Bình Dương"))] = "74";
        }

        // Đăng ký alias phổ biến
        RegisterAlias("hcm", "79");
        RegisterAlias("tp.hcm", "79");
        RegisterAlias("tphcm", "79");
        RegisterAlias("sg", "79");
        RegisterAlias("sài gòn", "79");
        RegisterAlias("sai gon", "79");
        RegisterAlias("hồ chí minh", "79");
        RegisterAlias("ho chi minh", "79");
        RegisterAlias("thành phố hồ chí minh", "79");
        RegisterAlias("tp hồ chí minh", "79");
        RegisterAlias("tp. hồ chí minh", "79");

        RegisterAlias("hn", "01");
        RegisterAlias("tp.hn", "01");
        RegisterAlias("tphn", "01");
        RegisterAlias("hà nội", "01");
        RegisterAlias("ha noi", "01");
        RegisterAlias("thành phố hà nội", "01");
        RegisterAlias("tp hà nội", "01");
        RegisterAlias("tp. hà nội", "01");

        RegisterAlias("đn", "48");
        RegisterAlias("dn", "48");
        RegisterAlias("đà nẵng", "48");
        RegisterAlias("da nang", "48");
        RegisterAlias("thành phố đà nẵng", "48");
        RegisterAlias("tp đà nẵng", "48");
        RegisterAlias("tp. đà nẵng", "48");

        RegisterAlias("hp", "31");
        RegisterAlias("hải phòng", "31");
        RegisterAlias("hai phong", "31");
        RegisterAlias("thành phố hải phòng", "31");

        RegisterAlias("ct", "92");
        RegisterAlias("cần thơ", "92");
        RegisterAlias("can tho", "92");
        RegisterAlias("thành phố cần thơ", "92");

        RegisterAlias("huế", "46");
        RegisterAlias("hue", "46");
        RegisterAlias("thừa thiên huế", "46");
        RegisterAlias("thua thien hue", "46");
        RegisterAlias("thành phố huế", "46");
    }

    private void RegisterAlias(string alias, string code)
    {
        string norm = NormalizeName(alias);
        if (!string.IsNullOrEmpty(norm) && _provincesByCode.ContainsKey(code))
        {
            _codeByNormalizedName[norm] = code;
            string clean = CleanPrefixes(norm);
            if (!string.IsNullOrEmpty(clean))
            {
                _codeByNormalizedName[clean] = code;
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

    public bool TryResolveProvince(string? provinceNameOrCode, out string provinceCode, out string canonicalProvinceName)
    {
        provinceCode = string.Empty;
        canonicalProvinceName = string.Empty;

        if (string.IsNullOrWhiteSpace(provinceNameOrCode))
        {
            return false;
        }

        string raw = provinceNameOrCode.Trim();

        // 1. Kiểm tra trực tiếp theo mã tỉnh
        if (_provincesByCode.TryGetValue(raw, out var prov))
        {
            provinceCode = prov.Code;
            canonicalProvinceName = prov.Name;
            return true;
        }

        if (raw.Length == 1 && _provincesByCode.TryGetValue("0" + raw, out var padded))
        {
            provinceCode = padded.Code;
            canonicalProvinceName = padded.Name;
            return true;
        }

        // 2. Tra theo tên hoặc alias đã chuẩn hóa
        string norm = NormalizeName(raw);
        if (_codeByNormalizedName.TryGetValue(norm, out var matchedCode) &&
            _provincesByCode.TryGetValue(matchedCode, out var matchedProv))
        {
            provinceCode = matchedProv.Code;
            canonicalProvinceName = matchedProv.Name;
            return true;
        }

        string clean = CleanPrefixes(norm);
        if (_codeByNormalizedName.TryGetValue(clean, out var matchedCleanCode) &&
            _provincesByCode.TryGetValue(matchedCleanCode, out var matchedCleanProv))
        {
            provinceCode = matchedCleanProv.Code;
            canonicalProvinceName = matchedCleanProv.Name;
            return true;
        }

        // Không đoán mò nếu không xác định được duy nhất
        return false;
    }

    public IReadOnlyList<ProvinceDivision> GetAllProvinces() => _allProvinces;

    private static string NormalizeName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return input.Trim().ToLowerInvariant();
    }

    private static string CleanPrefixes(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string result = input;
        string[] prefixes = ["thành phố ", "tp. ", "tp.", "tp ", "tỉnh "];
        foreach (var p in prefixes)
        {
            if (result.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                result = result[p.Length..].Trim();
            }
        }
        return result.Trim();
    }

    private sealed class ProvinceWardEqualityComparer : IEqualityComparer<(string ProvinceCode, string WardCode)>
    {
        public bool Equals((string ProvinceCode, string WardCode) x, (string ProvinceCode, string WardCode) y)
        {
            return string.Equals(x.ProvinceCode, y.ProvinceCode, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.WardCode, y.WardCode, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string ProvinceCode, string WardCode) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProvinceCode),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.WardCode));
        }
    }

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
}
