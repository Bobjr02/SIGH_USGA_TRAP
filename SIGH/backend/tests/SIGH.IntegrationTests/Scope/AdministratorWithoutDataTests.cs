using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Application.Common.Authorization;
using SIGH.Infrastructure.Authorization;
using SIGH.IntegrationTests.Infrastructure;
using Xunit;

namespace SIGH.IntegrationTests.Scope;

/// <summary>
/// O Administrador só gerencia ACESSOS. Toda rota que exige uma permissão fora do perfil Administrador (funcionários,
/// medidas, anexos, relatórios, painel, exportação, avisos, auditoria de medidas, processo em etapas) responde 403 a
/// um Administrador — uma linha por rota, lida dos controllers. As rotas que ele usa (usuários, perfis, gerências,
/// motivos, auditoria de acesso e importações) continuam respondendo.
/// </summary>
public class AdministratorWithoutDataTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string AdminEmail = "adm.sem-dados@sigh.test";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<CustomWebApplicationFactory, HttpClient> Clients = new();

    private readonly CustomWebApplicationFactory _factory;

    public AdministratorWithoutDataTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public sealed record ApiRoute(string Method, string Template, IReadOnlyList<string> Permissions)
    {
        public override string ToString() => $"{Method} {Template}";
    }

    private static readonly IReadOnlySet<string> AdministratorPermissions =
        SystemRoles.Find(SystemRoles.Administrator)!.Permissions.ToHashSet(StringComparer.Ordinal);

    /// <summary>Todas as rotas dos controllers (método, modelo e permissões exigidas), por reflexão.</summary>
    public static IReadOnlyList<ApiRoute> AllRoutes()
    {
        var controllers = typeof(SIGH.Api.Controllers.BaseController).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(ControllerBase).IsAssignableFrom(t));
        var routes = new List<ApiRoute>();
        foreach (var controller in controllers)
        {
            var prefix = controller.GetCustomAttributes<RouteAttribute>(true).First().Template
                .Replace("[controller]", controller.Name[..^"Controller".Length].ToLowerInvariant());
            var classPermissions = controller.GetCustomAttributes<PermissionAttribute>(true).Select(p => p.Permission);
            foreach (var action in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                var permissions = classPermissions.Concat(action.GetCustomAttributes<PermissionAttribute>(true).Select(p => p.Permission)).ToList();
                foreach (var http in action.GetCustomAttributes<HttpMethodAttribute>(true))
                {
                    var template = string.IsNullOrEmpty(http.Template) ? prefix : $"{prefix}/{http.Template}";
                    foreach (var method in http.HttpMethods)
                        routes.Add(new ApiRoute(method, "/" + template.TrimStart('/'), permissions));
                }
            }
        }
        return routes.OrderBy(r => r.Template, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal).ToList();
    }

    /// <summary>Rotas de DADOS: exigem alguma permissão que o perfil Administrador não tem.</summary>
    public static IReadOnlyList<ApiRoute> DataRoutes() =>
        AllRoutes().Where(r => r.Permissions.Any(p => !AdministratorPermissions.Contains(p))).ToList();

    public static IEnumerable<object[]> DataRouteRows() => DataRoutes().Select(r => new object[] { r.Method, r.Template });

    private static readonly Regex Parameter = new(@"\{[^}]+\}", RegexOptions.Compiled);

    private async Task<HttpClient> AdminAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (Clients.TryGetValue(_factory, out var cached))
                return cached;
            await _factory.ResetDatabaseAsync();
            await AccessTestHelpers.CreateUserAsync(_factory, AdminEmail, SystemRoles.Administrator);
            var client = await AccessTestHelpers.LoginAsync(_factory, AdminEmail, CompanyDefaults.UsgaCode);
            Clients[_factory] = client;
            return client;
        }
        finally
        {
            Gate.Release();
        }
    }

    [Theory]
    [MemberData(nameof(DataRouteRows))]
    public async Task Administrator_GetsForbidden_OnEveryDataRoute(string method, string template)
    {
        var client = await AdminAsync();
        var url = Parameter.Replace(template, _ => Guid.NewGuid().ToString());

        async Task<HttpResponseMessage> SendAsync(bool multipart)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method != "GET" && method != "DELETE")
                request.Content = multipart ? new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "x.pdf" } } : JsonContent.Create(new { });
            return await client.SendAsync(request);
        }

        // Rotas de upload só casam com multipart (senão o roteamento responde 415 antes da autorização).
        var response = await SendAsync(multipart: false);
        if (response.StatusCode == HttpStatusCode.UnsupportedMediaType)
            response = await SendAsync(multipart: true);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {template} é rota de dados: o Administrador não pode usá-la ({await response.Content.ReadAsStringAsync()})");
    }

    /// <summary>A lista por reflexão é a mesma do roteamento real (nenhuma rota fica de fora do teste acima).</summary>
    [Fact]
    public void ReflectedRoutes_MatchTheRealEndpoints()
    {
        _ = _factory.CreateClient();
        var real = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>() is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => $"{m} /{e.RoutePattern.RawText!.TrimStart('/')}"))
            .ToHashSet(StringComparer.Ordinal);

        AllRoutes().Select(r => r.ToString()).Should().BeEquivalentTo(real);
    }

    /// <summary>As rotas de dados centrais estão mesmo na lista (o teste por rota não fica vazio por engano).</summary>
    [Fact]
    public void DataRoutes_IncludeEmployeesMeasuresAttachmentsReportsExportsNotificationsAndMeasureAudit()
    {
        DataRoutes().Select(r => r.ToString()).Should().Contain(
        [
            "GET /api/v1/employees",
            "GET /api/v1/employees/{id:guid}",
            "GET /api/v1/employees/{id:guid}/decision-support",
            "GET /api/v1/employees/filter-options",
            "GET /api/v1/disciplinary-records",
            "GET /api/v1/disciplinary-records/{id:guid}",
            "GET /api/v1/disciplinary-records/options",
            "GET /api/v1/disciplinary-records/stats",
            "GET /api/v1/disciplinary-records/overview",
            "GET /api/v1/disciplinary-records/export",
            "GET /api/v1/disciplinary-records/pending-count",
            "GET /api/v1/disciplinary-cases/{caseId:guid}/evidences/{evidenceId:guid}/file",
            "GET /api/v1/disciplinary-cases/{id:guid}/audit-trail",
            "GET /api/v1/audit-logs",
            "GET /api/v1/audit-logs/users",
            "GET /api/v1/notifications",
            "GET /api/v1/notifications/unread/count",
        ]);
    }

    /// <summary>Controle positivo: o que o Administrador usa continua respondendo 200.</summary>
    [Theory]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/users/admin-status")]
    [InlineData("/api/v1/roles")]
    [InlineData("/api/v1/management-units")]
    [InlineData("/api/v1/infraction-types")]
    [InlineData("/api/v1/audit-logs/access")]
    [InlineData("/api/v1/audit-logs/access/users")]
    [InlineData("/api/v1/employees/import/batches")]
    [InlineData("/api/v1/disciplinary-history/import/batches")]
    [InlineData("/api/v1/disciplinary-history/import/status")]
    public async Task Administrator_StillManagesAccess_AndImports(string url)
    {
        var client = await AdminAsync();
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{url}: {await response.Content.ReadAsStringAsync()}");
    }
}
