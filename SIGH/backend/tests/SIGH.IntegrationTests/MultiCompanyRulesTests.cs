using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Demo;
using SIGH.Application.Authentication.CurrentUser;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Models;
using SIGH.Application.Disciplinary.Records;
using SIGH.Application.Employees.Import;
using SIGH.Application.Users.Access;
using SIGH.Domain.Enums;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.IntegrationTests.Scope;
using SIGH.Persistence.Context;
using SIGH.Persistence.Seeds;
using Xunit;

namespace SIGH.IntegrationTests;

/// <summary>
/// Regras da multiempresa: administrador por empresa (regra 4), empresa ativa e modo "Todas" só leitura (regra 5),
/// permissão avaliada na empresa do recurso (regra 6), funcionários e importações por empresa (regras 7 a 9),
/// e-mails com o nome da empresa (regra 11) e /auth/me com as empresas.
/// </summary>
public class MultiCompanyRulesTests : IntegrationTestBase
{
    private static readonly Guid Usga = DatabaseSeeder.DefaultCompanyId;
    private static readonly Guid Trap = DatabaseSeeder.TrapicheCompanyId;

    public MultiCompanyRulesTests(CustomWebApplicationFactory factory) : base(factory) { }

    private static async Task<Guid> RoleIdAsync(HttpClient client, string name) =>
        (await client.GetFromJsonAsync<Result<List<RoleOptionDto>>>("/api/v1/roles"))!.Data!.Single(r => r.Name == name).Id;

    /// <summary>Corpo da resposta; num ProblemDetails, o "detail" já sem os escapes do JSON.</summary>
    private static async Task<string> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("detail", out var detail)
                ? detail.GetString() ?? text : text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;

    private Task<TestUser> TrapAdminAsync(string email = "admin.trap@sigh.test") =>
        AccessTestHelpers.CreateUserAsync(Factory, email, [new RoleGrant(SystemRoles.Administrator, CompanyId: Trap)]);

