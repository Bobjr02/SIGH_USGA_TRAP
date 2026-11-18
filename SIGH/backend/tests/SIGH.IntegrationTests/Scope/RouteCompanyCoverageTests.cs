using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Application.Common.Authorization;
using Xunit;

namespace SIGH.IntegrationTests.Scope;

/// <summary>
/// TODA rota da API (leitura e escrita) está classificada quanto à empresa ([CompanyRoute]) e cada rota com dado de
/// empresa tem um caso em <see cref="CompanyIsolationTests"/>. Uma rota nova sem classificação, ou sem caso de
/// isolamento, faz este teste falhar.
/// </summary>
public class RouteCompanyCoverageTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RouteCompanyCoverageTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Route(string Key, string Method, CompanyRouteAttribute? Company);

    private List<Route> AllRoutes()
    {
        _ = _factory.CreateClient(); // sobe o host
        return _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
                var pattern = "/" + e.RoutePattern.RawText!.TrimStart('/');
                return methods.Select(m => new Route($"{m} {pattern}", m, e.Metadata.GetMetadata<CompanyRouteAttribute>()));
            })
            .DistinctBy(r => r.Key)
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void EveryRoute_IsClassifiedByCompany()
    {
        var routes = AllRoutes();
        routes.Should().HaveCountGreaterThan(80);

        routes.Where(r => r.Company is null).Select(r => r.Key).Should()
            .BeEmpty("toda rota precisa de [CompanyRoute(...)]: Read, Write, Personal ou NotApplicable (com o motivo)");

        routes.Where(r => r.Company!.Kind is CompanyRouteKind.NotApplicable or CompanyRouteKind.Personal && string.IsNullOrWhiteSpace(r.Company.Reason))
            .Select(r => r.Key).Should().BeEmpty("NotApplicable e Personal precisam do motivo");

        // Leitura nunca escreve e escrita nunca é GET: o modo "Todas" (só leitura) depende disso.
        routes.Where(r => r.Company!.Kind == CompanyRouteKind.Write && r.Method == "GET").Select(r => r.Key).Should().BeEmpty();
        routes.Where(r => r.Company!.Kind == CompanyRouteKind.Read && r.Method != "GET").Select(r => r.Key).Should().BeEmpty();
    }

    [Fact]
    public void EveryCompanyReadRoute_HasAnIsolationTest()
    {
        var reads = AllRoutes().Where(r => r.Company?.Kind == CompanyRouteKind.Read).Select(r => r.Key).ToList();
        CompanyIsolationTests.ReadCases.Keys.Should().BeEquivalentTo(reads,
            "cada rota de leitura com dado de empresa precisa de um caso em CompanyIsolationTests.ReadCases (e casos de rotas que não existem mais saem)");
    }

    [Fact]
    public void EveryCompanyWriteRoute_HasAnIsolationTest()
    {
        var writes = AllRoutes().Where(r => r.Company?.Kind is CompanyRouteKind.Write or CompanyRouteKind.Personal).Select(r => r.Key).ToList();
        CompanyIsolationTests.WriteCases.Keys.Should().BeEquivalentTo(writes,
            "cada rota de escrita precisa de um caso em CompanyIsolationTests.WriteCases (e casos de rotas que não existem mais saem)");
    }

    [Fact]
    public void NotApplicableRoutes_AreOnlyTheOnesWithoutCompanyData()
    {
        AllRoutes().Where(r => r.Company?.Kind == CompanyRouteKind.NotApplicable).Select(r => r.Key).Should().BeEquivalentTo(
        [
            "GET /api/v1",
            "GET /api/v1/health",
            "GET /api/v1/roles",
            "GET /api/v1/auth/me",
            "POST /api/v1/auth/login",
            "POST /api/v1/auth/logout",
            "POST /api/v1/auth/refresh",
            "POST /api/v1/auth/forgot-password",
            "POST /api/v1/auth/reset-password",
            "POST /api/v1/auth/change-password",
        ], "rota nova sem dado de empresa precisa ser revista aqui");
    }
}
