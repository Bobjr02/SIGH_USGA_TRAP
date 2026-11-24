using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Api.Contracts.Disciplinary;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Models;
using SIGH.Application.Disciplinary.DisciplinaryCases.AddEvidence;
using SIGH.Application.Disciplinary.Records;
using SIGH.Demo;
using SIGH.Domain.Disciplinary.Enums;
using SIGH.Domain.Notifications.Entities;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Context;
using SIGH.Persistence.Seeds;

namespace SIGH.IntegrationTests.Scope;

/// <summary>
/// Perfis DIFERENTES em cada empresa, conferidos no modo "Todas". Dados fictícios nas duas empresas:
/// - USGA: carga inicial de referência (gerências USGA131 e USGA132), motivo FALTA, medidas da USGA131 (pendente) e
///   da USGA132 (aprovada, com anexo), lote de funcionários e lote do histórico (pré-visualização);
/// - Trapiche: planilhas de demonstração (TRAP220 e TRAP230), motivo TRAPMOTIVO, medidas da TRAP220 (pendente) e da
///   TRAP230 (aprovada, com anexo), lote de funcionários e lote do histórico (pré-visualização);
/// - para cada usuário misto, cinco avisos: dois da USGA (um geral e um da medida da USGA132) e três da Trapiche
///   (um geral, um da medida da TRAP220 e um da TRAP230).
///
/// Usuários mistos (o que cada um deve ver é conferido em <see cref="MixedProfilesTests"/>):
/// - <see cref="RegistrarUsgaManagerTrap"/>: RH – Registro na USGA e só Gestor da TRAP220 na Trapiche;
/// - <see cref="ManagerUsgaRegistrarTrap"/>: o inverso (Gestor da USGA131 na USGA, RH – Registro na Trapiche);
/// - <see cref="AdminUsgaManagerTrap"/> e <see cref="ManagerUsgaAdminTrap"/>: Administrador numa empresa e Gestor na
///   outra (complementares: exercitam lotes, usuários, gerências, motivos e auditoria, que nem o RH – Registro nem o
///   Gestor enxergam em empresa nenhuma).
/// </summary>
public sealed class MixedProfilesScenario
{
    public const string RegistrarUsgaManagerTrap = "pm.registro-usga.gestor-trap@sigh.test";
    public const string ManagerUsgaRegistrarTrap = "pm.gestor-usga.registro-trap@sigh.test";
    public const string AdminUsgaManagerTrap = "pm.admin-usga.gestor-trap@sigh.test";
    public const string ManagerUsgaAdminTrap = "pm.gestor-usga.admin-trap@sigh.test";

    /// <summary>Pessoas de uma empresa só (para as listas de usuários).</summary>
    public const string UsgaRegistrar = "pm.so-usga.registro@sigh.test";
    public const string UsgaApprover = "pm.so-usga.aprovador@sigh.test";
    public const string TrapAdmin = "pm.so-trap.admin@sigh.test";
    public const string TrapRegistrar = "pm.so-trap.registro@sigh.test";
    public const string TrapApprover = "pm.so-trap.aprovador@sigh.test";

    public const string UsgaInfractionCode = "FALTA";
    public const string TrapInfractionCode = "TRAPMOTIVO";

    public const string NoticeUsgaGeneral = "Aviso PM USGA geral";
    public const string NoticeUsga132 = "Aviso PM USGA132";
    public const string NoticeTrapGeneral = "Aviso PM TRAP geral";
    public const string NoticeTrap220 = "Aviso PM TRAP220";
    public const string NoticeTrap230 = "Aviso PM TRAP230";

    /// <summary>Funcionários (matrícula) de cada gerência.</summary>
    public const string EmployeeUsga131 = "50090006";
    public const string EmployeeUsga132 = "50090000";
    public const string EmployeeTrap220 = "70090001";
    public const string EmployeeTrap230 = "70090007";

    public sealed record RecordRef(Guid Id, string Number, Guid EmployeeId, Guid? EvidenceId);

