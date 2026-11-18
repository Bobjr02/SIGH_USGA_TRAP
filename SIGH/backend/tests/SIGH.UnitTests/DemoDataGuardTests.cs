using FluentAssertions;
using SIGH.Demo;
using Xunit;

namespace SIGH.UnitTests;

/// <summary>Dados de demonstração só em Development ou Demo.</summary>
public class DemoDataGuardTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Demo", true)]
    [InlineData("demo", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Testing", false)]
    public void DemoData_IsOnlyAllowedInDevelopmentOrDemo(string environment, bool allowed)
    {
        DemoDataGuard.IsAllowed(new DemoDataOptions { DemoData = true }, environment).Should().Be(allowed);
        DemoDataGuard.IsDemoEnvironment(environment).Should().Be(allowed);
    }

    [Fact]
    public void WithoutDemoData_AnyEnvironmentIsFine() =>
        DemoDataGuard.IsAllowed(new DemoDataOptions { DemoData = false }, "Production").Should().BeTrue();

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void TheSeeder_IsOnlyRegisteredWithDemoData(bool demoData, int registrations)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDemoData(demoData);
        services.Count(s => s.ServiceType == typeof(DemoDataSeeder)).Should().Be(registrations);
    }

    [Fact]
    public void TheApi_DoesNotReferenceOpenXml_OnlyTheDemoProjectDoes()
    {
        typeof(SIGH.Api.Controllers.HealthController).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name).Should().NotContain("DocumentFormat.OpenXml");
        typeof(DemoDataSeeder).Assembly.GetName().Name.Should().Be("SIGH.Demo");
    }

    [Fact]
    public void TheFictitiousPdf_LooksLikeAPdf()
    {
        var pdf = System.Text.Encoding.ASCII.GetString(DemoDataSeeder.FictitiousPdf());
        pdf.Should().StartWith("%PDF-1.4").And.Contain("DOCUMENTO FICTICIO").And.EndWith("%%EOF\n");
    }
}
