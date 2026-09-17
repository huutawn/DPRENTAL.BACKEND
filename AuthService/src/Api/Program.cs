using AuthService.Infrastructure;
using Dprental.AuthService.Api.Authentication;
using Dprental.AuthService.Api.Authentication.Transport;
using Dprental.AuthService.Api.Observability;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthServiceDistributedTracing(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.InferSecuritySchemes());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapJwksEndpoint();

app.Run();

public partial class Program;