    /// <summary>Importa as planilhas fictícias da Trapiche (cria TRAP131 e TRAP132) como o administrador dela.</summary>
    private async Task ImportTrapicheEmployeesAsync(HttpClient trapAdmin)
    {
        var file = TrapicheDemoSpreadsheets.EmployeesXlsx();
        var preview = await CompanyScenario.PostFileAsync(trapAdmin, "/api/v1/employees/import/preview", file);
        preview.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(preview));
        var batch = (await preview.Content.ReadFromJsonAsync<Result<EmployeeImportPreviewDto>>())!.Data!.BatchId;
        (await CompanyScenario.PostFileAsync(trapAdmin, $"/api/v1/employees/import/{batch}/confirm", file, false)).EnsureSuccessStatusCode();
    }

    // ---------------- Regra 4: administrador por empresa ----------------

    [Fact]
    public async Task CreatingAUserWithAnExistingEmail_OffersTheLink_AndTheLinkReusesTheAccount_WithoutShowingTheOtherCompany()
    {
        await TrapAdminAsync();
        var usgaPerson = await AccessTestHelpers.CreateUserAsync(Factory, "juridico@sigh.test", [new RoleGrant(SystemRoles.Legal, CompanyId: Usga)]);
        using var trapAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test", CompanyDefaults.TrapicheCode);
        var legal = await RoleIdAsync(trapAdmin, SystemRoles.Legal);
        int UsersCount() => Factory.Services.CreateScope().ServiceProvider.GetRequiredService<SighDbContext>().Users.Count();
        var usersBefore = UsersCount();

        // Antes do vínculo o administrador da Trapiche não enxerga a pessoa da USGA.
        (await trapAdmin.GetAsync($"/api/v1/users/{usgaPerson.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var create = await trapAdmin.PostAsJsonAsync("/api/v1/users", new
        {
            fullName = "OUTRO NOME", email = "JURIDICO@sigh.test", cpf = "52998224725", initialPassword = "Senha@Forte123", roleIds = new[] { legal }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(create));
        (await CodeAsync(create)).Should().Be("USER_EXISTS_IN_ANOTHER_COMPANY");
        (await BodyAsync(create)).Should().NotContain(SystemRoles.Legal, "os perfis da outra empresa não aparecem");

        var link = await trapAdmin.PostAsJsonAsync("/api/v1/users/link", new LinkExistingUserRequest("juridico@sigh.test", [legal]));
        link.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(link));
        var linked = (await link.Content.ReadFromJsonAsync<Result<LinkExistingUserResponse>>())!.Data!;
        linked.UserId.Should().Be(usgaPerson.Id, "o vínculo usa a MESMA conta");
        linked.Access.Roles.Should().ContainSingle().Which.CompanyId.Should().Be(Trap);
        linked.Access.HasAccessInOtherCompany.Should().BeTrue();
        UsersCount().Should().Be(usersBefore, "nenhuma conta nova é criada");

        // Agora ele aparece para o administrador da Trapiche, só com o perfil da Trapiche.
        var access = (await trapAdmin.GetFromJsonAsync<Result<UserAccessDto>>($"/api/v1/users/{usgaPerson.Id}/access"))!.Data!;
        access.Roles.Should().OnlyContain(r => r.CompanyId == Trap);
        var detail = await trapAdmin.GetStringAsync($"/api/v1/users/{usgaPerson.Id}");
        detail.Should().Contain(SystemRoles.Legal);

        // E a pessoa passa a ter as duas empresas, com o Jurídico em cada uma.
        using var person = await AccessTestHelpers.LoginAsync(Factory, "juridico@sigh.test");
        var me = (await person.GetFromJsonAsync<Result<CurrentUserResponse>>("/api/v1/auth/me"))!.Data!;
        me.Companies!.Select(c => c.Code).Should().BeEquivalentTo(["USGA", "TRAP"]);
        me.Companies!.Should().OnlyContain(c => c.Roles.Contains(SystemRoles.Legal) && c.Permissions.Contains(PermissionCodes.Audit.View));
    }

    [Fact]
    public async Task TrapicheAdmin_DoesNotSeeUsgaUsers_AndTheUsgaAdminDoesNotSeeTrapicheUsers()
    {
        var trapAdmin = await TrapAdminAsync();
        var usgaOnly = await AccessTestHelpers.CreateUserAsync(Factory, "auxiliar.usga@sigh.test", SystemRoles.HrRegistrar);
        using var trap = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test");
        using var usga = await AccessTestHelpers.FixtureAdminAsync(Factory);

        var trapList = await trap.GetStringAsync("/api/v1/users?pageSize=100");
        trapList.Should().NotContain("auxiliar.usga@sigh.test").And.NotContain("admin@sigh.com");
        (await trap.GetAsync($"/api/v1/users/{usgaOnly.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await trap.GetAsync($"/api/v1/users/{usgaOnly.Id}/access")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var usgaList = await usga.GetStringAsync("/api/v1/users?pageSize=100");
        usgaList.Should().Contain("auxiliar.usga@sigh.test").And.NotContain("admin.trap@sigh.test");
        (await usga.GetAsync($"/api/v1/users/{trapAdmin.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeactivatingTheAccount_IsRefusedWhenThePersonHasAccessInAnotherCompany_TheAdminRemovesOnlyHisCompanyRoles()
    {
        await TrapAdminAsync();
        var person = await AccessTestHelpers.CreateUserAsync(Factory, "duas.empresas@sigh.test",
            [new RoleGrant(SystemRoles.Legal, CompanyId: Usga), new RoleGrant(SystemRoles.Legal, CompanyId: Trap)]);
        using var trapAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test");

        var deactivate = await trapAdmin.PatchAsJsonAsync($"/api/v1/users/{person.Id}/status", new { newStatus = UserStatus.Inactive, reason = "saiu" });
        deactivate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(deactivate)).Should().Contain("outra empresa");

        // Desbloqueio: mesma regra.
        (await trapAdmin.PostAsync($"/api/v1/users/{person.Id}/unlock", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Retirar o perfil da Trapiche é permitido: a pessoa perde só a Trapiche.
        var access = (await trapAdmin.GetFromJsonAsync<Result<UserAccessDto>>($"/api/v1/users/{person.Id}/access"))!.Data!;
        access.HasAccessInOtherCompany.Should().BeTrue();
        (await trapAdmin.DeleteAsync($"/api/v1/users/{person.Id}/roles/{access.Roles.Single().Id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var personClient = await AccessTestHelpers.LoginAsync(Factory, "duas.empresas@sigh.test");
        var me = (await personClient.GetFromJsonAsync<Result<CurrentUserResponse>>("/api/v1/auth/me"))!.Data!;
        me.Companies!.Select(c => c.Code).Should().Equal("USGA");
        (await personClient.GetAsync("/api/v1/audit-logs")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Quem só tem perfil na empresa do administrador pode ser desativado por ele.
        var trapOnly = await AccessTestHelpers.CreateUserAsync(Factory, "so.trap@sigh.test", [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: Trap)]);
        (await trapAdmin.PatchAsJsonAsync($"/api/v1/users/{trapOnly.Id}/status", new { newStatus = UserStatus.Inactive, reason = "saiu" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LastAdministrator_IsPerCompany_AndRolesAreAssignedOnlyInTheAdminsCompany()
    {
        var trapAdmin = await TrapAdminAsync();
        var roleManager = await AccessTestHelpers.CreateRoleAsync(Factory, "IntegrationTests.RoleManager", PermissionCodes.Users.View, PermissionCodes.Users.ManageRoles);
        await AccessTestHelpers.CreateUserAsync(Factory, "operador.trap@sigh.test", [new RoleGrant(roleManager, CompanyId: Trap)]);
        using var operador = await AccessTestHelpers.LoginAsync(Factory, "operador.trap@sigh.test");

        // A USGA tem dois administradores (fixture), a Trapiche só um: retirá-lo da Trapiche é bloqueado.
        var access = (await operador.GetFromJsonAsync<Result<UserAccessDto>>($"/api/v1/users/{trapAdmin.Id}/access"))!.Data!;
        var revoke = await operador.DeleteAsync($"/api/v1/users/{trapAdmin.Id}/roles/{access.Roles.Single(r => r.RoleName == SystemRoles.Administrator).Id}");
        revoke.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(revoke)).Should().Contain("último Administrador ativo desta empresa");

        // O perfil atribuído pelo operador da Trapiche vale só na Trapiche.
        var target = await AccessTestHelpers.CreateUserAsync(Factory, "novo.trap@sigh.test", [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: Trap)]);
        var approver = await RoleIdAsync(operador, SystemRoles.HrApprover);
        (await operador.PostAsJsonAsync($"/api/v1/users/{target.Id}/roles", new AssignRoleRequest(approver, null, null))).EnsureSuccessStatusCode();
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        (await db.UserRoles.Where(ur => ur.UserId == target.Id).Select(ur => ur.CompanyId).Distinct().ToListAsync()).Should().Equal(Trap);
    }

    [Fact]
    public async Task ManagerRole_RequiresAUnitOfThatCompany_AndAUnitOfAnotherCompanyIsRefused()
    {
        await TrapAdminAsync();
        using var trapAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test");
        await ImportTrapicheEmployeesAsync(trapAdmin);
        var target = await AccessTestHelpers.CreateUserAsync(Factory, "gestor.trap131@sigh.test", [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: Trap)], "USGA131");
        var manager = await RoleIdAsync(trapAdmin, SystemRoles.Manager);

        // Tem a USGA131 (da USGA), não uma gerência da Trapiche: o Gestor na Trapiche é recusado.
        var refused = await trapAdmin.PostAsJsonAsync($"/api/v1/users/{target.Id}/roles", new AssignRoleRequest(manager, null, null));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(refused)).Should().Contain("gerência desta empresa");

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            var usga131 = await db.ManagementUnits.Where(m => m.Code == "USGA131").Select(m => m.Id).SingleAsync();
            (await trapAdmin.PostAsJsonAsync($"/api/v1/users/{target.Id}/management-units", new AssignManagementUnitRequest(usga131, null, null)))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a gerência tem de ser da empresa do administrador");
        }

        var units = (await trapAdmin.GetFromJsonAsync<Result<List<ManagementUnitDto>>>("/api/v1/management-units"))!.Data!;
        units.Select(u => u.Code).Should().BeEquivalentTo(["TRAP131", "TRAP132"], "a Trapiche começa sem gerências e a importação cria as dela");
        units.Should().OnlyContain(u => u.Name == u.Code, "criadas com o código como nome");
        (await trapAdmin.PostAsJsonAsync($"/api/v1/users/{target.Id}/management-units", new AssignManagementUnitRequest(units.Single(u => u.Code == "TRAP131").Id, null, null))).EnsureSuccessStatusCode();
        (await trapAdmin.PostAsJsonAsync($"/api/v1/users/{target.Id}/roles", new AssignRoleRequest(manager, null, null))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangingRolesInAnyCompany_InvalidatesTheTokenUsedInTheOther()
    {
        await TrapAdminAsync();
        var person = await AccessTestHelpers.CreateUserAsync(Factory, "duas@sigh.test",
            [new RoleGrant(SystemRoles.Legal, CompanyId: Usga), new RoleGrant(SystemRoles.Legal, CompanyId: Trap)]);
        using var personClient = await AccessTestHelpers.LoginAsync(Factory, "duas@sigh.test", CompanyDefaults.UsgaCode);
        (await personClient.GetAsync("/api/v1/employees")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var trapAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test");
        var approver = await RoleIdAsync(trapAdmin, SystemRoles.HrApprover);
        (await trapAdmin.PostAsJsonAsync($"/api/v1/users/{person.Id}/roles", new AssignRoleRequest(approver, null, null))).EnsureSuccessStatusCode();

        Factory.Clock.Advance(TimeSpan.FromMinutes(1)); // passa o cache do SecurityStamp
        (await personClient.GetAsync("/api/v1/employees")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "mudou o perfil na Trapiche: o token usado na USGA cai");
    }

    // ---------------- Regras 5 e 6: empresa ativa, "Todas" só leitura, permissão na empresa do recurso ----------------

    private async Task<(Guid UsgaRecord, Guid TrapRecord)> RecordsInBothCompaniesAsync()
    {
        await ReferenceData.LoadEmployeesAsync(Factory);
        await TrapAdminAsync();
        using var trapAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test");
        await ImportTrapicheEmployeesAsync(trapAdmin);

        var usgaInfraction = await ReferenceData.InfractionTypeAsync(Factory);
        var trapInfraction = await ReferenceData.InfractionTypeAsync(Factory, companyId: Trap);
        await AccessTestHelpers.CreateUserAsync(Factory, "auxiliar.usga@sigh.test", SystemRoles.HrRegistrar);
        await AccessTestHelpers.CreateUserAsync(Factory, "auxiliar.trap@sigh.test", [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: Trap)]);
        using var usgaRegistrar = await AccessTestHelpers.LoginAsync(Factory, "auxiliar.usga@sigh.test");
        using var trapRegistrar = await AccessTestHelpers.LoginAsync(Factory, "auxiliar.trap@sigh.test");
        var today = BusinessCalendar.Today(Factory.Clock.UtcNow);

        var usgaRecord = await ReferenceData.RegisterAsync(usgaRegistrar, ReferenceData.Warning(await ReferenceData.EmployeeIdAsync(Factory, "50090006"), usgaInfraction, today));
        Guid trapEmployee;
        await using (var scope = Factory.Services.CreateAsyncScope())
            trapEmployee = await scope.ServiceProvider.GetRequiredService<SighDbContext>().Employees.Where(e => e.CompanyId == Trap && e.EmployeeNumber == "70090001").Select(e => e.Id).SingleAsync();
        var trapRecord = await ReferenceData.RegisterAsync(trapRegistrar, ReferenceData.Warning(trapEmployee, trapInfraction, today));
        return (usgaRecord.Summary.Id, trapRecord.Summary.Id);
    }

    [Fact]
    public async Task AllCompaniesMode_IsReadOnly_ShowsBothWithTheCompany_AndWritingRequiresASpecificCompany()
    {
        var (usgaRecord, trapRecord) = await RecordsInBothCompaniesAsync();
        await AccessTestHelpers.CreateUserAsync(Factory, "encarregado.duas@sigh.test",
            [new RoleGrant(SystemRoles.HrApprover, CompanyId: Usga), new RoleGrant(SystemRoles.HrApprover, CompanyId: Trap)]);

        using var all = await AccessTestHelpers.LoginAsync(Factory, "encarregado.duas@sigh.test", ICompanyContext.AllCompanies);
        var list = (await all.GetFromJsonAsync<Result<PagedResult<RecordSummaryDto>>>("/api/v1/disciplinary-records?pageSize=100"))!.Data!;
        list.Items.Select(r => r.Id).Should().Contain([usgaRecord, trapRecord]);
        list.Items.Single(r => r.Id == trapRecord).CompanyName.Should().Be("Trapiche");
        list.Items.Single(r => r.Id == usgaRecord).CompanyCode.Should().Be("USGA");
        (await all.GetStringAsync("/api/v1/disciplinary-records/export")).TrimStart('\uFEFF').Should().StartWith("Empresa;");

        // No modo "Todas" o detalhe não oferece nenhuma ação, e qualquer escrita é recusada (400, COMPANY_REQUIRED).
        var detail = (await all.GetFromJsonAsync<Result<RecordDetailDto>>($"/api/v1/disciplinary-records/{trapRecord}"))!.Data!;
        detail.Actions.CanApprove.Should().BeFalse();
        var approve = await all.PostAsync($"/api/v1/disciplinary-records/{trapRecord}/approve", null);
        approve.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeAsync(approve)).Should().Be(CompanyRequiredException.ErrorCode);

        // Com a Trapiche selecionada (e Aprovar na Trapiche), aprova; com a USGA selecionada, a medida da Trapiche é 404.
        using var usgaSelected = await AccessTestHelpers.LoginAsync(Factory, "encarregado.duas@sigh.test", CompanyDefaults.UsgaCode);
        (await usgaSelected.PostAsync($"/api/v1/disciplinary-records/{trapRecord}/approve", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var trapSelected = await AccessTestHelpers.LoginAsync(Factory, "encarregado.duas@sigh.test", CompanyDefaults.TrapicheCode);
        (await trapSelected.GetFromJsonAsync<Result<RecordDetailDto>>($"/api/v1/disciplinary-records/{trapRecord}"))!.Data!.Actions.CanApprove.Should().BeTrue();
        (await trapSelected.PostAsync($"/api/v1/disciplinary-records/{trapRecord}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Permission_IsEvaluatedInTheCompanyOfTheResource()
    {
        var (_, trapRecord) = await RecordsInBothCompaniesAsync();
        // Aprovador na USGA, mas só Jurídico na Trapiche.
        await AccessTestHelpers.CreateUserAsync(Factory, "misto@sigh.test",
            [new RoleGrant(SystemRoles.HrApprover, CompanyId: Usga), new RoleGrant(SystemRoles.Legal, CompanyId: Trap)]);

        using var trapSelected = await AccessTestHelpers.LoginAsync(Factory, "misto@sigh.test", CompanyDefaults.TrapicheCode);
        (await trapSelected.GetAsync($"/api/v1/disciplinary-records/{trapRecord}")).StatusCode.Should().Be(HttpStatusCode.OK, "o Jurídico vê");
        (await trapSelected.PostAsync($"/api/v1/disciplinary-records/{trapRecord}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "aprovar exige a permissão NA Trapiche, e ele só a tem na USGA");

        using var all = await AccessTestHelpers.LoginAsync(Factory, "misto@sigh.test", ICompanyContext.AllCompanies);
        (await all.GetFromJsonAsync<Result<int>>("/api/v1/disciplinary-records/pending-count"))!.Data.Should().Be(1, "só conta a pendente da USGA, onde ele aprova");
    }

    [Fact]
    public async Task SingleCompanyUser_WorksWithoutTheHeader_AndAllModeShowsOnlyHisCompany()
    {
        var (usgaRecord, trapRecord) = await RecordsInBothCompaniesAsync();
        await AccessTestHelpers.CreateUserAsync(Factory, "encarregado.usga@sigh.test", SystemRoles.HrApprover);

        using var noHeader = await AccessTestHelpers.LoginAsync(Factory, "encarregado.usga@sigh.test");
        (await noHeader.PostAsync($"/api/v1/disciplinary-records/{usgaRecord}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var all = await AccessTestHelpers.LoginAsync(Factory, "encarregado.usga@sigh.test", ICompanyContext.AllCompanies);
        var list = await all.GetStringAsync("/api/v1/disciplinary-records?pageSize=100");
        list.Should().Contain(usgaRecord.ToString()).And.NotContain(trapRecord.ToString());
    }

    // ---------------- Regras 7 a 9: funcionários e importações por empresa ----------------

    [Fact]
    public async Task TheSamePerson_CanExistInBothCompanies_WithTheSameEmployeeNumberAndCpf()
    {
        await TrapAdminAsync();
        await AccessTestHelpers.CreateUserAsync(Factory, "admin.usga@sigh.test", [new RoleGrant(SystemRoles.Administrator, CompanyId: Usga)]);
        using var trapAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test");
        using var usgaAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.usga@sigh.test");

        var sameInUsga = TrapicheDemoSpreadsheets.Employees.Take(1).Select(e => e with { Unit = "USGA131" }).ToList();
        var usgaFile = TrapicheDemoSpreadsheets.EmployeesXlsx(sameInUsga);
        var usgaPreview = (await (await CompanyScenario.PostFileAsync(usgaAdmin, "/api/v1/employees/import/preview", usgaFile)).Content.ReadFromJsonAsync<Result<EmployeeImportPreviewDto>>())!.Data!;
        (await CompanyScenario.PostFileAsync(usgaAdmin, $"/api/v1/employees/import/{usgaPreview.BatchId}/confirm", usgaFile, false)).EnsureSuccessStatusCode();
        await ImportTrapicheEmployeesAsync(trapAdmin);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var both = await db.Employees.Where(e => e.EmployeeNumber == "70090001").ToListAsync();
        both.Select(e => e.CompanyId).Should().BeEquivalentTo([Usga, Trap]);
        both.Select(e => e.Cpf).Distinct().Should().ContainSingle("mesmo CPF, cadastros separados");
    }

    [Fact]
    public async Task ImportingAFileOfAnotherCompany_RejectsItsRows_ShowsTheAlert_AndCannotBeConfirmed()
    {
        await AccessTestHelpers.CreateUserAsync(Factory, "admin.usga@sigh.test", [new RoleGrant(SystemRoles.Administrator, CompanyId: Usga)]);
        using var usgaAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.usga@sigh.test");
        var trapFile = TrapicheDemoSpreadsheets.EmployeesXlsx();

        var response = await CompanyScenario.PostFileAsync(usgaAdmin, "/api/v1/employees/import/preview", trapFile);
        var preview = (await response.Content.ReadFromJsonAsync<Result<EmployeeImportPreviewDto>>())!.Data!;
        preview.CompanyCode.Should().Be("USGA");
        preview.CompanyName.Should().NotBeNullOrWhiteSpace();
        preview.LooksLikeOtherCompany.Should().BeTrue();
        preview.OtherCompanyRows.Should().Be(TrapicheDemoSpreadsheets.Employees.Count);
        preview.Errors.Should().HaveCount(TrapicheDemoSpreadsheets.Employees.Count)
            .And.OnlyContain(e => e.Message.Contains("gerência de outra empresa — confira se o arquivo é desta empresa"));
        preview.Summary.Created.Should().Be(0);

        var confirm = await CompanyScenario.PostFileAsync(usgaAdmin, $"/api/v1/employees/import/{preview.BatchId}/confirm", trapFile, true);
        confirm.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await BodyAsync(confirm)).Should().Contain("parece ser de outra empresa");
        await using var scope = Factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SighDbContext>().Employees.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task HistoryImport_IsPerCompany_MatchesTheTrapicheUnitNames_RejectsOtherCompanyUnits_AndClosesOnlyThatCompany()
    {
        await TrapAdminAsync();
        using var trapAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test");
        await ImportTrapicheEmployeesAsync(trapAdmin);
        foreach (var (code, name) in new[] { ("FALTA", "Faltas injustificadas de forma recorrente"), ("EPI", "Falta de utilização do EPI"), ("ATRASO", "Atrasos reiterados") })
            await ReferenceData.InfractionTypeAsync(Factory, code, name, Trap);

        // Uma linha com a GERÊNCIA de uma gerência só da USGA ("Industrial"): rejeitada como de outra empresa.
        var history = TrapicheDemoSpreadsheets.HistoryXlsx(unitOf: h => h.Number == "70090007" ? "INDUSTRIAL" : h.Number == "70090009" ? "GERENCIA QUE NAO EXISTE" : "TRAP131");
        var response = await CompanyScenario.PostFileAsync(trapAdmin, "/api/v1/disciplinary-history/import/preview", history);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(response));
        var preview = (await response.Content.ReadFromJsonAsync<Result<SIGH.Application.Disciplinary.History.HistoryImportPreviewDto>>())!.Data!;
        preview.CompanyName.Should().Be("Trapiche");
        preview.Rejected.Should().ContainSingle(r => r.EmployeeNumber == "70090007")
            .Which.Message.Should().Contain("gerência de outra empresa — confira se o arquivo é desta empresa");
        preview.Warnings.Should().Contain(w => w.EmployeeNumber == "70090009" && w.Message.Contains("usada a gerência atual"));
        preview.ToImport.Should().HaveCount(TrapicheDemoSpreadsheets.History.Count - 1);
        preview.ToImport.Where(r => r.EmployeeNumber == "70090001").Should().OnlyContain(r => r.ManagementUnitCode == "TRAP131");
        preview.LooksLikeOtherCompany.Should().BeFalse();

        (await CompanyScenario.PostFileAsync(trapAdmin, $"/api/v1/disciplinary-history/import/{preview.BatchId}/confirm", history)).EnsureSuccessStatusCode();
        (await trapAdmin.PostAsync("/api/v1/disciplinary-history/import/close", null)).EnsureSuccessStatusCode();

        // Encerrada na Trapiche, continua aberta na USGA.
        using var usgaAdmin = await AccessTestHelpers.FixtureAdminAsync(Factory);
        (await usgaAdmin.GetStringAsync("/api/v1/disciplinary-history/import/status")).Should().Contain("\"isClosed\":false");
        (await trapAdmin.GetStringAsync("/api/v1/disciplinary-history/import/status")).Should().Contain("\"isClosed\":true");
    }

    [Fact]
    public async Task InfractionTypes_ArePerCompany_WithTheSameCodeAllowedInEach()
    {
        await TrapAdminAsync();
        using var trapAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test");
        using var usgaAdmin = await AccessTestHelpers.FixtureAdminAsync(Factory);
        var body = new { code = "MESMO", name = "Mesmo código", defaultSeverity = 2 };
        (await usgaAdmin.PostAsJsonAsync("/api/v1/infraction-types", body)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await trapAdmin.PostAsJsonAsync("/api/v1/infraction-types", body)).StatusCode.Should().Be(HttpStatusCode.Created, "código único por empresa");
        (await trapAdmin.PostAsJsonAsync("/api/v1/infraction-types", body)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---------------- Regra 11: avisos e e-mails por empresa ----------------

    [Fact]
    public async Task RecordNotices_GoOnlyToApproversOfThatCompany_AndTheEmailCitesTheCompany_WithoutEmployeeData()
    {
        await AccessTestHelpers.CreateUserAsync(Factory, "encarregado.usga@sigh.test", SystemRoles.HrApprover);
        await AccessTestHelpers.CreateUserAsync(Factory, "encarregado.trap@sigh.test", [new RoleGrant(SystemRoles.HrApprover, CompanyId: Trap)]);
        var (_, trapRecord) = await RecordsInBothCompaniesAsync();

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var trapNumber = await db.DisciplinaryCases.Where(c => c.Id == trapRecord).Select(c => c.CaseNumber).SingleAsync();
        var emails = await db.EmailOutbox.AsNoTracking().Where(m => m.TextBody.Contains(trapNumber)).ToListAsync();

        emails.Select(m => m.To).Should().Contain("encarregado.trap@sigh.test").And.NotContain("encarregado.usga@sigh.test");
        emails.Should().OnlyContain(m => m.Subject.Contains("Trapiche") && m.TextBody.Contains("Empresa: Trapiche"));
        emails.Should().OnlyContain(m => !m.TextBody.Contains("JOAO DA SILVA TESTE") && !m.TextBody.Contains(TrapicheDemoSpreadsheets.Employees[0].Cpf));
        (await db.Notifications.Where(n => n.DisciplinaryCaseId == trapRecord).Select(n => n.CompanyId).Distinct().ToListAsync()).Should().Equal(Trap);
    }

    [Fact]
    public async Task AuditTrail_ShowsOnlyTheCompaniesWithAuditView()
    {
        var (usgaRecord, trapRecord) = await RecordsInBothCompaniesAsync();
        await AccessTestHelpers.CreateUserAsync(Factory, "juridico.trap@sigh.test", [new RoleGrant(SystemRoles.Legal, CompanyId: Trap)]);
        using var trapLegal = await AccessTestHelpers.LoginAsync(Factory, "juridico.trap@sigh.test");
        var trapAudit = await trapLegal.GetStringAsync("/api/v1/audit-logs?pageSize=200");
        trapAudit.Should().Contain(trapRecord.ToString()).And.NotContain(usgaRecord.ToString());

        // O Administrador da Trapiche só tem a auditoria de acesso: nenhuma medida, de empresa nenhuma.
        using var trapAdmin = await AccessTestHelpers.LoginAsync(Factory, "admin.trap@sigh.test");
        (await trapAdmin.GetAsync("/api/v1/audit-logs?pageSize=200")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await trapAdmin.GetStringAsync("/api/v1/audit-logs/access?pageSize=200")).Should().NotContain(trapRecord.ToString()).And.NotContain(usgaRecord.ToString());

        await AccessTestHelpers.CreateUserAsync(Factory, "juridico.duas@sigh.test",
            [new RoleGrant(SystemRoles.Legal, CompanyId: Usga), new RoleGrant(SystemRoles.Legal, CompanyId: Trap)]);
        using var legal = await AccessTestHelpers.LoginAsync(Factory, "juridico.duas@sigh.test", ICompanyContext.AllCompanies);
        var both = await legal.GetStringAsync("/api/v1/audit-logs?pageSize=200");
        both.Should().Contain(trapRecord.ToString()).And.Contain(usgaRecord.ToString());
    }
}