    public RecordRef Usga131 { get; private init; } = null!;
    public RecordRef Usga132 { get; private init; } = null!;
    public RecordRef Trap220 { get; private init; } = null!;
    public RecordRef Trap230 { get; private init; } = null!;
    public Guid UsgaInfraction { get; private init; }
    public Guid TrapInfraction { get; private init; }
    public Guid UsgaEmployeeBatch { get; private init; }
    public Guid TrapEmployeeBatch { get; private init; }
    public Guid UsgaHistoryBatch { get; private init; }
    public Guid TrapHistoryBatch { get; private init; }
    public Guid UsgaRegistrarId { get; private init; }
    public Guid TrapRegistrarId { get; private init; }
    public Guid ManagerUsgaRegistrarTrapId { get; private init; }
    public Guid RegistrarUsgaManagerTrapId { get; private init; }
    public Guid UsgaUnit131 { get; private init; }
    public Guid UsgaUnit132 { get; private init; }
    public Guid TrapUnit220 { get; private init; }
    public Guid TrapUnit230 { get; private init; }
    public string UsgaName { get; private init; } = string.Empty;
    public string TrapName { get; private init; } = string.Empty;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<CustomWebApplicationFactory, MixedProfilesScenario> Cache = new();

    /// <summary>O cenário da fixture (o banco é zerado e montado só na primeira chamada; as sondas só leem).</summary>
    public static async Task<MixedProfilesScenario> GetAsync(CustomWebApplicationFactory factory)
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

    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Referencias");

    private static async Task<MixedProfilesScenario> BuildAsync(CustomWebApplicationFactory factory)
    {
        var usga = DatabaseSeeder.DefaultCompanyId;
        var trap = DatabaseSeeder.TrapicheCompanyId;
        var today = BusinessCalendar.Today(factory.Clock.UtcNow);

        // ---- USGA ----
        await ReferenceData.LoadEmployeesAsync(factory);
        var usgaInfraction = await ReferenceData.InfractionTypeAsync(factory, UsgaInfractionCode);
        var usgaRegistrar = await AccessTestHelpers.CreateUserAsync(factory, UsgaRegistrar, [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: usga)]);
        await AccessTestHelpers.CreateUserAsync(factory, UsgaApprover, [new RoleGrant(SystemRoles.HrApprover, CompanyId: usga)]);

        using var usgaAdmin = await AccessTestHelpers.FixtureAdminAsync(factory);
        var usgaHistory = await PreviewAsync(usgaAdmin, "/api/v1/disciplinary-history/import/preview",
            File.ReadAllBytes(Path.Combine(FixtureDir, "base_disciplinar_ficticia.xlsx")));

