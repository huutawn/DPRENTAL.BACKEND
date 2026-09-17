using Dprental.AuthService.Api.Authentication;
using Dprental.AuthService.Api.Authentication.Transport;

var builder = WebApplication.CreateBuilder(args);
// Đăng ký và validate JWT trước khi API bắt đầu nhận request.
builder.Services.AddJwtAuthentication(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
// Công bố public key cho gateway và các service cần tự xác thực JWT.
app.MapJwksEndpoint();

app.Run();

public partial class Program;
