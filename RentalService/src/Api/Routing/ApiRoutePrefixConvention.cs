using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Dprental.RentalService.Api.Routing;

public sealed class ApiRoutePrefixConvention : IApplicationModelConvention
{
    private readonly AttributeRouteModel _centralPrefix;

    public ApiRoutePrefixConvention(string prefix)
    {
        _centralPrefix = new AttributeRouteModel(new Microsoft.AspNetCore.Mvc.RouteAttribute(prefix));
    }

    public void Apply(ApplicationModel application)
    {
        var apiAssembly = typeof(ApiRoutePrefixConvention).Assembly;

        foreach (var controller in application.Controllers)
        {
            if (controller.ControllerType.Assembly != apiAssembly)
            {
                continue;
            }

            var matchedSelectors = controller.Selectors.Where(x => x.AttributeRouteModel != null).ToList();
            if (matchedSelectors.Count > 0)
            {
                foreach (var selectorModel in matchedSelectors)
                {
                    selectorModel.AttributeRouteModel = AttributeRouteModel.CombineAttributeRouteModel(
                        _centralPrefix,
                        selectorModel.AttributeRouteModel);
                }
            }
            else
            {
                controller.Selectors.Add(new SelectorModel
                {
                    AttributeRouteModel = _centralPrefix
                });
            }
        }
    }
}
