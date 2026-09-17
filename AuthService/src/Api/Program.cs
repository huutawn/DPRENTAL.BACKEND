using Dprental.AuthService.Api.Endpoints;
using Dprental.AuthService.Api.Observability;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.DependencyInjection;
using Dprental.AuthService.Domain.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthServiceDistributedTracing(builder.Configuration);
builder.Services.AddAuthApplication(builder.Configuration);
builder.Services.AddAuthDomainPersistence(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddJwtBearerSecurity();
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
