using System.Reflection;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Persistence.Adapters;

namespace InviteMe.IntegrationTests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void DomainAndApplicationHaveNoPersistenceOrProviderDependency()
    {
        var domain = typeof(WeddingGuest).Assembly;
        Assert.All(domain.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name!.StartsWith("System", StringComparison.Ordinal) || reference.Name == "netstandard",
                $"Domain must remain framework-independent: {reference.Name}"));

        var application = typeof(IWeddingAccessReader).Assembly;
        Assert.DoesNotContain(application.GetReferencedAssemblies(), reference =>
            reference.Name is "InviteMe.Infrastructure" or "InviteMe.Api" || IsProviderDependency(reference.Name!));
    }

    [Fact]
    public void ApplicationPortsExposeOnlyApplicationDomainOrSystemTypes()
    {
        var application = typeof(IWeddingAccessReader).Assembly;
        var ports = application.GetExportedTypes().Where(t => t.IsInterface &&
            t.Namespace?.StartsWith("InviteMe.Application.Ports.", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(ports);
        foreach (var port in ports)
        foreach (var method in port.GetMethods())
        foreach (var type in Flatten(method.ReturnType).Concat(method.GetParameters().SelectMany(p => Flatten(p.ParameterType))))
        {
            Assert.NotEqual(typeof(IQueryable), type);
            Assert.False(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>));
            var owner = type.Assembly.GetName().Name!;
            Assert.True(owner.StartsWith("System", StringComparison.Ordinal) ||
                owner is "InviteMe.Application" or "InviteMe.Domain" or "netstandard", $"{port.Name}.{method.Name} exposes {type}");
        }
        Assert.True(typeof(IWeddingAccessReader).IsAssignableFrom(typeof(WeddingAccessReader)));
    }

    [Fact]
    public void EndpointTypesDoNotDependOnPersistenceOrIntegrationAdapters()
    {
        var endpoints = typeof(Program).Assembly.GetTypes().Where(t =>
            t.Namespace?.StartsWith("InviteMe.Api.Endpoints", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(endpoints);
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var endpoint in endpoints)
        {
            var types = endpoint.GetFields(members).Select(f => f.FieldType)
                .Concat(endpoint.GetProperties(members).Select(p => p.PropertyType))
                .Concat(endpoint.GetMethods(members).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
                .Concat(endpoint.GetConstructors(members).SelectMany(c => c.GetParameters().Select(p => p.ParameterType)))
                .SelectMany(Flatten);
            foreach (var type in types)
                Assert.False(type.Assembly.GetName().Name == "InviteMe.Infrastructure" || IsProviderDependency(type.Assembly.GetName().Name!),
                    $"Endpoint {endpoint.Name} directly depends on adapter/provider type {type}");
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.HasElementType)
            foreach (var element in Flatten(type.GetElementType()!)) yield return element;
        foreach (var argument in type.GetGenericArguments())
            foreach (var nested in Flatten(argument)) yield return nested;
    }

    private static bool IsProviderDependency(string name) =>
        name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
        name.StartsWith("Npgsql", StringComparison.Ordinal) ||
        name.StartsWith("AWSSDK", StringComparison.Ordinal) ||
        name.StartsWith("Azure.", StringComparison.Ordinal) ||
        name.StartsWith("Hangfire", StringComparison.Ordinal) ||
        name.StartsWith("Stripe", StringComparison.Ordinal) ||
        name.StartsWith("Twilio", StringComparison.Ordinal) ||
        name.StartsWith("OpenAI", StringComparison.Ordinal);
}
