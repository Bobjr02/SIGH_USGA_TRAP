using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Models;
using SIGH.Application.Disciplinary.History;
using SIGH.Application.Employees.Import;
using SIGH.Application.Notifications.DTOs;
using SIGH.Application.Users.GetUsers;
using SIGH.Demo;
using SIGH.Domain.Notifications.Entities;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Context;
using SIGH.Persistence.Seeds;
using Xunit;

namespace SIGH.IntegrationTests.Scope;

/// <summary>
/// Nenhum ponto escolhe "a primeira empresa" por conta própria: empresa não autorizada é 404 (nunca 401, que a tela
/// entende como sessão expirada); no modo "Todas" as notificações são de TODAS as empresas da requisição; o estado da
/// importação de histórico exige uma empresa; requisição sem cabeçalho só vale com uma única empresa; e as listas de
/// usuários e de lotes dizem a empresa de cada item.
/// </summary>
public class ImplicitCompanyTests : IntegrationTestBase
{
    private static readonly Guid Usga = DatabaseSeeder.DefaultCompanyId;
    private static readonly Guid Trap = DatabaseSeeder.TrapicheCompanyId;
    private const string TwoCompaniesRole = "IntegrationTests.TwoCompanies";
    private const string Email = "duas.empresas@sigh.test";

    public ImplicitCompanyTests(CustomWebApplicationFactory factory) : base(factory) { }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;

    /// <summary>Usuário com o mesmo perfil (avisos, usuários, importações) na USGA e na Trapiche.</summary>
    private async Task<TestUser> TwoCompaniesUserAsync()
    {
        await AccessTestHelpers.CreateRoleAsync(Factory, TwoCompaniesRole,
            PermissionCodes.Notifications.View, PermissionCodes.Notifications.Read, PermissionCodes.Notifications.Manage,
            PermissionCodes.Users.View, PermissionCodes.Employees.View, PermissionCodes.Employees.Import,
            PermissionCodes.DisciplinaryHistory.Import);
        return await AccessTestHelpers.CreateUserAsync(Factory, Email,
            [new RoleGrant(TwoCompaniesRole, CompanyId: Usga), new RoleGrant(TwoCompaniesRole, CompanyId: Trap)]);
    }

