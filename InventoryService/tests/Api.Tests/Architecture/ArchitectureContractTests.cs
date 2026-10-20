using Dprental.InventoryService.Application;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using System.Reflection;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Architecture;

public sealed class ArchitectureContractTests
{
    [Fact]
    public void Domain_DoesNotReference_ApplicationOrApi()
    {
        var domainAssembly = typeof(OutboxMessage).Assembly;
        var referencedAssemblies = domainAssembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Application", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Api", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void DomainAndCommon_DoNotReference_ConfluentKafka()
    {
        var domainAssembly = typeof(OutboxMessage).Assembly;
        var commonAssembly = typeof(Result).Assembly;

        var domainReferencedAssemblies = domainAssembly.GetReferencedAssemblies();
        var commonReferencedAssemblies = commonAssembly.GetReferencedAssemblies();

        Assert.DoesNotContain(domainReferencedAssemblies, a => a.Name?.Contains("Confluent.Kafka", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(commonReferencedAssemblies, a => a.Name?.Contains("Confluent.Kafka", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Application_DoesNotReference_DatabasePersistenceSdks()
    {
        var applicationAssembly = typeof(ApplicationAssemblyMarker).Assembly;
        var referencedAssemblies = applicationAssembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Common_DoesNotReference_TechnicalVendorSdks()
    {
        var commonAssembly = typeof(Result).Assembly;
        var referencedAssemblies = commonAssembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Confluent.Kafka", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("StackExchange.Redis", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void PublicContracts_DoNotExpose_ConfluentKafkaTypes()
    {
        var contractTypes = new[]
        {
            typeof(MessageEnvelope),
            typeof(MessageEnvelope<>),
            typeof(IKafkaProducer),
            typeof(IMessageHandler<>),
            typeof(KafkaTopics),
            typeof(DlqRecord),
            typeof(HandlerRegistration)
        };

        foreach (var type in contractTypes)
        {
            // Kiểm tra kiểu trả về và tham số của mọi public method
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                AssertTypeDoesNotContainConfluent(method.ReturnType, type, method.Name);

                foreach (var param in method.GetParameters())
                {
                    AssertTypeDoesNotContainConfluent(param.ParameterType, type, $"{method.Name}({param.Name})");
                }
            }

            // Kiểm tra mọi public properties
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                AssertTypeDoesNotContainConfluent(prop.PropertyType, type, prop.Name);
            }
        }
    }

    private static void AssertTypeDoesNotContainConfluent(Type t, Type declaringType, string memberName)
    {
        var typeToCheck = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
        var ns = typeToCheck.Namespace ?? string.Empty;

        Assert.False(
            ns.StartsWith("Confluent.Kafka", StringComparison.OrdinalIgnoreCase),
            $"Public member '{memberName}' on '{declaringType.Name}' exposes Confluent.Kafka type '{t.FullName}'.");
    }

    [Fact]
    public void PublicAssetContracts_DoNotExpose_InternalOrOwnerFields()
    {
        var publicProps = typeof(PublicEquipmentListingResponse).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("OwnerUserId", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("SerialNumber", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("SuspensionReason", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Version", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Xmin", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DisplayName", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ThumbnailMediaId", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("AssetCode", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("AssetId", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Assets", publicProps, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("GroupReference", publicProps, StringComparer.OrdinalIgnoreCase);
    }
}
