using System.Reflection;
using Dprental.ReviewService.Api.Controllers;
using Dprental.ReviewService.Application;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// Architecture tests xác thực ranh giới phân tầng:
/// 1. Application và Controllers tuyệt đối không inject DbContext trực tiếp.
/// 2. Mọi business service trong Application không được vượt quá 1.000 dòng theo quy chuẩn AGENTS.md.
/// </summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void Application_Layer_DoesNotDependOn_DbContext()
    {
        var applicationAssembly = typeof(ApplicationAssemblyMarker).Assembly;
        var types = applicationAssembly.GetTypes();

        foreach (var type in types)
        {
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var ctor in constructors)
            {
                foreach (var param in ctor.GetParameters())
                {
                    Assert.False(
                        typeof(DbContext).IsAssignableFrom(param.ParameterType),
                        $"Type '{type.FullName}' in Application layer has constructor parameter of type DbContext ('{param.ParameterType.Name}'). Direct DbContext dependency is strictly prohibited.");
                }
            }

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                Assert.False(
                    typeof(DbContext).IsAssignableFrom(field.FieldType),
                    $"Type '{type.FullName}' in Application layer has field of type DbContext ('{field.FieldType.Name}'). Direct DbContext dependency is strictly prohibited.");
            }
        }
    }

    [Fact]
    public void Controllers_DoNotDependOn_DbContext()
    {
        var apiAssembly = typeof(ReviewsController).Assembly;
        var controllerTypes = apiAssembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t));

        foreach (var type in controllerTypes)
        {
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var ctor in constructors)
            {
                foreach (var param in ctor.GetParameters())
                {
                    Assert.False(
                        typeof(DbContext).IsAssignableFrom(param.ParameterType),
                        $"Controller '{type.FullName}' has constructor parameter of type DbContext ('{param.ParameterType.Name}'). Controllers must not depend on DbContext.");
                }
            }
        }
    }

    [Fact]
    public void BusinessServices_DoNotExceed_1000Lines()
    {
        // Get directory of Application/Services
        var solutionDir = Directory.GetParent(AppContext.BaseDirectory);
        while (solutionDir != null && !File.Exists(Path.Combine(solutionDir.FullName, "ReviewService.sln")))
        {
            solutionDir = solutionDir.Parent;
        }

        Assert.NotNull(solutionDir);
        var servicesDir = Path.Combine(solutionDir.FullName, "ReviewService", "src", "Application", "Services");
        var serviceFiles = Directory.GetFiles(servicesDir, "*.cs");

        foreach (var file in serviceFiles)
        {
            var lineCount = File.ReadAllLines(file).Length;
            Assert.True(
                lineCount < 1000,
                $"Service file '{Path.GetFileName(file)}' has {lineCount} lines, exceeding the 1,000 line limit specified in AGENTS.md.");
        }
    }
}
