namespace Dprental.AuthService.Application.Validation;

/// <summary>
/// Mật khẩu phổ biến (từ 8 ký tự trở lên — ngắn hơn đã bị chặn bởi độ dài) theo các danh sách mật khẩu bị lộ
/// thường gặp, kèm các mẫu tiếng Việt và tên hệ thống. So sánh không phân biệt hoa thường.
/// </summary>
internal static class CommonPasswords
{
    private static readonly HashSet<string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        // Dãy số và bàn phím
        "12345678", "123456789", "1234567890", "0123456789", "87654321", "987654321", "9876543210",
        "11111111", "111111111", "1111111111", "00000000", "000000000", "0000000000", "22222222",
        "66666666", "88888888", "99999999", "12341234", "11223344", "12121212", "123123123",
        "123321123", "147258369", "123456123", "1q2w3e4r", "1q2w3e4r5t", "1qaz2wsx", "qwertyui",
        "qwertyuiop", "asdfghjk", "asdfghjkl", "zxcvbnm1", "qwerty12", "qwerty123", "qwerty1234",
        "1234qwer", "qazwsxedc", "abcd1234", "abc12345", "abcdefgh", "a1b2c3d4", "aaaaaaaa",
        "zzzzzzzz", "1234abcd", "12345abc", "12345678a", "a12345678", "123456789a",

        // Từ tiếng Anh hay dùng
        "password", "password1", "password12", "password123", "passw0rd", "p@ssw0rd", "p@ssword",
        "iloveyou", "iloveyou1", "sunshine", "princess", "football", "baseball", "superman",
        "whatever", "trustno1", "starwars", "michelle", "jennifer", "computer", "internet",
        "welcome1", "welcome123", "letmein1", "changeme", "admin123", "admin1234", "administrator",
        "qwe12345", "master123", "dragon123", "monkey123", "shadow123", "football1", "baseball1",
        "loveyou1", "hello123", "hello1234", "secret123", "test1234", "testing123", "default1",

        // Mẫu tiếng Việt
        "matkhau", "matkhau1", "matkhau12", "matkhau123", "matkhau1234", "anhyeuem", "anhyeuem1",
        "anhyeuem123", "emyeuanh", "emyeuanh1", "emyeuanh123", "yeuemnhieu", "iloveyouvn",
        "vietnam1", "vietnam123", "saigon123", "hanoi123", "123456aa", "123456abc", "abc123456",
        "zxcvbnm123",

        // Tên hệ thống
        "dprental", "dprental1", "dprental123", "dprental2026", "admin@123", "admin@1234",
    };

    public static bool Contains(string password) => Values.Contains(password);
}
