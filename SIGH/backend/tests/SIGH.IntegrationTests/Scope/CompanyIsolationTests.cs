using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Application.Common.Authorization;
using SIGH.Domain.Disciplinary.Enums;
using SIGH.Domain.Employees.Enums;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Context;
using SIGH.Persistence.Seeds;
using Xunit;

namespace SIGH.IntegrationTests.Scope;

/// <summary>
/// Isolamento entre empresas, rota a rota (o teste mais importante da multiempresa).
/// - LEITURA: um usuário só da USGA (com TODAS as permissões nela) NÃO vê dado da Trapiche (lista sem o registro ou
///   404, nunca 403); o controle positivo, um usuário da Trapiche, vê.
/// - ESCRITA: o mesmo usuário da USGA tenta agir sobre um recurso da Trapiche e é recusado; antes, confere que o
///   recurso existe e é visível ao usuário da Trapiche (a sonda acerta um alvo real).
/// <see cref="RouteCompanyCoverageTests"/> falha se uma rota de empresa não tiver caso aqui.
/// </summary>
public class CompanyIsolationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CompanyIsolationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private delegate Task<(HttpStatusCode Status, bool Sees)> Probe(HttpClient client, CompanyScenario s);

    private static async Task<(HttpStatusCode, string)> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static Probe Contains(Func<CompanyScenario, string> url, Func<CompanyScenario, string> marker) => async (client, s) =>
    {
        var (status, body) = await GetAsync(client, url(s));
        return (status, status == HttpStatusCode.OK && body.Contains(marker(s), StringComparison.OrdinalIgnoreCase));
    };

    private static Probe Found(Func<CompanyScenario, string> url) => async (client, s) =>
    {
        var (status, _) = await GetAsync(client, url(s));
        return (status, status == HttpStatusCode.OK);
    };

    private static Probe Number(Func<CompanyScenario, string> url, Func<JsonElement, int> read) => async (client, s) =>
    {
        var (status, body) = await GetAsync(client, url(s));
        return (status, status == HttpStatusCode.OK && read(JsonDocument.Parse(body).RootElement.GetProperty("data")) > 0);
    };

    /// <summary>Uma sonda por rota de LEITURA com dados de empresa ("GET rota"). A USGA não tem dado nenhum: qualquer coisa vista é da Trapiche.</summary>
    internal static readonly IReadOnlyDictionary<string, object> ReadCases = new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["GET /api/v1/employees"] = (Probe)Contains(_ => "/api/v1/employees?pageSize=100", s => s.TrapEmployeeNumber),
        ["GET /api/v1/employees/{id:guid}"] = (Probe)Found(s => $"/api/v1/employees/{s.TrapEmployee}"),
        ["GET /api/v1/employees/{id:guid}/decision-support"] = (Probe)Found(s => $"/api/v1/employees/{s.TrapEmployee}/decision-support"),
        ["GET /api/v1/employees/filter-options"] = (Probe)Contains(_ => "/api/v1/employees/filter-options", _ => "TRAP131"),

        ["GET /api/v1/disciplinary-records"] = (Probe)Contains(_ => "/api/v1/disciplinary-records?pageSize=100", s => s.TrapRecord.ToString()),
        ["GET /api/v1/disciplinary-records/{id:guid}"] = (Probe)Found(s => $"/api/v1/disciplinary-records/{s.TrapRecord}"),
        ["GET /api/v1/disciplinary-records/options"] = (Probe)Contains(_ => "/api/v1/disciplinary-records/options", _ => CompanyScenario.TrapInfractionCode),
        ["GET /api/v1/disciplinary-records/stats"] = (Probe)Number(_ => "/api/v1/disciplinary-records/stats", d => d.GetProperty("total").GetInt32()),
        ["GET /api/v1/disciplinary-records/export"] = (Probe)Contains(_ => "/api/v1/disciplinary-records/export", s => s.TrapRecordNumber),
        ["GET /api/v1/disciplinary-records/overview"] = (Probe)Contains(_ => "/api/v1/disciplinary-records/overview", s => s.TrapRecordNumber),
        ["GET /api/v1/disciplinary-records/pending-count"] = (Probe)Number(_ => "/api/v1/disciplinary-records/pending-count", d => d.GetInt32()),

        ["GET /api/v1/disciplinary-cases"] = (Probe)Contains(_ => "/api/v1/disciplinary-cases?pageSize=100", s => s.TrapRecord.ToString()),
        ["GET /api/v1/disciplinary-cases/{id:guid}"] = (Probe)Found(s => $"/api/v1/disciplinary-cases/{s.TrapRecord}"),
        ["GET /api/v1/disciplinary-cases/{id:guid}/audit-trail"] = (Probe)Found(s => $"/api/v1/disciplinary-cases/{s.TrapRecord}/audit-trail"),
        ["GET /api/v1/disciplinary-cases/{caseId:guid}/evidences/{evidenceId:guid}/file"] =
            (Probe)Found(s => $"/api/v1/disciplinary-cases/{s.TrapRecord}/evidences/{s.TrapEvidence}/file"),


        ["GET /api/v1/notifications"] = (Probe)Contains(_ => "/api/v1/notifications?pageSize=100", _ => CompanyScenario.TrapNotificationTitle),
        ["GET /api/v1/notifications/unread"] = (Probe)Contains(_ => "/api/v1/notifications/unread", _ => CompanyScenario.TrapNotificationTitle),
        ["GET /api/v1/notifications/unread/count"] = (Probe)Number(_ => "/api/v1/notifications/unread/count", d => d.GetInt32()),

        ["GET /api/v1/audit-logs"] = (Probe)Contains(s => $"/api/v1/audit-logs?pageSize=200&reference={s.TrapRecordNumber}", s => s.TrapRecord.ToString()),
        ["GET /api/v1/audit-logs/users"] = (Probe)Contains(_ => "/api/v1/audit-logs/users?search=trap.registrar", _ => CompanyScenario.TrapRegistrar),

        ["GET /api/v1/users"] = (Probe)Contains(_ => "/api/v1/users?pageSize=100", _ => CompanyScenario.TrapManager),
        ["GET /api/v1/users/{id:guid}"] = (Probe)Found(s => $"/api/v1/users/{s.TrapManagerUserId}"),
        ["GET /api/v1/users/{id:guid}/access"] = (Probe)Found(s => $"/api/v1/users/{s.TrapManagerUserId}/access"),
        ["GET /api/v1/management-units"] = (Probe)Contains(_ => "/api/v1/management-units", _ => "TRAP131"),

        ["GET /api/v1/infraction-types"] = (Probe)Contains(_ => "/api/v1/infraction-types?pageSize=100", _ => CompanyScenario.TrapInfractionCode),
        ["GET /api/v1/infraction-types/{id:guid}"] = (Probe)Found(s => $"/api/v1/infraction-types/{s.TrapInfraction}"),

        ["GET /api/v1/employees/import/batches"] = (Probe)Contains(_ => "/api/v1/employees/import/batches?pageSize=100", s => s.TrapEmployeeBatch.ToString()),
        ["GET /api/v1/disciplinary-history/import/batches"] = (Probe)Contains(_ => "/api/v1/disciplinary-history/import/batches?pageSize=100", s => s.TrapHistoryBatch.ToString()),
        // Encerrada só na Trapiche: para a USGA a importação continua aberta.
        ["GET /api/v1/disciplinary-history/import/status"] = (Probe)Contains(_ => "/api/v1/disciplinary-history/import/status", _ => "\"isClosed\":true"),
    };

    /// <summary>Escrita: a requisição, a empresa ativa enviada (USGA ou, para criação, TRAP) e a leitura que prova que o alvo existe na Trapiche.</summary>
    /// <param name="PersonalNoEffect">Rota pessoal que responde 200 para o próprio usuário: tem de não tocar em nada da Trapiche.</param>
    internal sealed record WriteCase(Func<CompanyScenario, HttpRequestMessage> Request, Func<CompanyScenario, string>? Target,
        string Header = CompanyDefaults.UsgaCode, bool PersonalNoEffect = false);

    private static HttpRequestMessage Json(HttpMethod method, string url, object? body = null) =>
        new(method, url) { Content = body is null ? null : JsonContent.Create(body) };

    private static HttpRequestMessage File(string url, string field = "file")
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(CompanyScenario.PdfBytes), field, "arquivo.pdf" } };
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
    }

    private static HttpRequestMessage Xlsx(string url, byte[] file)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(file), "file", "planilha.xlsx" }, { new StringContent("false"), "confirmMassTermination" } };
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
    }

    private static string Rec(CompanyScenario s) => $"/api/v1/disciplinary-records/{s.TrapRecord}";
    private static string Case(CompanyScenario s) => $"/api/v1/disciplinary-cases/{s.TrapRecord}";
    private static string Emp(CompanyScenario s) => $"/api/v1/employees/{s.TrapEmployee}";
    private static string TrapUserUrl(CompanyScenario s) => $"/api/v1/users/{s.TrapManagerUserId}";
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));

    private static object RecordBody(CompanyScenario s) => new
    {
        employeeId = s.TrapEmployee, infractionTypeId = s.TrapInfraction, measureType = (int)DisciplinaryMeasureType.WrittenWarning,
        occurrenceDates = new[] { Today.AddDays(-1) }, measureDate = Today, description = "Sonda de isolamento.", confirmPossibleDuplicate = true
    };

    /// <summary>Uma sonda por rota de ESCRITA ("MÉTODO rota").</summary>
    internal static readonly IReadOnlyDictionary<string, WriteCase> WriteCases = new Dictionary<string, WriteCase>(StringComparer.Ordinal)
    {
        // Medidas: registrar com funcionário e motivo da Trapiche, e agir sobre uma medida da Trapiche.
        ["POST /api/v1/disciplinary-records"] = new(s => Json(HttpMethod.Post, "/api/v1/disciplinary-records", RecordBody(s)), Emp),
        ["PUT /api/v1/disciplinary-records/{id:guid}"] = new(s => Json(HttpMethod.Put, Rec(s), RecordBody(s)), Rec),
        ["POST /api/v1/disciplinary-records/{id:guid}/approve"] = new(s => Json(HttpMethod.Post, Rec(s) + "/approve"), Rec),
        ["POST /api/v1/disciplinary-records/{id:guid}/reject"] = new(s => Json(HttpMethod.Post, Rec(s) + "/reject", new { reason = "Sonda." }), Rec),
        ["POST /api/v1/disciplinary-records/{id:guid}/cancel"] = new(s => Json(HttpMethod.Post, Rec(s) + "/cancel", new { reason = "Sonda." }), Rec),

        // Anexos.
        ["POST /api/v1/disciplinary-cases/{caseId:guid}/evidences/{evidenceId:guid}/file"] = new(s => File($"{Case(s)}/evidences/{s.TrapEvidence}/file"), Rec),
        ["DELETE /api/v1/disciplinary-cases/{caseId:guid}/evidences/{evidenceId:guid}"] = new(s => Json(HttpMethod.Delete, $"{Case(s)}/evidences/{s.TrapEvidence}", new { reason = "Sonda." }), Rec),

        // Processo em etapas (legado, fora do menu).
        ["POST /api/v1/disciplinary-cases"] = new(_ => Json(HttpMethod.Post, "/api/v1/disciplinary-cases",
            new { caseNumber = "PROC-SONDA", companyId = DatabaseSeeder.TrapicheCompanyId, title = "Sonda", description = "Sonda de isolamento." }), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/open"] = new(s => Json(HttpMethod.Post, Case(s) + "/open"), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/start-investigation"] = new(s => Json(HttpMethod.Post, Case(s) + "/start-investigation"), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/submit-for-decision"] = new(s => Json(HttpMethod.Post, Case(s) + "/submit-for-decision"), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/approve-decision"] = new(s => Json(HttpMethod.Post, Case(s) + "/approve-decision"), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/reject-decision"] = new(s => Json(HttpMethod.Post, Case(s) + "/reject-decision", new { reason = "Sonda." }), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/occurrences"] = new(s => Json(HttpMethod.Post, Case(s) + "/occurrences",
            new { occurrenceDate = DateTimeOffset.UtcNow.AddDays(-1), description = "Sonda de isolamento.", infractionTypeId = s.TrapInfraction }), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/employees"] = new(s => Json(HttpMethod.Post, Case(s) + "/employees",
            new { disciplinaryCaseId = s.TrapRecord, employeeId = s.TrapEmployee, role = (int)CaseEmployeeRole.Witness }), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/evidences"] = new(s => Json(HttpMethod.Post, Case(s) + "/evidences",
            new { evidenceType = (int)EvidenceType.Document, title = "Sonda", collectedAt = DateTimeOffset.UtcNow }), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/decisions"] = new(s => Json(HttpMethod.Post, Case(s) + "/decisions",
            new { decisionType = (int)DecisionType.FormalWarning, summary = "Sonda", reasoning = "Sonda de isolamento." }), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/measures"] = new(s => Json(HttpMethod.Post, Case(s) + "/measures",
            new { disciplinaryDecisionId = Guid.NewGuid(), employeeId = s.TrapEmployee, measureType = (int)DisciplinaryMeasureType.WrittenWarning, reason = "Sonda", effectiveFrom = DateTimeOffset.UtcNow }), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/cancel"] = new(s => Json(HttpMethod.Post, Case(s) + "/cancel",
            new { disciplinaryCaseId = s.TrapRecord, cancellationReason = "Sonda de isolamento." }), Rec),
        ["POST /api/v1/disciplinary-cases/{id:guid}/conclude"] = new(s => Json(HttpMethod.Post, Case(s) + "/conclude",
            new { disciplinaryCaseId = s.TrapRecord, conclusionSummary = "Sonda de isolamento." }), Rec),

        // Funcionários (cadastro manual legado).
        ["POST /api/v1/employees"] = new(s => Json(HttpMethod.Post, "/api/v1/employees", new
        {
            companyId = DatabaseSeeder.TrapicheCompanyId, employeeNumber = "79999999", fullName = "SONDA TESTE", cpf = "52998224725",
            admissionDate = Today, jobTitleId = s.TrapJobTitle, managementUnitId = s.TrapUnit
        }), Emp),
        ["PUT /api/v1/employees/{id:guid}"] = new(s => Json(HttpMethod.Put, Emp(s), new
        {
            employeeId = s.TrapEmployee, employeeNumber = s.TrapEmployeeNumber, fullName = "SONDA TESTE", cpf = "52998224725",
            jobTitleId = s.TrapJobTitle, managementUnitId = s.TrapUnit
        }), Emp),
        ["PATCH /api/v1/employees/{id:guid}/organizational-assignment"] = new(s => Json(HttpMethod.Patch, Emp(s) + "/organizational-assignment",
            new { employeeId = s.TrapEmployee, jobTitleId = s.TrapJobTitle, managementUnitId = s.TrapUnit }), Emp),
        ["PATCH /api/v1/employees/{id:guid}/supervisor"] = new(s => Json(HttpMethod.Patch, Emp(s) + "/supervisor", new { employeeId = s.TrapEmployee }), Emp),
        ["PATCH /api/v1/employees/{id:guid}/status"] = new(s => Json(HttpMethod.Patch, Emp(s) + "/status",
            new { employeeId = s.TrapEmployee, targetStatus = (int)EmployeeStatus.OnLeave }), Emp),
        ["PATCH /api/v1/employees/{id:guid}/terminate"] = new(s => Json(HttpMethod.Patch, Emp(s) + "/terminate",
            new { employeeId = s.TrapEmployee, terminationDate = Today, terminationReason = "Sonda de isolamento." }), Emp),
        ["PATCH /api/v1/employees/{id:guid}/reactivate"] = new(s => Json(HttpMethod.Patch, Emp(s) + "/reactivate",
            new { employeeId = s.TrapEmployee, newAdmissionDate = Today }), Emp),
        ["PATCH /api/v1/employees/{id:guid}/link-user"] = new(s => Json(HttpMethod.Patch, Emp(s) + "/link-user",
            new { employeeId = s.TrapEmployee, userId = s.UsgaUserId }), Emp),
        ["PATCH /api/v1/employees/{id:guid}/unlink-user"] = new(s => Json(HttpMethod.Patch, Emp(s) + "/unlink-user", new { employeeId = s.TrapEmployee }), Emp),

        // Importações: a confirmação de um lote da Trapiche; a pré-visualização e o encerramento PARA a Trapiche
        // (empresa ativa pedida pelo cliente sem perfil nela: 404).
        ["POST /api/v1/employees/import/preview"] = new(_ => Xlsx("/api/v1/employees/import/preview", SIGH.Demo.TrapicheDemoSpreadsheets.EmployeesXlsx()), null, CompanyDefaults.TrapicheCode),
        ["POST /api/v1/employees/import/{batchId:guid}/confirm"] = new(s => Xlsx($"/api/v1/employees/import/{s.TrapEmployeeBatch}/confirm", SIGH.Demo.TrapicheDemoSpreadsheets.EmployeesXlsx()),
            _ => "/api/v1/employees/import/batches"),
        ["POST /api/v1/disciplinary-history/import/preview"] = new(_ => Xlsx("/api/v1/disciplinary-history/import/preview", SIGH.Demo.TrapicheDemoSpreadsheets.HistoryXlsx()), null, CompanyDefaults.TrapicheCode),
        ["POST /api/v1/disciplinary-history/import/{batchId:guid}/confirm"] = new(s => Xlsx($"/api/v1/disciplinary-history/import/{s.TrapHistoryBatch}/confirm", SIGH.Demo.TrapicheDemoSpreadsheets.HistoryXlsx()),
            _ => "/api/v1/disciplinary-history/import/batches"),
        ["POST /api/v1/disciplinary-history/import/close"] = new(_ => Json(HttpMethod.Post, "/api/v1/disciplinary-history/import/close"), null, CompanyDefaults.TrapicheCode),

        // Motivos.
        ["POST /api/v1/infraction-types"] = new(_ => Json(HttpMethod.Post, "/api/v1/infraction-types",
            new { code = "SONDA", name = "Sonda", defaultSeverity = (int)InfractionSeverity.Low }), null, CompanyDefaults.TrapicheCode),
        ["PUT /api/v1/infraction-types/{id:guid}"] = new(s => Json(HttpMethod.Put, $"/api/v1/infraction-types/{s.TrapInfraction}",
            new { id = s.TrapInfraction, name = "Sonda", defaultSeverity = (int)InfractionSeverity.Low, requiresFormalInvestigation = false, allowsTerminationRecommendation = false }),
            s => $"/api/v1/infraction-types/{s.TrapInfraction}"),
        ["POST /api/v1/infraction-types/{id:guid}/activate"] = new(s => Json(HttpMethod.Post, $"/api/v1/infraction-types/{s.TrapInfraction}/activate"), s => $"/api/v1/infraction-types/{s.TrapInfraction}"),
        ["POST /api/v1/infraction-types/{id:guid}/deactivate"] = new(s => Json(HttpMethod.Post, $"/api/v1/infraction-types/{s.TrapInfraction}/deactivate"), s => $"/api/v1/infraction-types/{s.TrapInfraction}"),

        // Administração de usuários e gerências.
        ["POST /api/v1/users"] = new(_ => Json(HttpMethod.Post, "/api/v1/users", new
        {
            fullName = "SONDA TESTE", email = "sonda@sigh.test", cpf = "52998224725", initialPassword = "Sonda@123456", roleIds = new[] { Guid.NewGuid() }
        }), null, CompanyDefaults.TrapicheCode),
        ["POST /api/v1/users/link"] = new(_ => Json(HttpMethod.Post, "/api/v1/users/link",
            new { email = CompanyScenario.UsgaUser, roleIds = new[] { Guid.NewGuid() } }), null, CompanyDefaults.TrapicheCode),
        ["PATCH /api/v1/users/{id:guid}/status"] = new(s => Json(HttpMethod.Patch, TrapUserUrl(s) + "/status",
            new { userId = s.TrapManagerUserId, newStatus = (int)SIGH.Domain.Enums.UserStatus.Inactive, reason = "Sonda de isolamento." }), TrapUserUrl),
        ["POST /api/v1/users/{id:guid}/unlock"] = new(s => Json(HttpMethod.Post, TrapUserUrl(s) + "/unlock"), TrapUserUrl),
        ["POST /api/v1/users/{id:guid}/roles"] = new(s => Json(HttpMethod.Post, TrapUserUrl(s) + "/roles", new { roleId = Guid.NewGuid() }), TrapUserUrl),
        ["DELETE /api/v1/users/{id:guid}/roles/{userRoleId:guid}"] = new(s => Json(HttpMethod.Delete, $"{TrapUserUrl(s)}/roles/{s.TrapManagerRoleLink}"), TrapUserUrl),
        ["POST /api/v1/users/{id:guid}/management-units"] = new(s => Json(HttpMethod.Post, TrapUserUrl(s) + "/management-units",
            new { managementUnitId = s.TrapUnit }), TrapUserUrl),
        ["DELETE /api/v1/users/{id:guid}/management-units/{linkId:guid}"] = new(s => Json(HttpMethod.Delete, $"{TrapUserUrl(s)}/management-units/{s.TrapManagerUnitLink}"), TrapUserUrl),
        ["PUT /api/v1/management-units/{id:guid}"] = new(s => Json(HttpMethod.Put, $"/api/v1/management-units/{s.TrapUnit}", new { name = "Sonda" }), _ => "/api/v1/management-units"),

        // Avisos: marcar como lido um aviso da Trapiche (gravado para o próprio usuário da USGA) e o "marcar todos".
        ["PUT /api/v1/notifications/{id:guid}/read"] = new(s => Json(HttpMethod.Put, $"/api/v1/notifications/{s.TrapNotificationOfUsgaUser}/read"), null),
        ["PUT /api/v1/notifications/read-all"] = new(_ => Json(HttpMethod.Put, "/api/v1/notifications/read-all"), null, PersonalNoEffect: true),
        ["POST /api/v1/notifications/process-deadlines"] = new(_ => Json(HttpMethod.Post, "/api/v1/notifications/process-deadlines"), null, CompanyDefaults.TrapicheCode),
    };

    public static IEnumerable<object[]> ReadRoutes() => ReadCases.Keys.Order(StringComparer.Ordinal).Select(r => new object[] { r });

    public static IEnumerable<object[]> WriteRoutes() => WriteCases.Keys.Order(StringComparer.Ordinal).Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task UsgaOnlyUser_DoesNotSeeTrapicheData_AndTheTrapicheUserDoes(string route)
    {
        var scenario = await CompanyScenario.GetAsync(_factory);
        var probe = (Probe)ReadCases[route];

        using var trap = await AccessTestHelpers.LoginAsync(_factory, CompanyScenario.TrapUser, CompanyDefaults.TrapicheCode);
        var (positiveStatus, positiveSees) = await probe(trap, scenario);
        positiveStatus.Should().Be(HttpStatusCode.OK, $"controle positivo de {route}");
        positiveSees.Should().BeTrue($"o usuário da Trapiche deve ver o dado da Trapiche em {route}");

        // Com a empresa USGA no cabeçalho e sem cabeçalho (a empresa do primeiro perfil): nada da Trapiche.
        foreach (var header in new[] { CompanyDefaults.UsgaCode, null })
        {
            using var usga = await AccessTestHelpers.LoginAsync(_factory, CompanyScenario.UsgaUser, header);
            var (status, sees) = await probe(usga, scenario);
            sees.Should().BeFalse($"o usuário só da USGA não pode ver a Trapiche em {route} (cabeçalho {header ?? "ausente"})");
            status.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.NotFound], "fora do alcance: lista sem o registro ou 404, nunca 403");
        }

        // Pedir a Trapiche sem ter perfil nela: 404 (a empresa "não existe" para ele), nunca os dados.
        using var asksForTrap = await AccessTestHelpers.LoginAsync(_factory, CompanyScenario.UsgaUser, CompanyDefaults.TrapicheCode);
        var (trapStatus, trapSees) = await probe(asksForTrap, scenario);
        trapSees.Should().BeFalse();
        trapStatus.Should().Be(HttpStatusCode.NotFound, $"empresa sem perfil vigente no cabeçalho de {route}");
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task UsgaOnlyUser_CannotActOnTrapicheResources(string route)
    {
        var scenario = await CompanyScenario.GetAsync(_factory);
        var write = WriteCases[route];

        if (write.Target is { } target)
        {
            using var trap = await AccessTestHelpers.LoginAsync(_factory, CompanyScenario.TrapUser, CompanyDefaults.TrapicheCode);
            var exists = await trap.GetAsync(target(scenario));
            exists.StatusCode.Should().Be(HttpStatusCode.OK, $"o alvo da sonda de {route} tem de existir na Trapiche");
        }

        var before = await TrapicheFingerprintAsync(scenario);
        using var usga = await AccessTestHelpers.LoginAsync(_factory, CompanyScenario.UsgaUser, write.Header);
        using var request = write.Request(scenario);
        var response = await usga.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (write.PersonalNoEffect)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            JsonDocument.Parse(body).RootElement.GetProperty("data").GetInt32().Should().Be(0, "o aviso da Trapiche não é marcado");
        }
        else
        {
            response.IsSuccessStatusCode.Should().BeFalse($"{route} sobre a Trapiche deve ser recusada: {body}");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"recurso de outra empresa é 404 em {route}: {body}");
        }
        (await TrapicheFingerprintAsync(scenario)).Should().Be(before, $"{route} recusada não pode alterar nada da Trapiche");
    }

    /// <summary>Retrato dos dados da Trapiche que as sondas poderiam alterar: tem de ser igual antes e depois.</summary>
    private async Task<string> TrapicheFingerprintAsync(CompanyScenario scenario)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var trap = DatabaseSeeder.TrapicheCompanyId;
        var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == scenario.TrapEmployee);
        var parts = new object?[]
        {
            await db.Employees.CountAsync(e => e.CompanyId == trap),
            employee.Status, employee.FullName, employee.ManagementUnitId, employee.SupervisorId, employee.UserId,
            (await db.InfractionTypes.AsNoTracking().SingleAsync(i => i.Id == scenario.TrapInfraction)) is var i ? $"{i.Name}|{i.IsActive}" : null,
            await db.InfractionTypes.CountAsync(x => x.CompanyId == trap),
            (await db.ManagementUnits.AsNoTracking().SingleAsync(m => m.Id == scenario.TrapUnit)).Name,
            (await db.DisciplinaryEvidences.AsNoTracking().SingleAsync(e => e.Id == scenario.TrapEvidence)).IsDeleted,
            await db.DisciplinaryCases.CountAsync(c => c.CompanyId == trap),
            await db.DisciplinaryCases.Where(c => c.CompanyId == trap).Select(c => c.Status).OrderBy(x => x).Select(x => (int)x).SumAsync(),
            await db.DisciplinaryDecisions.CountAsync(d => d.DisciplinaryCaseId == scenario.TrapRecord),
            await db.DisciplinaryOccurrences.CountAsync(o => o.DisciplinaryCaseId == scenario.TrapRecord),
            await db.UserRoles.CountAsync(ur => ur.CompanyId == trap),
            await db.UserManagementUnits.CountAsync(x => x.UserId == scenario.TrapManagerUserId),
            (await db.Users.AsNoTracking().SingleAsync(u => u.Id == scenario.TrapManagerUserId)).Status,
            await db.Users.CountAsync(),
            await db.EmployeeImportBatches.CountAsync(b => b.CompanyId == trap && b.AppliedAt != null),
            await db.DisciplinaryHistoryImportBatches.CountAsync(b => b.CompanyId == trap && b.AppliedAt != null),
            await db.HistoryImportStates.CountAsync(x => x.CompanyId == trap && x.IsClosed),
            (await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == scenario.TrapNotificationOfUsgaUser)).IsRead,
        };
        return string.Join("|", parts);
    }
}
