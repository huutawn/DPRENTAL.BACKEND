using System.Reflection;
using System.Xml.Linq;
using Dprental.RecommendationService.Application;
using Dprental.RecommendationService.Common;
using Dprental.RecommendationService.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests;

public sealed class AssemblyDependencyTests
{
    private readonly Assembly _apiAssembly = typeof(Program).Assembly;
    private readonly Assembly _applicationAssembly = typeof(ApplicationAssemblyMarker).Assembly;
    private readonly Assembly _domainAssembly = typeof(DomainAssemblyMarker).Assembly;
    private readonly Assembly _commonAssembly = typeof(CommonAssemblyMarker).Assembly;

    [Fact]
    public void Assemblies_FollowStrictLayeringDependencies()
    {
        string commonName = _commonAssembly.GetName().Name!;
        string domainName = _domainAssembly.GetName().Name!;
        string appName = _applicationAssembly.GetName().Name!;
        string apiName = _apiAssembly.GetName().Name!;

        HashSet<string> commonRefs = _commonAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToHashSet();
        HashSet<string> domainRefs = _domainAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToHashSet();
        HashSet<string> appRefs = _applicationAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToHashSet();
        HashSet<string> apiRefs = _apiAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToHashSet();

        // Common does not depend on higher layers
        Assert.DoesNotContain(domainName, commonRefs);
        Assert.DoesNotContain(appName, commonRefs);
        Assert.DoesNotContain(apiName, commonRefs);

        // Domain depends only on Common, not on Application or Api
        Assert.Contains(commonName, domainRefs);
        Assert.DoesNotContain(appName, domainRefs);
        Assert.DoesNotContain(apiName, domainRefs);

        // Application depends on Domain, not on Api
        Assert.Contains(domainName, appRefs);
        Assert.DoesNotContain(apiName, appRefs);

        // Api depends on Domain
        Assert.Contains(domainName, apiRefs);
    }

    [Fact]
    public void ProjectFiles_FollowStrictLayeringDependencies()
    {
        string rootDir = FindRepositoryRoot();
        string commonCsproj = Path.Combine(rootDir, "RecommendationService/src/Common/RecommendationService.Common.csproj");
        string domainCsproj = Path.Combine(rootDir, "RecommendationService/src/Domain/RecommendationService.Domain.csproj");
        string appCsproj = Path.Combine(rootDir, "RecommendationService/src/Application/RecommendationService.Application.csproj");
        string apiCsproj = Path.Combine(rootDir, "RecommendationService/src/Api/RecommendationService.Api.csproj");

        HashSet<string> commonProjectRefs = GetProjectReferences(commonCsproj);
        HashSet<string> domainProjectRefs = GetProjectReferences(domainCsproj);
        HashSet<string> appProjectRefs = GetProjectReferences(appCsproj);
        HashSet<string> apiProjectRefs = GetProjectReferences(apiCsproj);

        // Common has no project references
        Assert.Empty(commonProjectRefs);

        // Domain only references Common
        Assert.Single(domainProjectRefs);
        Assert.Contains("RecommendationService.Common.csproj", domainProjectRefs.First(), StringComparison.Ordinal);

        // Application only references Domain
        Assert.Single(appProjectRefs);
        Assert.Contains("RecommendationService.Domain.csproj", appProjectRefs.First(), StringComparison.Ordinal);

        // Api references Application and Domain
        Assert.Equal(2, apiProjectRefs.Count);
        Assert.Contains(apiProjectRefs, p => p.Contains("RecommendationService.Application.csproj", StringComparison.Ordinal));
        Assert.Contains(apiProjectRefs, p => p.Contains("RecommendationService.Domain.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void Persistence_IsContainedInDomain_AndNotDirectlyUsedByApplicationOrApiControllers()
    {
        HashSet<string> domainRefs = _domainAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToHashSet();
        HashSet<string> appRefs = _applicationAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToHashSet();

        // Architectural exception: Domain contains EF Core & PostgreSQL provider
        Assert.Contains("Microsoft.EntityFrameworkCore", domainRefs);
        Assert.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", domainRefs);

        // Application layer must not reference EF Core directly
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", appRefs);
        Assert.DoesNotContain("Npgsql", appRefs);
        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL", appRefs);

        // Application types must not expose or inject DbContext
        Type[] appTypes = _applicationAssembly.GetTypes();
        foreach (Type type in appTypes)
        {
            Assert.False(
                typeof(DbContext).IsAssignableFrom(type),
                $"Application type {type.FullName} must not inherit from DbContext.");

            ConstructorInfo[] constructors = type.GetConstructors();
            foreach (ConstructorInfo ctor in constructors)
            {
                foreach (ParameterInfo param in ctor.GetParameters())
                {
                    Assert.False(
                        typeof(DbContext).IsAssignableFrom(param.ParameterType),
                        $"Application constructor {type.FullName} must not accept DbContext.");
                }
            }
        }

        // Api controllers must not inject DbContext directly
        Type[] apiTypes = _apiAssembly.GetTypes();
        IEnumerable<Type> controllerTypes = apiTypes.Where(t => typeof(ControllerBase).IsAssignableFrom(t));
        foreach (Type controller in controllerTypes)
        {
            Assert.False(
                typeof(DbContext).IsAssignableFrom(controller),
                $"Controller {controller.FullName} must not inherit from DbContext.");

            ConstructorInfo[] constructors = controller.GetConstructors();
            foreach (ConstructorInfo ctor in constructors)
            {
                foreach (ParameterInfo param in ctor.GetParameters())
                {
                    Assert.False(
                        typeof(DbContext).IsAssignableFrom(param.ParameterType),
                        $"Controller {controller.FullName} must not inject DbContext directly.");
                }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "RecommendationService.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static HashSet<string> GetProjectReferences(string csprojPath)
    {
        XDocument doc = XDocument.Load(csprojPath);
        return doc.Descendants("ProjectReference")
            .Select(pr => (string?)pr.Attribute("Include") ?? string.Empty)
            .ToHashSet();
    }
}
