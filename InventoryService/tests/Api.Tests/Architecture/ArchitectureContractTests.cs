using Dprental.InventoryService.Application;
using System.Reflection;
using Dprental.InventoryService.Domain.Kafka.Contracts;
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
    public void Application_DoesNotReference_TechnicalVendorSdks()
    {
        var applicationAssembly = typeof(ApplicationAssemblyMarker).Assembly;
        var referencedAssemblies = applicationAssembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Confluent.Kafka", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("StackExchange.Redis", StringComparison.OrdinalIgnoreCase) == true);
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
}