        // ---- Trapiche ----
        await AccessTestHelpers.CreateUserAsync(factory, TrapAdmin,
            [new RoleGrant(SystemRoles.Administrator, CompanyId: trap), new RoleGrant(CustomWebApplicationFactory.FullAccessTestRoleName, CompanyId: trap)]);
        var trapRegistrar = await AccessTestHelpers.CreateUserAsync(factory, TrapRegistrar, [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: trap)]);
        await AccessTestHelpers.CreateUserAsync(factory, TrapApprover, [new RoleGrant(SystemRoles.HrApprover, CompanyId: trap)]);

        using var trapAdmin = await AccessTestHelpers.LoginAsync(factory, TrapAdmin, CompanyDefaults.TrapicheCode);
        var employeesFile = TrapicheDemoSpreadsheets.EmployeesXlsx();
        var trapBatch = await PreviewAsync(trapAdmin, "/api/v1/employees/import/preview", employeesFile);
        var confirm = await CompanyScenario.PostFileAsync(trapAdmin, $"/api/v1/employees/import/{trapBatch}/confirm", employeesFile, confirmMassTermination: false);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());

        var infraction = await trapAdmin.PostAsJsonAsync("/api/v1/infraction-types",
            new { code = TrapInfractionCode, name = "Motivo só da Trapiche", defaultSeverity = (int)InfractionSeverity.Moderate });
        infraction.StatusCode.Should().Be(HttpStatusCode.Created, await infraction.Content.ReadAsStringAsync());
        var trapInfraction = JsonDocument.Parse(await infraction.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var trapHistory = await PreviewAsync(trapAdmin, "/api/v1/disciplinary-history/import/preview", TrapicheDemoSpreadsheets.HistoryXlsx());

        // ---- Usuários mistos (gerências: códigos únicos entre as empresas) ----
        var regUsgaGesTrap = await AccessTestHelpers.CreateUserAsync(factory, RegistrarUsgaManagerTrap,
            [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: usga), new RoleGrant(SystemRoles.Manager, CompanyId: trap)], "TRAP220");
        var gesUsgaRegTrap = await AccessTestHelpers.CreateUserAsync(factory, ManagerUsgaRegistrarTrap,
            [new RoleGrant(SystemRoles.Manager, CompanyId: usga), new RoleGrant(SystemRoles.HrRegistrar, CompanyId: trap)], "USGA131");
        var admUsgaGesTrap = await AccessTestHelpers.CreateUserAsync(factory, AdminUsgaManagerTrap,
            [new RoleGrant(SystemRoles.Administrator, CompanyId: usga), new RoleGrant(SystemRoles.Manager, CompanyId: trap)], "TRAP220");
        var gesUsgaAdmTrap = await AccessTestHelpers.CreateUserAsync(factory, ManagerUsgaAdminTrap,
            [new RoleGrant(SystemRoles.Manager, CompanyId: usga), new RoleGrant(SystemRoles.Administrator, CompanyId: trap)], "USGA131");

        // ---- Medidas: uma pendente na 131 e uma aprovada (com anexo) na 132, em cada empresa ----
        Guid Employee(SighDbContext db, Guid company, string number) =>
            db.Employees.Where(e => e.CompanyId == company && e.EmployeeNumber == number).Select(e => e.Id).Single();
        Guid Unit(SighDbContext db, string code) => db.ManagementUnits.Where(m => m.Code == code).Select(m => m.Id).Single();

        Guid eU131, eU132, eT220, eT230, uU131, uU132, uT220, uT230;
        string usgaName, trapName;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            eU131 = Employee(db, usga, EmployeeUsga131);
            eU132 = Employee(db, usga, EmployeeUsga132);
            eT220 = Employee(db, trap, EmployeeTrap220);
            eT230 = Employee(db, trap, EmployeeTrap230);
            uU131 = Unit(db, "USGA131");
            uU132 = Unit(db, "USGA132");
            uT220 = Unit(db, "TRAP220");
            uT230 = Unit(db, "TRAP230");
            usgaName = await db.Companies.Where(c => c.Id == usga).Select(c => c.Name).SingleAsync();
            trapName = await db.Companies.Where(c => c.Id == trap).Select(c => c.Name).SingleAsync();
        }

        using var usgaReg = await AccessTestHelpers.LoginAsync(factory, UsgaRegistrar);
        using var usgaApp = await AccessTestHelpers.LoginAsync(factory, UsgaApprover);
        using var trapReg = await AccessTestHelpers.LoginAsync(factory, TrapRegistrar);
        using var trapApp = await AccessTestHelpers.LoginAsync(factory, TrapApprover);

        var rU131 = await ReferenceData.RegisterAsync(usgaReg, ReferenceData.Warning(eU131, usgaInfraction, today.AddDays(-2)));
        var rU132 = await ReferenceData.RegisterAsync(usgaReg, ReferenceData.Warning(eU132, usgaInfraction, today.AddDays(-3)));
        var rT220 = await ReferenceData.RegisterAsync(trapReg, ReferenceData.Warning(eT220, trapInfraction, today.AddDays(-2)));
        var rT230 = await ReferenceData.RegisterAsync(trapReg, ReferenceData.Warning(eT230, trapInfraction, today.AddDays(-3)));
        var evU132 = await AttachAsync(usgaReg, rU132.Summary.Id);
        var evT230 = await AttachAsync(trapReg, rT230.Summary.Id);
        (await usgaApp.PostAsync($"/api/v1/disciplinary-records/{rU132.Summary.Id}/approve", null)).EnsureSuccessStatusCode();
        (await trapApp.PostAsync($"/api/v1/disciplinary-records/{rT230.Summary.Id}/approve", null)).EnsureSuccessStatusCode();

        // ---- Avisos: os automáticos do fluxo ficam lidos; o contador mede só os cinco abaixo ----
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            var mixed = new[] { regUsgaGesTrap.Id, gesUsgaRegTrap.Id, admUsgaGesTrap.Id, gesUsgaAdmTrap.Id };
            foreach (var automatic in await db.Notifications.Where(n => mixed.Contains(n.UserId)).ToListAsync())
                automatic.MarkAsRead(DateTimeOffset.UtcNow);
            foreach (var user in mixed)
            {
                db.Notifications.Add(Notification.Create(usga, user, NoticeUsgaGeneral, "Sem medida vinculada."));
                db.Notifications.Add(Notification.Create(usga, user, NoticeUsga132, "Medida da USGA132.", disciplinaryCaseId: rU132.Summary.Id));
                db.Notifications.Add(Notification.Create(trap, user, NoticeTrapGeneral, "Sem medida vinculada."));
                db.Notifications.Add(Notification.Create(trap, user, NoticeTrap220, "Medida da TRAP220.", disciplinaryCaseId: rT220.Summary.Id));
                db.Notifications.Add(Notification.Create(trap, user, NoticeTrap230, "Medida da TRAP230.", disciplinaryCaseId: rT230.Summary.Id));
            }
            await db.SaveChangesAsync();
        }

        var usgaBatch = await LatestEmployeeBatchAsync(factory, usga);

        return new MixedProfilesScenario
        {
            Usga131 = new(rU131.Summary.Id, rU131.Summary.CaseNumber, eU131, null),
            Usga132 = new(rU132.Summary.Id, rU132.Summary.CaseNumber, eU132, evU132),
            Trap220 = new(rT220.Summary.Id, rT220.Summary.CaseNumber, eT220, null),
            Trap230 = new(rT230.Summary.Id, rT230.Summary.CaseNumber, eT230, evT230),
            UsgaInfraction = usgaInfraction,
            TrapInfraction = trapInfraction,
            UsgaEmployeeBatch = usgaBatch,
            TrapEmployeeBatch = trapBatch,
            UsgaHistoryBatch = usgaHistory,
            TrapHistoryBatch = trapHistory,
            UsgaRegistrarId = usgaRegistrar.Id,
            TrapRegistrarId = trapRegistrar.Id,
            ManagerUsgaRegistrarTrapId = gesUsgaRegTrap.Id,
            RegistrarUsgaManagerTrapId = regUsgaGesTrap.Id,
            UsgaUnit131 = uU131,
            UsgaUnit132 = uU132,
            TrapUnit220 = uT220,
            TrapUnit230 = uT230,
            UsgaName = usgaName,
            TrapName = trapName
        };
    }

    private static async Task<Guid> PreviewAsync(HttpClient client, string url, byte[] file)
    {
        var response = await CompanyScenario.PostFileAsync(client, url, file);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("batchId").GetGuid();
    }

    private static async Task<Guid> AttachAsync(HttpClient registrar, Guid recordId)
    {
        var created = await registrar.PostAsJsonAsync($"/api/v1/disciplinary-cases/{recordId}/evidences",
            new AddEvidenceHttpRequest(EvidenceType.Document, "Carta assinada (fictícia)", DateTimeOffset.UtcNow));
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync());
        var evidenceId = (await created.Content.ReadFromJsonAsync<Result<AddEvidenceResponse>>())!.Data!.Id;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CompanyScenario.PdfBytes), "file", "carta.pdf");
        (await registrar.PostAsync($"/api/v1/disciplinary-cases/{recordId}/evidences/{evidenceId}/file", form)).EnsureSuccessStatusCode();
        return evidenceId;
    }

    private static async Task<Guid> LatestEmployeeBatchAsync(CustomWebApplicationFactory factory, Guid companyId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        return await db.EmployeeImportBatches.Where(b => b.CompanyId == companyId).OrderByDescending(b => b.PreviewedAt).Select(b => b.Id).FirstAsync();
    }
}
