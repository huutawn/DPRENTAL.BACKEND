using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Dprental.ReviewService.Api.Routing;

/// <summary>
/// Tiền tố route chuẩn cho mọi endpoint trong ReviewService.
/// </summary>
public static class ApiRoutes
{
    public const string Prefix = "api/v1";
}

/// <summary>
/// Ghép tiền tố <see cref="ApiRoutes.Prefix"/> vào trước route của mọi controller trong API assembly,
/// cho phép controller chỉ khai báo route tương đối (như [Route("reviews")], [Route("reviews/admin")]).
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
