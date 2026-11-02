using Dprental.RentalService.Application.Policies;
using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.RentalService.Application.Options
{
    /// <remarks>
    /// Cung cấp giới hạn ngày thuê qua IRentalCartPolicy cho evaluator/use case của giỏ.
    /// DI hiện đăng ký singleton dùng giá trị mặc định của class; class này không tự bind appsettings.
    /// </remarks>
    public sealed class RentalCartOptions : IRentalCartPolicy
    {
        /// <summary>Giới hạn số ngày thuê của một dòng giỏ, được cung cấp cho evaluator qua IRentalCartPolicy.</summary>
        public int MaximumRentalDays { get; init; } = Dprental.RentalService.Domain.Entities.RentalCart.MaximumRentalDays;
    }
}
