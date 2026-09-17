using System.Threading.Tasks;
using AuthService.Application.DTOs.Auth;

namespace AuthService.Application.Interfaces;

public interface IAuthService
{
    Task<LoginResponseDto> LoginAsync(LoginRequestDto request);
}