    private async Task<(Guid Usga, Guid Trap)> NotificationsAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var usga = Notification.Create(Usga, userId, "Aviso da USGA", "Sem medida vinculada.");
        var trap = Notification.Create(Trap, userId, "Aviso da Trapiche", "Sem medida vinculada.");
        db.Notifications.AddRange(usga, trap);
        await db.SaveChangesAsync();
        return (usga.Id, trap.Id);
    }

    private async Task<string> CompanyNameAsync(Guid id)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SighDbContext>().Companies.Where(c => c.Id == id).Select(c => c.Name).SingleAsync();
    }

    // ---- 1. Empresa não autorizada = 404, nunca 401 ----

    [Fact]
    public async Task Notifications_OfACompanyWithoutAccess_Are404_AndTheSessionStaysValid()
    {
        await AccessTestHelpers.CreateUserAsync(Factory, "so.usga@sigh.test", [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: Usga)]);
        using var client = await AccessTestHelpers.LoginAsync(Factory, "so.usga@sigh.test");

        foreach (var url in new[]
                 {
                     $"/api/v1/notifications?companyId={Trap}",
                     $"/api/v1/notifications/unread?companyId={Trap}",
                     $"/api/v1/notifications/unread/count?companyId={Trap}",
                 })
        {
            var response = await client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, url);
            (await CodeAsync(response)).Should().Be(CompanyNotAccessibleException.ErrorCode, url);
        }
        var markAll = await client.PutAsync($"/api/v1/notifications/read-all?companyId={Trap}", null);
        markAll.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(markAll)).Should().Be(CompanyNotAccessibleException.ErrorCode);

        // Uma empresa que nem existe também é 404 (e não 401).
        (await client.GetAsync($"/api/v1/notifications?companyId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A sessão continua válida: o mesmo token segue funcionando.
        (await client.GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/notifications")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/v1/notifications?companyId={Usga}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- 2a. Notificações no modo "Todas" ----

    [Fact]
    public async Task Notifications_InAllCompaniesMode_ListCountAndMarkAllCompanies_WithTheCompanyInEachItem()
    {
        var user = await TwoCompaniesUserAsync();
        var (usgaId, trapId) = await NotificationsAsync(user.Id);
        var usgaName = await CompanyNameAsync(Usga);
        var trapName = await CompanyNameAsync(Trap);
        using var all = await AccessTestHelpers.LoginAsync(Factory, Email, ICompanyContext.AllCompanies);

        var page = (await all.GetFromJsonAsync<Result<PagedResult<NotificationDto>>>("/api/v1/notifications?pageSize=50"))!.Data!;
        page.Items.Select(n => (n.Id, n.CompanyId, n.CompanyName)).Should().BeEquivalentTo([(usgaId, Usga, usgaName), (trapId, Trap, trapName)]);
        (await all.GetFromJsonAsync<Result<List<NotificationDto>>>("/api/v1/notifications/unread"))!.Data!.Select(n => n.Id).Should().BeEquivalentTo([usgaId, trapId]);
        (await all.GetFromJsonAsync<Result<int>>("/api/v1/notifications/unread/count"))!.Data.Should().Be(2);

        // Marcar um aviso de cada empresa como lido funciona no modo "Todas" (é pessoal).
        (await all.PutAsync($"/api/v1/notifications/{trapId}/read", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await all.GetFromJsonAsync<Result<int>>("/api/v1/notifications/unread/count"))!.Data.Should().Be(1);
        (await all.PutAsync("/api/v1/notifications/read-all", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await all.GetFromJsonAsync<Result<int>>("/api/v1/notifications/unread/count"))!.Data.Should().Be(0);
    }

    [Fact]
    public async Task Notifications_WithASpecificCompany_AreOnlyThatCompanys()
    {
        var user = await TwoCompaniesUserAsync();
        var (usgaId, trapId) = await NotificationsAsync(user.Id);
        using var usga = await AccessTestHelpers.LoginAsync(Factory, Email, CompanyDefaults.UsgaCode);

        (await usga.GetFromJsonAsync<Result<PagedResult<NotificationDto>>>("/api/v1/notifications"))!.Data!.Items.Select(n => n.Id).Should().Equal(usgaId);
        (await usga.GetFromJsonAsync<Result<int>>("/api/v1/notifications/unread/count"))!.Data.Should().Be(1);
        // A empresa ativa é a USGA: pedir a Trapiche pelo companyId é 404, e o aviso da Trapiche "não existe".
        (await usga.GetAsync($"/api/v1/notifications?companyId={Trap}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await usga.PutAsync($"/api/v1/notifications/{trapId}/read", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Marcar todos na USGA não toca no aviso da Trapiche.
        (await usga.PutAsync("/api/v1/notifications/read-all", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        (await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == trapId)).IsRead.Should().BeFalse();
        (await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == usgaId)).IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessDeadlines_RunsOnlyInTheActiveCompany_AnotherCompanyIs404_AndAllCompaniesIsRefused()
    {
        await TwoCompaniesUserAsync();
        using var usga = await AccessTestHelpers.LoginAsync(Factory, Email, CompanyDefaults.UsgaCode);
        using var all = await AccessTestHelpers.LoginAsync(Factory, Email, ICompanyContext.AllCompanies);

        (await usga.PostAsync("/api/v1/notifications/process-deadlines", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await usga.PostAsync($"/api/v1/notifications/process-deadlines?companyId={Usga}", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var other = await usga.PostAsync($"/api/v1/notifications/process-deadlines?companyId={Trap}", null);
        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeAsync(other)).Should().Be(CompanyNotAccessibleException.ErrorCode);
        var allMode = await all.PostAsync("/api/v1/notifications/process-deadlines", null);
        allMode.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeAsync(allMode)).Should().Be(CompanyRequiredException.ErrorCode);
    }

    // ---- 2b. Estado da importação de histórico ----

    [Fact]
    public async Task HistoryImportStatus_InAllCompaniesMode_IsCompanyRequired_AndWorksWithACompany()
    {
        await TwoCompaniesUserAsync();
        using var all = await AccessTestHelpers.LoginAsync(Factory, Email, ICompanyContext.AllCompanies);
        var response = await all.GetAsync("/api/v1/disciplinary-history/import/status");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeAsync(response)).Should().Be(CompanyRequiredException.ErrorCode);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Selecione uma empresa");

        using var trap = await AccessTestHelpers.LoginAsync(Factory, Email, CompanyDefaults.TrapicheCode);
        (await trap.GetAsync("/api/v1/disciplinary-history/import/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- 2c. Relatórios antigos removidos ----

    [Theory]
    [InlineData("/api/v1/disciplinary-reports/dashboard")]
    [InlineData("/api/v1/disciplinary-reports/cases")]
    [InlineData("/api/v1/disciplinary-reports/cases/export")]
    [InlineData("/api/v1/disciplinary-reports/measures")]
    public async Task LegacyReportEndpoints_NoLongerExist(string url)
    {
        using var admin = await AccessTestHelpers.FixtureAdminAsync(Factory);
        (await admin.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Sem cabeçalho: só com uma empresa ----

    [Fact]
    public async Task WithoutTheCompanyHeader_AUserOfTwoCompaniesMustChoose_AUserOfOneCompanyUsesIt()
    {
        await TwoCompaniesUserAsync();
        using var noHeader = await AccessTestHelpers.LoginAsync(Factory, Email);
        var response = await noHeader.GetAsync("/api/v1/employees");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeAsync(response)).Should().Be(CompanyRequiredException.ErrorCode);
        (await noHeader.GetAsync("/api/v1/notifications")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // /auth/me não depende de empresa: a tela usa para escolher a empresa.
        (await noHeader.GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var oneCompany = await AccessTestHelpers.FixtureAdminAsync(Factory);
        (await oneCompany.GetAsync("/api/v1/employees")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- 3. Coluna Empresa nas listas de usuários e de lotes ----

    [Fact]
    public async Task UsersList_InAllCompaniesMode_SaysTheCompaniesOfEachUser()
    {
        await TwoCompaniesUserAsync();
        await AccessTestHelpers.CreateUserAsync(Factory, "so.trap@sigh.test", [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: Trap)]);
        var usgaName = await CompanyNameAsync(Usga);
        var trapName = await CompanyNameAsync(Trap);
        using var all = await AccessTestHelpers.LoginAsync(Factory, Email, ICompanyContext.AllCompanies);

        var users = (await all.GetFromJsonAsync<Result<PagedList<UserSummaryDto>>>("/api/v1/users?pageSize=100"))!.Data!.Items;
        users.Single(u => u.Email == "so.trap@sigh.test").Companies.Should().Equal(trapName);
        users.Single(u => u.Email == Email).Companies.Should().BeEquivalentTo([usgaName, trapName]);
        users.Single(u => u.Email == "admin@sigh.com").Companies.Should().Equal(usgaName);

        using var trap = await AccessTestHelpers.LoginAsync(Factory, Email, CompanyDefaults.TrapicheCode);
        var trapUsers = (await trap.GetFromJsonAsync<Result<PagedList<UserSummaryDto>>>("/api/v1/users?pageSize=100"))!.Data!.Items;
        trapUsers.Should().NotContain(u => u.Email == "admin@sigh.com");
        trapUsers.Single(u => u.Email == Email).Companies.Should().Equal([trapName], "só as empresas da requisição");
    }

    [Fact]
    public async Task ImportBatchLists_InAllCompaniesMode_SayTheCompanyOfEachBatch()
    {
        await TwoCompaniesUserAsync();
        var usgaName = await CompanyNameAsync(Usga);
        var trapName = await CompanyNameAsync(Trap);
        using var usga = await AccessTestHelpers.LoginAsync(Factory, Email, CompanyDefaults.UsgaCode);
        using var trap = await AccessTestHelpers.LoginAsync(Factory, Email, CompanyDefaults.TrapicheCode);
        using var all = await AccessTestHelpers.LoginAsync(Factory, Email, ICompanyContext.AllCompanies);

        // Uma pré-visualização em cada empresa cria um lote em cada uma (nada é aplicado).
        foreach (var client in new[] { usga, trap })
        {
            (await CompanyScenario.PostFileAsync(client, "/api/v1/employees/import/preview", TrapicheDemoSpreadsheets.EmployeesXlsx())).EnsureSuccessStatusCode();
            (await CompanyScenario.PostFileAsync(client, "/api/v1/disciplinary-history/import/preview", TrapicheDemoSpreadsheets.HistoryXlsx())).StatusCode
                .Should().NotBe(HttpStatusCode.InternalServerError);
        }

        var employeeBatches = (await all.GetFromJsonAsync<Result<PagedResult<EmployeeImportBatchDto>>>("/api/v1/employees/import/batches"))!.Data!.Items;
        employeeBatches.Select(b => b.CompanyName).Should().BeEquivalentTo([usgaName, trapName]);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            var historyCompanies = await db.DisciplinaryHistoryImportBatches.Select(b => b.CompanyId).Distinct().ToListAsync();
            var historyBatches = (await all.GetFromJsonAsync<Result<PagedResult<HistoryImportBatchDto>>>("/api/v1/disciplinary-history/import/batches"))!.Data!.Items;
            historyBatches.Should().HaveCount(await db.DisciplinaryHistoryImportBatches.CountAsync()).And.OnlyContain(b => b.CompanyName == usgaName || b.CompanyName == trapName);
            historyBatches.Select(b => b.CompanyName).Distinct().Should().HaveCount(historyCompanies.Count);
        }

        (await trap.GetFromJsonAsync<Result<PagedResult<EmployeeImportBatchDto>>>("/api/v1/employees/import/batches"))!.Data!.Items
            .Should().OnlyContain(b => b.CompanyName == trapName);
    }
}
