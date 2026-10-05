using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Dprental.InventoryService.Api.Routing;

/// <summary>
/// Tiền tố route chung của mọi endpoint trong InventoryService.
/// </summary>
public static class ApiRoutes
{
    public const string Prefix = "api/v1/inventory";
}

/// <summary>
/// Ghép <see cref="ApiRoutes.Prefix"/> cứng vào trước route của mọi controller,
/// cho phép controller chỉ khai báo route con tương đối (như [Route("assets")]).
/// </summary>
public sealed class ApiRoutePrefixConvention : IApplicationModelConvention
{
    private readonly AttributeRouteModel _prefixRoute;

    public string Prefix { get; }

    public ApiRoutePrefixConvention(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        Prefix = prefix.Trim('/');
        _prefixRoute = new AttributeRouteModel(new RouteAttribute(Prefix));
    }
    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);

        var apiAssembly = typeof(ApiRoutePrefixConvention).Assembly;

        foreach (var controller in application.Controllers)
        {
            if (controller.ControllerType.Assembly != apiAssembly)
            {
                continue;
            }

            foreach (var selector in controller.Selectors)
            {
                selector.AttributeRouteModel = selector.AttributeRouteModel is null
                    ? _prefixRoute
                    : AttributeRouteModel.CombineAttributeRouteModel(_prefixRoute, selector.AttributeRouteModel);
            }
        }
    }
}
