using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Models;

namespace Dprental.AuthService.Application.IServices;

public interface IExternalLoginService
{
    /// <summary>
    /// Tìm, liên kết hoặc tạo user cho một danh tính đã được nhà cung cấp bên ngoài xác nhận,
    /// rồi trả về id của user để tạo phiên đăng nhập.
    /// </summary>
    Task<Result<Guid>> SignInAsync(ExternalIdentity identity, CancellationToken cancellationToken = default);
}
