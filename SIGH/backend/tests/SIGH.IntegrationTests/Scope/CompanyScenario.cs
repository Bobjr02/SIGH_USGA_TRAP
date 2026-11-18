using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Api.Contracts.Disciplinary;
using SIGH.Demo;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Models;
using SIGH.Application.Disciplinary.DisciplinaryCases.AddEvidence;
using SIGH.Application.Disciplinary.Records;
using SIGH.Domain.Disciplinary.Enums;
using SIGH.Domain.Notifications.Entities;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Context;
using SIGH.Persistence.Seeds;

namespace SIGH.IntegrationTests.Scope;

/// <summary>
/// Cenário de isolamento entre empresas: dados da Trapiche (funcionários TRAP131/TRAP132 importados, medida pendente,
/// medida aprovada com anexo, motivo próprio, lotes de importação, aviso, usuário e vínculos) e dois usuários com TODAS
/// as permissões, cada um em UMA empresa: <see cref="UsgaUser"/> (só USGA) e <see cref="TrapUser"/> (só Trapiche,
/// o controle positivo). Montado uma vez por fixture (as sondas não alteram dados quando o isolamento funciona).
/// </summary>
public sealed class CompanyScenario
{
    public const string UsgaUser = "usga.full@sigh.test";
    public const string TrapUser = "trap.full@sigh.test";
    public const string TrapRegistrar = "trap.registrar@sigh.test";
    public const string TrapManager = "trap.gestor@sigh.test";
    public const string TrapInfractionCode = "TRAPMOTIVO";
    public const string TrapNotificationTitle = "Aviso exclusivo da Trapiche";

    public Guid TrapEmployee { get; private init; }
    public string TrapEmployeeNumber => "70090001";
    public Guid TrapRecord { get; private init; }
    public string TrapRecordNumber { get; private init; } = string.Empty;
    public Guid TrapApprovedRecord { get; private init; }
    public Guid TrapEvidence { get; private init; }
    public Guid TrapInfraction { get; private init; }
    public Guid TrapUnit { get; private init; }
    public Guid TrapJobTitle { get; private init; }
    public Guid TrapManagerUserId { get; private init; }
    public Guid TrapManagerRoleLink { get; private init; }
    public Guid TrapManagerUnitLink { get; private init; }
    public Guid TrapEmployeeBatch { get; private init; }
    public Guid TrapHistoryBatch { get; private init; }
    public Guid TrapNotificationOfUsgaUser { get; private init; }
    public Guid TrapNotificationOfTrapUser { get; private init; }
    public Guid UsgaUserId { get; private init; }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<CustomWebApplicationFactory, CompanyScenario> Cache = new();

