global using Dprental.AuthService.Common.Enums;
global using Dprental.AuthService.Application.Options;
global using Dprental.AuthService.Application.Repositories;
global using Dprental.AuthService.Application.Exceptions;
global using Dprental.AuthService.Infrastructure.Data;
global using Dprental.AuthService.Infrastructure.Repositories;

namespace Dprental.AuthService.Api.Tests;

internal sealed class FixedCorrelationIdProvider : Dprental.AuthService.Application.IServices.ICorrelationIdProvider
{
    public static readonly FixedCorrelationIdProvider Instance = new();

    public string GetCorrelationId() => "test-correlation-id";
}
