using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Dprental.CatalogService.Api.Routing;

/// <summary>Tiền tố route chung của mọi endpoint CatalogService (Gateway route theo tiền tố này).</summary>
internal static class ApiRoutes
{
    internal const string Prefix = "api/v1/catalog";
}

/// <summary>
/// Ghép <see cref="ApiRoutes.Prefix"/> vào trước route của mọi controller, nên <c>[Route]</c> trên
/// controller chỉ ghi phần con (vd <c>[Route("equipment-types")]</c>). Route bắt đầu bằng <c>/</c> hoặc <c>~/</c>
/// được giữ nguyên, dùng khi một controller cần thoát khỏi tiền tố.
/// </summary>
internal sealed class ApiRoutePrefixConvention(string prefix) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (ControllerModel controller in application.Controllers)
        {
            foreach (SelectorModel selector in controller.Selectors)
            {
                AttributeRouteModel prefixRoute = new(new RouteAttribute(prefix));
                selector.AttributeRouteModel = selector.AttributeRouteModel is null
                    ? prefixRoute
                    : AttributeRouteModel.CombineAttributeRouteModel(prefixRoute, selector.AttributeRouteModel);
            }
        }
    }
}
