global using Dprental.AuthService.Common.Enums;
global using Dprental.AuthService.Application.Options;
global using Dprental.AuthService.Domain.IRepositories;
global using Dprental.AuthService.Domain.Repositories;

namespace Dprental.AuthService.Api.Tests;

internal sealed class FixedCorrelationIdProvider : Dprental.AuthService.Application.IServices.ICorrelationIdProvider
{
    public static readonly FixedCorrelationIdProvider Instance = new();

    public string GetCorrelationId() => "test-correlation-id";
}