    /// <summary>O cenário da fixture (o banco é zerado e montado só na primeira chamada).</summary>
    public static async Task<CompanyScenario> GetAsync(CustomWebApplicationFactory factory)
    {
        await Gate.WaitAsync();
        try
        {
            if (Cache.TryGetValue(factory, out var cached))
                return cached;
            await factory.ResetDatabaseAsync();
            var scenario = await BuildAsync(factory);
            Cache[factory] = scenario;
            return scenario;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");

    private static async Task<CompanyScenario> BuildAsync(CustomWebApplicationFactory factory)
    {
        var usga = DatabaseSeeder.DefaultCompanyId;
        var trap = DatabaseSeeder.TrapicheCompanyId;
        var everything = CustomWebApplicationFactory.FullAccessTestRoleName;

        var usgaUser = await AccessTestHelpers.CreateUserAsync(factory, UsgaUser,
            [new RoleGrant(SystemRoles.Administrator, CompanyId: usga), new RoleGrant(everything, CompanyId: usga)]);
        await AccessTestHelpers.CreateUserAsync(factory, TrapUser,
            [new RoleGrant(SystemRoles.Administrator, CompanyId: trap), new RoleGrant(everything, CompanyId: trap)]);
        await AccessTestHelpers.CreateUserAsync(factory, TrapRegistrar, [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: trap)]);

        using var trapAdmin = await AccessTestHelpers.LoginAsync(factory, TrapUser, CompanyDefaults.TrapicheCode);

        // 1. Funcionários da Trapiche: a primeira importação cria as gerências TRAP131 e TRAP132.
        var employeesFile = TrapicheDemoSpreadsheets.EmployeesXlsx();
        var preview = await PostFileAsync(trapAdmin, "/api/v1/employees/import/preview", employeesFile);
        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        var batchId = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("batchId").GetGuid();
        var confirm = await PostFileAsync(trapAdmin, $"/api/v1/employees/import/{batchId}/confirm", employeesFile, confirmMassTermination: false);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());

        // 2. Motivo próprio da Trapiche (pela API, na empresa ativa).
        var infractionResponse = await trapAdmin.PostAsJsonAsync("/api/v1/infraction-types",
            new { code = TrapInfractionCode, name = "Motivo só da Trapiche", defaultSeverity = (int)InfractionSeverity.Moderate });
        infractionResponse.StatusCode.Should().Be(HttpStatusCode.Created, await infractionResponse.Content.ReadAsStringAsync());
        var infractionId = JsonDocument.Parse(await infractionResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("id").GetGuid();

        // 3. Gestor da TRAP131 (usuário da Trapiche, com perfil e gerência para as sondas de administração).
        var manager = await AccessTestHelpers.CreateUserAsync(factory, TrapManager, [new RoleGrant(SystemRoles.Manager, CompanyId: trap)], "TRAP131");

        Guid employeeId, employee2Id, unitId, jobId, roleLink, unitLink;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            employeeId = await db.Employees.Where(e => e.CompanyId == trap && e.EmployeeNumber == "70090001").Select(e => e.Id).SingleAsync();
            employee2Id = await db.Employees.Where(e => e.CompanyId == trap && e.EmployeeNumber == "70090002").Select(e => e.Id).SingleAsync();
            unitId = await db.ManagementUnits.Where(m => m.CompanyId == trap && m.Code == "TRAP131").Select(m => m.Id).SingleAsync();
            jobId = await db.JobTitles.Where(j => j.CompanyId == trap).Select(j => j.Id).FirstAsync();
            roleLink = await db.UserRoles.Where(ur => ur.UserId == manager.Id).Select(ur => ur.Id).SingleAsync();
            unitLink = await db.UserManagementUnits.Where(x => x.UserId == manager.Id).Select(x => x.Id).SingleAsync();
        }

        // 4. Medidas da Trapiche: uma pendente (com anexo) e uma aprovada.
        using var registrar = await AccessTestHelpers.LoginAsync(factory, TrapRegistrar, CompanyDefaults.TrapicheCode);
        var today = BusinessCalendar.Today(factory.Clock.UtcNow);
        var pending = await ReferenceData.RegisterAsync(registrar, ReferenceData.Warning(employeeId, infractionId, today.AddDays(-2)));
        var approved = await ReferenceData.RegisterAsync(registrar, ReferenceData.Warning(employee2Id, infractionId, today.AddDays(-3)));
        (await trapAdmin.PostAsync($"/api/v1/disciplinary-records/{approved.Summary.Id}/approve", null)).EnsureSuccessStatusCode();

        var created = await registrar.PostAsJsonAsync($"/api/v1/disciplinary-cases/{pending.Summary.Id}/evidences",
            new AddEvidenceHttpRequest(EvidenceType.Document, "Carta assinada da Trapiche", DateTimeOffset.UtcNow));
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync());
        var evidenceId = (await created.Content.ReadFromJsonAsync<Result<AddEvidenceResponse>>())!.Data!.Id;
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(PdfBytes), "file", "carta.pdf");
            (await registrar.PostAsync($"/api/v1/disciplinary-cases/{pending.Summary.Id}/evidences/{evidenceId}/file", form)).EnsureSuccessStatusCode();
        }

        // 5. Lote do histórico da Trapiche (pré-visualização) e importação encerrada só na Trapiche.
        var historyFile = TrapicheDemoSpreadsheets.HistoryXlsx();
        var historyPreview = await PostFileAsync(trapAdmin, "/api/v1/disciplinary-history/import/preview", historyFile);
        historyPreview.StatusCode.Should().Be(HttpStatusCode.OK, await historyPreview.Content.ReadAsStringAsync());
        var historyBatch = JsonDocument.Parse(await historyPreview.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("batchId").GetGuid();
        (await trapAdmin.PostAsync("/api/v1/disciplinary-history/import/close", null)).EnsureSuccessStatusCode();

        // 6. Avisos da Trapiche: um do usuário da Trapiche e um gravado (por engano) para o usuário da USGA — a empresa
        //    do aviso decide quem o vê, não só o destinatário.
        var notificationOfUsga = Notification.Create(trap, usgaUser.Id, TrapNotificationTitle, "Aviso da Trapiche.", disciplinaryCaseId: pending.Summary.Id);
        Guid notificationOfTrap;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            var trapUserId = await db.Users.Where(u => u.Email == TrapUser).Select(u => u.Id).SingleAsync();
            var ofTrap = Notification.Create(trap, trapUserId, TrapNotificationTitle, "Aviso da Trapiche.", disciplinaryCaseId: pending.Summary.Id);
            db.Notifications.AddRange(notificationOfUsga, ofTrap);
            await db.SaveChangesAsync();
            notificationOfTrap = ofTrap.Id;
        }

        return new CompanyScenario
        {
            TrapEmployee = employeeId,
            TrapRecord = pending.Summary.Id,
            TrapRecordNumber = pending.Summary.CaseNumber,
            TrapApprovedRecord = approved.Summary.Id,
            TrapEvidence = evidenceId,
            TrapInfraction = infractionId,
            TrapUnit = unitId,
            TrapJobTitle = jobId,
            TrapManagerUserId = manager.Id,
            TrapManagerRoleLink = roleLink,
            TrapManagerUnitLink = unitLink,
            TrapEmployeeBatch = batchId,
            TrapHistoryBatch = historyBatch,
            TrapNotificationOfUsgaUser = notificationOfUsga.Id,
            TrapNotificationOfTrapUser = notificationOfTrap,
            UsgaUserId = usgaUser.Id
        };
    }

    public static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, string url, byte[] file, bool? confirmMassTermination = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(file), "file", "planilha.xlsx");
        if (confirmMassTermination is { } c)
            form.Add(new StringContent(c ? "true" : "false"), "confirmMassTermination");
        return await client.PostAsync(url, form);
    }
}
