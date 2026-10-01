using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

/// <summary>Quy tắc email (RFC 5321/5322 + HTML5) và chính sách mật khẩu (NIST SP 800-63B) dùng khi ghi dữ liệu.</summary>
public sealed class EmailPasswordPolicyTests
{
    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last@sub.example.com.vn")]
    [InlineData("user+tag@example.io")]
    [InlineData("o'brien_99@my-company.co")]
    [InlineData("  user@example.com  ")]
    public void StrictEmailAcceptsWellFormedAddresses(string email) =>
        Assert.True(new StrictEmailAttribute().IsValid(email));

    [Theory]
    [InlineData("a@b")]
    [InlineData("a@localhost")]
    [InlineData("user@example.c")]
    [InlineData("user@example.123")]
    [InlineData("user@.example.com")]
    [InlineData("user@example..com")]
    [InlineData("user@-example.com")]
    [InlineData("user@example-.com")]
    [InlineData(".user@example.com")]
    [InlineData("user.@example.com")]
    [InlineData("us..er@example.com")]
    [InlineData("us er@example.com")]
    [InlineData("user@exa mple.com")]
    [InlineData("user@@example.com")]
    [InlineData("user@example.com@x.com")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("userexample.com")]
    [InlineData("người@example.com")]
    public void StrictEmailRejectsMalformedAddresses(string email) =>
        Assert.False(new StrictEmailAttribute().IsValid(email));

    [Fact]
    public void StrictEmailEnforcesRfcLengthLimits()
    {
        string longLocalPart = new('a', StrictEmailAttribute.MaxLocalPartLength + 1);
        string longLabel = new('a', StrictEmailAttribute.MaxLabelLength + 1);

        Assert.False(new StrictEmailAttribute().IsValid($"{longLocalPart}@example.com"));
        Assert.False(new StrictEmailAttribute().IsValid($"user@{longLabel}.com"));
        Assert.True(new StrictEmailAttribute().IsValid($"{longLocalPart[1..]}@{longLabel[1..]}.com"));
    }

    [Fact]
    public void StrictEmailLeavesMissingValueToRequiredAttribute()
    {
        Assert.True(new StrictEmailAttribute().IsValid(null));
        Assert.True(new StrictEmailAttribute().IsValid(string.Empty));
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("password")]
    [InlineData("PASSWORD123")]
    [InlineData("Qwerty123")]
    [InlineData("matkhau123")]
    [InlineData("dprental123")]
    public void NotCommonPasswordRejectsCommonPasswordsIgnoringCase(string password) =>
        Assert.False(new NotCommonPasswordAttribute().IsValid(password));

    [Theory]
    [InlineData("Matkhau123!")]
    [InlineData("correct horse battery staple")]
    [InlineData("mật khẩu có dấu 2026")]
    public void NotCommonPasswordAcceptsUncommonPasswordsWithoutCompositionRules(string password) =>
        Assert.True(new NotCommonPasswordAttribute().IsValid(password));

    [Theory]
    [InlineData("minhtran-2026", "minhtran@example.com", "Trần Minh")]
    [InlineData("xx-MINHTRAN-xx", "minhtran@example.com", null)]
    [InlineData("TranVanMinh#99", "m@example.com", "Tran Van Minh")]
    public void CheckPersonalInfoRejectsPasswordContainingEmailOrFullName(string password, string email, string? fullName) =>
        Assert.Equal(AuthErrors.Auth.PasswordContainsPersonalInfo, PasswordPolicy.CheckPersonalInfo(password, email, fullName));

    [Theory]
    [InlineData("Matkhau123!", "minhtran@example.com", "Trần Minh")]
    // Phần định danh quá ngắn (dưới 4 ký tự) dễ trùng ngẫu nhiên nên không tính.
    [InlineData("abc-Matkhau123!", "abc@example.com", "An")]
    public void CheckPersonalInfoAllowsUnrelatedPassword(string password, string email, string? fullName) =>
        Assert.Null(PasswordPolicy.CheckPersonalInfo(password, email, fullName));

    [Fact]
    public void WriteRequestsUsePolicyWhileLoginStaysLenient()
    {
        // Ghi dữ liệu: email chặt + mật khẩu theo chính sách.
        Assert.False(DtoValidator.TryValidate(new RegisterRequest("a@b", "Matkhau123!", "Customer"), out _));
        Assert.False(DtoValidator.TryValidate(new RegisterRequest("user@example.com", "12345678", "Customer"), out _));
        Assert.False(DtoValidator.TryValidate(new RegisterRequest("user@example.com", "short1!", "Customer"), out _));
        Assert.False(DtoValidator.TryValidate(new ChangePasswordRequest("old", "password123"), out _));
        Assert.False(DtoValidator.TryValidate(new ResetPasswordRequest("qwerty123", "qwerty123"), out _));
        Assert.True(DtoValidator.TryValidate(new RegisterRequest("user@example.com", "Matkhau123!", "Customer"), out _));

        // Đăng nhập: tài khoản tạo trước khi siết quy tắc vẫn phải vào được.
        Assert.True(DtoValidator.TryValidate(new LoginRequest("a@b", "12345678"), out _));
    }
}
