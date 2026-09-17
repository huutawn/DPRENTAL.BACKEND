using AuthService.Application.Interfaces;
using AuthService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using AuthService.Application.Services;
using Dprental.AuthService.Api.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Đăng ký Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Đăng ký DI
builder.Services.AddScoped<IApplicationDbContext>(provider =>
    provider.GetRequiredService<ApplicationDbContext>());
builder.Services.AddScoped<IAuthService, AuthService.Application.Services.AuthService>();

// Đăng ký JWT
builder.Services.AddJwtAuthentication(builder.Configuration);

// Đăng ký Swagger & Controller
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.MapControllers();

app.Run();

public partial class Program { }