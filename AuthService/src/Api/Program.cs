using AuthService.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
using Dprental.AuthService.Api.Authentication;
using Dprental.AuthService.Api.Authentication.Transport;
using Dprental.AuthService.Api.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthServiceDistributedTracing(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapControllers();
// Đăng ký và validate JWT trước khi API bắt đầu nhận request.
builder.Services.AddJwtAuthentication(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
// Công bố public key cho gateway và các service cần tự xác thực JWT.
app.MapJwksEndpoint();

app.Run();

public partial class Program { }