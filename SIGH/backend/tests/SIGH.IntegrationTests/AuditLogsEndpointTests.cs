using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Application.Audit;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Models;
using SIGH.Application.Disciplinary.InfractionTypes.CreateInfractionType;
using SIGH.Application.Security;
using SIGH.Domain.Disciplinary.Enums;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Auditing;
using SIGH.Persistence.Context;
using SIGH.Persistence.Seeds;
using Xunit;

namespace SIGH.IntegrationTests;

public class AuditLogsEndpointTests : IntegrationTestBase
{
    public AuditLogsEndpointTests(CustomWebApplicationFactory factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task SeededAdministratorRole_HasOnlyTheAccessAudit_AndLegalTheFullTrail()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();

        var admin = await db.RolePermissions
            .Where(rp => rp.Role.Name == DatabaseSeeder.AdministratorRoleName)
            .Select(rp => rp.Permission.Code)
            .ToListAsync();
        var legal = await db.RolePermissions
            .Where(rp => rp.Role.Name == SystemRoles.Legal)
            .Select(rp => rp.Permission.Code)
            .ToListAsync();

        admin.Should().Contain(PermissionCodes.Audit.ViewAccess).And.NotContain(PermissionCodes.Audit.View);
        legal.Should().Contain(PermissionCodes.Audit.View).And.NotContain(PermissionCodes.Audit.ViewAccess);
    }

    /// <summary>
    /// A auditoria de ACESSO traz perfis atribuídos e eventos de segurança, e nunca medidas, funcionários, anexos ou
    /// importações — mesmo que eles estejam na trilha completa.
    /// </summary>
    [Fact]
    public async Task AccessAudit_HasProfilesAndSecurityEvents_ButNoEmployeeOrMeasureEntries()
    {
        var admin = await AccessTestHelpers.CreateUserAsync(Factory, "audit.acesso.admin@sigh.test", SystemRoles.Administrator);
        await AccessTestHelpers.CreateUserAsync(Factory, "audit.acesso.alvo@sigh.test", SystemRoles.HrRegistrar);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            foreach (var entity in new[] { "Employee", "DisciplinaryCase", "DisciplinaryEvidence", "DisciplinaryMeasure", "HistoryImportState" })
                db.AuditLogs.Add(new SIGH.Domain.Entities.AuditLog { CompanyId = DatabaseSeeder.DefaultCompanyId, Action = "Created", Entity = entity, EntityId = Guid.NewGuid().ToString() });
            db.AuditLogs.Add(new SIGH.Domain.Entities.AuditLog
            {
                CompanyId = DatabaseSeeder.DefaultCompanyId, Action = SIGH.Application.Security.SecurityAuditActions.EvidenceDownloaded, Entity = "User", EntityId = Guid.NewGuid().ToString()
            });
            await db.SaveChangesAsync();
        }
        using var client = await AccessTestHelpers.LoginAsync(Factory, admin.Email, CompanyDefaults.UsgaCode);

        var body = JsonDocument.Parse(await client.GetStringAsync("/api/v1/audit-logs/access?pageSize=200")).RootElement.GetProperty("data");
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Select(i => i.GetProperty("entity").GetString()).Should().OnlyContain(e => SIGH.Application.Audit.AccessAuditPolicy.Entities.Contains(e!));
        items.Should().NotContain(i => i.GetProperty("action").GetString() == SIGH.Application.Security.SecurityAuditActions.EvidenceDownloaded);
        items.Should().Contain(i => i.GetProperty("entity").GetString() == "UserRole");
        items.Should().Contain(i => i.GetProperty("action").GetString() == SIGH.Application.Security.SecurityAuditActions.LoginSucceeded,
            "o login do próprio administrador (evento sem empresa, de uma pessoa da USGA)");

        (await client.GetAsync("/api/v1/audit-logs?pageSize=200")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LoginEvents_AreRecordedWithoutThePassword()
    {
        const string wrongPassword = "Senha-Errada-Que-Nao-Pode-Vazar-123";
        var client = Factory.CreateClient();

        (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "admin@sigh.com", password = wrongPassword }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "ninguem@sigh.com", password = wrongPassword }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AuthTestHelpers.LoginAsync(client, "admin@sigh.com", CustomWebApplicationFactory.DefaultPassword);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var logs = await db.AuditLogs.Where(a => a.Action.StartsWith("Security.Login")).ToListAsync();

        logs.Should().Contain(a => a.Action == SecurityAuditActions.LoginFailed && a.UserId == CustomWebApplicationFactory.AdminUserId);
        logs.Should().Contain(a => a.Action == SecurityAuditActions.LoginFailed && a.UserId == null && a.NewValues!.Contains("UnknownUser"));
        logs.Should().Contain(a => a.Action == SecurityAuditActions.LoginSucceeded && a.UserId == CustomWebApplicationFactory.AdminUserId);

        var everything = string.Join("\n", (await db.AuditLogs.ToListAsync()).Select(a => a.OldValues + a.NewValues));
        everything.Should().NotContain(wrongPassword).And.NotContain(CustomWebApplicationFactory.DefaultPassword);
    }

    [Fact]
    public async Task RepeatedFailures_RecordAccountLocked()
    {
        var client = Factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "approver@sigh.com", password = "errada" });

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        (await db.AuditLogs.AnyAsync(a => a.Action == SecurityAuditActions.AccountLocked && a.UserId == CustomWebApplicationFactory.ApproverUserId))
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetAuditLogs_WithoutAuditView_Returns403()
    {
        using var client = await CreateClientAsync("unprivileged@sigh.com");

        var response = await client.GetAsync("/api/v1/audit-logs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreatingAnEntity_IsAuditedWithUserIpAndCorrelationId_AndCanBeFiltered()
    {
        using var admin = await CreateClientAsync("admin@sigh.com");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/infraction-types")
        {
            Content = JsonContent.Create(new CreateInfractionTypeRequest(
                Code: $"AUD-{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
                Name: "Tipo auditado",
                DefaultSeverity: InfractionSeverity.Moderate,
                Description: "Criado no teste de auditoria"))
        };
        request.Headers.Add("X-Correlation-ID", "corr-audit-123");
        var created = await admin.SendAsync(request);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<Result<CreateInfractionTypeResponse>>())!.Data!.Id;

        var response = await admin.GetAsync($"/api/v1/audit-logs?entityName=InfractionType&entityId={id}&action=Created");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<Result<PagedResult<AuditLogDto>>>())!.Data!;

        var log = page.Items.Should().ContainSingle().Subject;
        log.Action.Should().Be(AuditActions.Created);
        log.UserId.Should().Be(CustomWebApplicationFactory.AdminUserId);
        log.UserName.Should().Be("Administrador do Sistema");
        log.CorrelationId.Should().Be("corr-audit-123");
        log.IpAddress.Should().NotBeNullOrEmpty();
        log.NewValues.Should().Contain("Tipo auditado");

        // Filtro por período fora do intervalo não traz nada.
        var empty = await admin.GetFromJsonAsync<Result<PagedResult<AuditLogDto>>>(
            $"/api/v1/audit-logs?entityId={id}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}");
        empty!.Data!.TotalCount.Should().Be(0);
    }

    private async Task<HttpClient> CreateClientAsync(string email)
    {
        var client = Factory.CreateClient();
        var (login, _) = await AuthTestHelpers.LoginAsync(client, email, CustomWebApplicationFactory.DefaultPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }
}
