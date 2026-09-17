using Dprental.AuthService.Api.Authentication;
using Dprental.AuthService.Api.Authentication.Transport;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddJwtAuthentication(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapJwksEndpoint();

app.Run();

public partial class Program;
