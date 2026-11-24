using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Api.Cli;
using SIGH.Application.Authentication.ResetPassword;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Email;
using SIGH.Application.Security;
using SIGH.Application.Users.Bootstrap;
using SIGH.Domain.Entities;
using SIGH.Domain.Enums;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Context;
using SIGH.Persistence.Seeds;
using Xunit;

namespace SIGH.IntegrationTests;

/// <summary>
/// O administrador nunca conhece a senha de ninguém: criação sem senha (link de uso único, 48 h, guardado só como
/// hash, reenviável), domínios de e-mail aceitos, primeiro administrador de cada empresa na publicação
/// (Seed:CompanyAdmins), recuperação pelo TI na linha de comando e o alerta de administrador único.
/// </summary>
public class AdministratorAccountsTests : IntegrationTestBase
{
    private const string NewPassword = "Definida@Pela2026Pessoa";

    public AdministratorAccountsTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    // ---------- 2. criação sem senha ----------

    [Fact]
    public async Task CreatingAUser_HasNoPasswordField_SendsASingleUse48HourLink_StoredOnlyAsHash()
    {
        using var admin = await AccessTestHelpers.FixtureAdminAsync(Factory);
        var roleId = await RoleIdAsync(SystemRoles.HrRegistrar);

        // Um "initialPassword" enviado por um cliente antigo é ignorado: a conta não aceita essa senha.
        var created = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            fullName = "PESSOA NOVA TESTE", email = "pessoa.nova@usga.com.br", cpf = "71428793860",
            initialPassword = "Conhecida@Pelo2026Admin", roleIds = new[] { roleId }
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var userId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var login = await Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = "pessoa.nova@usga.com.br", password = "Conhecida@Pelo2026Admin" });
        login.StatusCode.Should().NotBe(HttpStatusCode.OK, "a senha informada pelo administrador não vale");

        var message = (await OutboxAsync()).Should().ContainSingle(m => m.To == "pessoa.nova@usga.com.br").Subject;
        message.Template.Should().Be(EmailTemplates.AccountCreatedTemplate);
        message.TextBody.Should().Contain("48 horas").And.NotContain("Conhecida@Pelo2026Admin");
        var raw = TokenFrom(message.TextBody);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            var token = await db.PasswordResetTokens.AsNoTracking().SingleAsync(t => t.UserId == userId && t.UsedAt == null);
            token.Purpose.Should().Be(PasswordTokenPurpose.AccountSetup);
            token.TokenHash.Should().NotBe(raw).And.NotContain(raw, "o token é guardado só como hash");
            (token.ExpiresAt - token.CreatedAt).Should().Be(TimeSpan.FromHours(48));
            (await db.PasswordResetTokens.AnyAsync(t => t.TokenHash == raw)).Should().BeFalse();
        }

        var anonymous = Factory.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(raw, NewPassword))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(raw, NewPassword + "x")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "o link é de uso único");

        var (firstLogin, _) = await AuthTestHelpers.LoginAsync(Factory.CreateClient(), "pessoa.nova@usga.com.br", NewPassword);
        firstLogin.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public async Task TheSetupLink_ExpiresAfter48Hours()
    {
        using var admin = await AccessTestHelpers.FixtureAdminAsync(Factory);
        await CreateViaApiAsync(admin, "expira@usga.com.br", "52998224725");
        var raw = TokenFrom((await OutboxAsync()).Single(m => m.To == "expira@usga.com.br").TextBody);

        Factory.Clock.Advance(TimeSpan.FromHours(48) + TimeSpan.FromMinutes(1));

        (await Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(raw, NewPassword)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("pessoa@gmail.com")]
    [InlineData("pessoa@usga.com.br.outro.com")]
    [InlineData("pessoa@filial.usga.com.br")]
    public async Task NewAccounts_OnlyAcceptTheAllowedEmailDomains(string email)
    {
        using var admin = await AccessTestHelpers.FixtureAdminAsync(Factory);
        var response = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            fullName = "FORA DO DOMINIO TESTE", email, cpf = "39053344705", roleIds = new[] { await RoleIdAsync(SystemRoles.HrRegistrar) }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString().Should().Contain("@usga.com.br");
        (await OutboxAsync()).Should().NotContain(m => m.To == email);
    }

    [Fact]
    public async Task TheSetupLink_CanBeResent_AndOnlyTheLastOneWorks()
    {
        using var admin = await AccessTestHelpers.FixtureAdminAsync(Factory);
        var userId = await CreateViaApiAsync(admin, "reenvio@usga.com.br", "11144477735");
        var first = TokenFrom((await OutboxAsync()).Single(m => m.To == "reenvio@usga.com.br").TextBody);

        var resent = await admin.PostAsync($"/api/v1/users/{userId}/setup-link", null);
        resent.StatusCode.Should().Be(HttpStatusCode.OK, await resent.Content.ReadAsStringAsync());

        var messages = (await OutboxAsync()).Where(m => m.To == "reenvio@usga.com.br").ToList();
        messages.Should().HaveCount(2);
        var second = messages.Select(m => TokenFrom(m.TextBody)).Single(t => t != first);

        var anonymous = Factory.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(first, NewPassword)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "o reenvio invalida o link anterior");
        (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(second, NewPassword)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PostAsync($"/api/v1/users/{userId}/setup-link", null))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "quem já definiu a senha usa \"Esqueci minha senha\"");

        (await AuditAsync(SecurityAuditActions.AccountSetupLinkSent)).Should().Contain(a => a.EntityId == userId.ToString() && Detail(a, "reason")!.StartsWith("reenvio"));
    }

    [Fact]
    public async Task ResendingTheLink_OfSomeoneFromAnotherCompany_IsNotFound()
    {
        var trapUser = await AccessTestHelpers.CreateUserAsync(Factory, "so.trap.reenvio@sigh.test",
            [new RoleGrant(SystemRoles.HrRegistrar, CompanyId: DatabaseSeeder.TrapicheCompanyId)]);
        using var admin = await AccessTestHelpers.FixtureAdminAsync(Factory);

        (await admin.PostAsync($"/api/v1/users/{trapUser.Id}/setup-link", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- 3. primeiro administrador de cada empresa ----------

    [Fact]
    public async Task TheUsgaSeedAdmin_UsesTheSameMechanism_NoKnownPassword_LinkAndAudit()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var admin = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .SingleAsync(u => u.Email == CustomWebApplicationFactory.SeedAdminEmail);

        admin.Status.Should().Be(UserStatus.PendingFirstAccess);
        admin.UserRoles.Should().ContainSingle().Which.Should().Match<UserRole>(ur =>
            ur.Role.Name == SystemRoles.Administrator && ur.CompanyId == DatabaseSeeder.DefaultCompanyId);
        (await Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
                new { email = CustomWebApplicationFactory.SeedAdminEmail, password = CustomWebApplicationFactory.SeedAdminPassword }))
            .StatusCode.Should().NotBe(HttpStatusCode.OK, "Seed:AdminPassword não existe mais: ninguém conhece a senha");

        var mail = (await OutboxAsync()).Should().ContainSingle(m => m.To == CustomWebApplicationFactory.SeedAdminEmail).Subject;
        mail.Template.Should().Be(EmailTemplates.AdministratorAccessTemplate);
        TokenFrom(mail.TextBody).Should().NotBeNullOrEmpty();

        var audit = (await AuditAsync(SecurityAuditActions.CompanyAdminProvisioned)).Should().ContainSingle().Subject;
        audit.CompanyId.Should().Be(DatabaseSeeder.DefaultCompanyId);
        Detail(audit, "origin").Should().Be(CompanyAdminProvisioner.OriginPublication);
    }

    [Fact]
    public async Task RunningTheSeedAgain_WhenTheCompanyHasAnAdministrator_DoesNothing_AndKeepsTheOldPassword()
    {
        // Banco antigo: o administrador já existe e entra com a senha de antes.
        await Factory.GiveSeedAdminLegacyPasswordAsync();
        var before = (await OutboxAsync()).Count;

        await using (var scope = Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();

        (await OutboxAsync()).Should().HaveCount(before, "empresa que já tem administrador não muda");
        (await AuditAsync(SecurityAuditActions.CompanyAdminProvisioned)).Should().HaveCount(1);
        await AuthTestHelpers.LoginAsync(Factory.CreateClient(), CustomWebApplicationFactory.SeedAdminEmail, CustomWebApplicationFactory.SeedAdminPassword);
    }

    [Fact]
    public async Task CompanyAdmins_CreatesTheTrapicheAdmin_OnlyWithTheAdministratorProfileThere_AndOnlyOnce()
    {
        var seeds = new[] { new CompanyAdminSeed(CompanyDefaults.TrapicheCode, "primeiro.trap@usga.com.br", "PRIMEIRO ADMIN TRAP TESTE") };

        var first = await ProvisionAsync(seeds);
        first.Should().ContainSingle(r => r.Granted && r.AccountCreated);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<SighDbContext>().Users.AsNoTracking()
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).SingleAsync(u => u.Email == "primeiro.trap@usga.com.br");
            user.FullName.Should().Be("PRIMEIRO ADMIN TRAP TESTE");
            user.CreatedInCompanyId.Should().Be(DatabaseSeeder.TrapicheCompanyId);
            user.UserRoles.Select(ur => (ur.Role.Name, ur.CompanyId)).Should().Equal((SystemRoles.Administrator, DatabaseSeeder.TrapicheCompanyId));
        }
        var mail = (await OutboxAsync()).Should().ContainSingle(m => m.To == "primeiro.trap@usga.com.br").Subject;
        mail.Subject.Should().Contain("Trapiche");
        var audit = (await AuditAsync(SecurityAuditActions.CompanyAdminProvisioned)).Single(a => a.CompanyId == DatabaseSeeder.TrapicheCompanyId);
        Detail(audit, "origin").Should().Be(CompanyAdminProvisioner.OriginPublication);
        Detail(audit, "company").Should().Be("TRAP");

        // Segunda publicação: a Trapiche já tem administrador vigente (mesmo sem ter definido a senha).
        var second = await ProvisionAsync(seeds);
        second.Should().ContainSingle(r => !r.Granted);
        (await OutboxAsync()).Where(m => m.To == "primeiro.trap@usga.com.br").Should().HaveCount(1);

        // Um link funciona como sempre: a pessoa define a senha e entra só na Trapiche.
        (await Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(TokenFrom(mail.TextBody), NewPassword)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var (login, _) = await AuthTestHelpers.LoginAsync(Factory.CreateClient(), "primeiro.trap@usga.com.br", NewPassword);
        login.User.Roles.Should().Equal(SystemRoles.Administrator);
    }

    [Fact]
    public async Task CompanyAdmins_ForACompanyThatAlreadyHasAnAdministrator_ChangesNothing()
    {
        await AccessTestHelpers.CreateUserAsync(Factory, "ja.admin.trap@sigh.test", [new RoleGrant(SystemRoles.Administrator, CompanyId: DatabaseSeeder.TrapicheCompanyId)]);

        var results = await ProvisionAsync([new CompanyAdminSeed(CompanyDefaults.TrapicheCode, "outro.trap@usga.com.br")]);

        results.Should().ContainSingle(r => !r.Granted);
        await using var scope = Factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SighDbContext>().Users.AnyAsync(u => u.Email == "outro.trap@usga.com.br")).Should().BeFalse();
    }

    [Fact]
    public async Task CompanyAdmins_WithAnExistingAccount_OnlyGrantsTheProfile_WithoutASecondAccount()
    {
        var existing = await AccessTestHelpers.CreateUserAsync(Factory, "conta.usga@sigh.test", SystemRoles.HrRegistrar);

        var results = await ProvisionAsync([new CompanyAdminSeed(CompanyDefaults.TrapicheCode, "conta.usga@sigh.test")]);

        results.Should().ContainSingle(r => r.Granted && !r.AccountCreated);
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        (await db.Users.CountAsync(u => u.Email == "conta.usga@sigh.test")).Should().Be(1);
        (await db.UserRoles.Where(ur => ur.UserId == existing.Id).Select(ur => new { ur.Role.Name, ur.CompanyId }).ToListAsync())
            .Should().BeEquivalentTo([
                new { Name = SystemRoles.HrRegistrar, CompanyId = DatabaseSeeder.DefaultCompanyId },
                new { Name = SystemRoles.Administrator, CompanyId = DatabaseSeeder.TrapicheCompanyId },
            ]);
    }

    [Fact]
    public async Task CompanyAdmins_OutsideTheAllowedDomains_IsRefused_WithoutBreakingTheStartup()
    {
        var results = await ProvisionAsync([new CompanyAdminSeed(CompanyDefaults.TrapicheCode, "admin@gmail.com")]);

        results.Should().ContainSingle(r => !r.Granted);
        (await OutboxAsync()).Should().NotContain(m => m.To == "admin@gmail.com");
    }

    // ---------- 4. recuperação pelo TI (linha de comando) ----------

    [Fact]
    public async Task AdminRecover_GrantsAdministratorInThatCompany_SendsTheLink_AndAuditsTheCommandLineOrigin()
    {
        var output = new StringWriter();

        var exit = await AdminRecoveryCommand.RunAsync(Factory.Services,
            ["admin-recover", "--company", "TRAP", "--email", "ti.recupera@usga.com.br", "--name", "PESSOA DO TI TESTE"], output);

        exit.Should().Be(0, output.ToString());
        output.ToString().Should().Contain("TRAP").And.Contain("link");
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<SighDbContext>().Users.AsNoTracking()
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).SingleAsync(u => u.Email == "ti.recupera@usga.com.br");
            user.UserRoles.Select(ur => (ur.Role.Name, ur.CompanyId)).Should().Equal((SystemRoles.Administrator, DatabaseSeeder.TrapicheCompanyId));
        }
        (await OutboxAsync()).Should().ContainSingle(m => m.To == "ti.recupera@usga.com.br" && m.Template == EmailTemplates.AdministratorAccessTemplate);
        var audit = (await AuditAsync(SecurityAuditActions.AdministratorRecovered)).Should().ContainSingle().Subject;
        audit.CompanyId.Should().Be(DatabaseSeeder.TrapicheCompanyId);
        audit.UserId.Should().BeNull("não há usuário do sistema por trás do comando");
        Detail(audit, "origin").Should().Be("linha de comando");
    }

    [Fact]
    public async Task AdminRecover_WorksEvenIfTheCompanyAlreadyHasAnAdministrator_AndReactivatesAnInactiveAccount()
    {
        var inactive = await AccessTestHelpers.CreateUserAsync(Factory, "inativo@sigh.test", SystemRoles.HrApprover);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            (await db.Users.SingleAsync(u => u.Id == inactive.Id)).Status = UserStatus.Inactive;
            await db.SaveChangesAsync();
        }

        var exit = await AdminRecoveryCommand.RunAsync(Factory.Services, ["admin-recover", "--company", "USGA", "--email", "inativo@sigh.test"], new StringWriter());

        exit.Should().Be(0);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            (await db.Users.SingleAsync(u => u.Id == inactive.Id)).Status.Should().Be(UserStatus.Active);
            (await db.UserRoles.CountAsync(ur => ur.UserId == inactive.Id && ur.Role.Name == SystemRoles.Administrator && ur.CompanyId == DatabaseSeeder.DefaultCompanyId))
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task AdminRecover_InACompanyWithCurrentAdministrators_EmailsThemWhoReceivedTheProfileAndWhen()
    {
        var trapAdmin = await AccessTestHelpers.CreateUserAsync(Factory, "admin.vigente.trap@sigh.test",
            [new RoleGrant(SystemRoles.Administrator, CompanyId: DatabaseSeeder.TrapicheCompanyId)]);
        var expired = await AccessTestHelpers.CreateUserAsync(Factory, "admin.vencido.trap@sigh.test",
            [new RoleGrant(SystemRoles.Administrator, ValidUntil: DateTimeOffset.UtcNow.AddDays(-1), CompanyId: DatabaseSeeder.TrapicheCompanyId)]);
        var usgaAdmin = await AccessTestHelpers.CreateUserAsync(Factory, "admin.so.usga@sigh.test", SystemRoles.Administrator);
        var when = Factory.Clock.UtcNow;

        var exit = await AdminRecoveryCommand.RunAsync(Factory.Services,
            ["admin-recover", "--company", "TRAP", "--email", "novo.admin.aviso@usga.com.br", "--name", "NOVO ADMIN DO AVISO TESTE"], new StringWriter());

        exit.Should().Be(0);
        var outbox = await OutboxAsync();
        var notice = outbox.Should().ContainSingle(m => m.To == trapAdmin.Email && m.Template == EmailTemplates.AdministratorRecoveredNoticeTemplate).Subject;
        notice.Subject.Should().Contain("Novo administrador").And.Contain("Trapiche");
        var brasilia = TimeZoneInfo.ConvertTime(when, TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "E. South America Standard Time" : "America/Sao_Paulo"));
        notice.TextBody.Should().Contain("linha de comando")
            .And.Contain("NOVO ADMIN DO AVISO TESTE (novo.admin.aviso@usga.com.br)")
            .And.Contain($"Quando: {brasilia:dd/MM/yyyy}")
            .And.NotContain("reset-password", "o aviso não traz link de definição de senha");
        outbox.Should().NotContain(m => m.Template == EmailTemplates.AdministratorRecoveredNoticeTemplate
            && (m.To == "novo.admin.aviso@usga.com.br" || m.To == usgaAdmin.Email || m.To == expired.Email),
            "só os administradores VIGENTES da empresa (fora quem recebeu o perfil) são avisados");
        outbox.Should().ContainSingle(m => m.To == "novo.admin.aviso@usga.com.br" && m.Template == EmailTemplates.AdministratorAccessTemplate);
    }

    [Fact]
    public async Task AdminRecover_InACompanyWithoutOtherAdministrators_SendsNoNotice()
    {
        await RemoveFixtureAdministratorsAsync();
        await RemoveSeedAdministratorAsync();
        await RemoveAdministratorsAsync(DatabaseSeeder.DefaultCompanyId);

        (await AdminRecoveryCommand.RunAsync(Factory.Services, ["admin-recover", "--company", "USGA", "--email", "sozinho.usga@usga.com.br"], new StringWriter()))
            .Should().Be(0);

        (await OutboxAsync()).Should().NotContain(m => m.Template == EmailTemplates.AdministratorRecoveredNoticeTemplate);
    }

    [Fact]
    public async Task TechnicalCpf_OfAdministratorsCreatedByTheSeedOrTheCommandLine_IsMarked_Invalid_Unique_AndNeverShown()
    {
        foreach (var email in new[] { "tecnico.um@usga.com.br", "tecnico.dois@usga.com.br" })
            (await AdminRecoveryCommand.RunAsync(Factory.Services, ["admin-recover", "--company", "TRAP", "--email", email], new StringWriter())).Should().Be(0);

        List<User> technical;
        await using (var scope = Factory.Services.CreateAsyncScope())
            technical = await scope.ServiceProvider.GetRequiredService<SighDbContext>().Users.AsNoTracking()
                .Where(u => u.Email == "tecnico.um@usga.com.br" || u.Email == "tecnico.dois@usga.com.br" || u.Email == CustomWebApplicationFactory.SeedAdminEmail)
                .ToListAsync();
        technical.Should().HaveCount(3);
        technical.Select(u => u.Cpf).Should().OnlyHaveUniqueItems().And.OnlyContain(c => TechnicalCpf.IsTechnical(c));
        technical.Select(u => u.Cpf).Should().OnlyContain(c => !SIGH.Domain.Employees.Helpers.CpfValidator.IsValid(c), "não passa na validação de CPF real");

        using var admin = await AccessTestHelpers.FixtureAdminAsync(Factory);
        var seedAdmin = technical.Single(u => u.Email == CustomWebApplicationFactory.SeedAdminEmail);
        var detail = JsonDocument.Parse(await (await admin.GetAsync($"/api/v1/users/{seedAdmin.Id}")).Content.ReadAsStringAsync()).RootElement;
        detail.GetProperty("data").GetProperty("cpf").GetString().Should().BeEmpty("a tela e a API não mostram o CPF técnico");
        var list = JsonDocument.Parse(await (await admin.GetAsync("/api/v1/users?pageSize=100")).Content.ReadAsStringAsync()).RootElement
            .GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        list.Should().Contain(u => u.GetProperty("id").GetGuid() == seedAdmin.Id);
        list.Select(u => u.GetProperty("cpf").GetString()).Should().OnlyContain(c => !TechnicalCpf.IsTechnical(c), "a lista não mostra CPF técnico");
        var search = JsonDocument.Parse(await (await admin.GetAsync($"/api/v1/users?searchTerm={seedAdmin.Cpf}")).Content.ReadAsStringAsync()).RootElement;
        search.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(0, "a busca por CPF não encontra o CPF técnico");

        var create = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            fullName = "PESSOA COM CPF TECNICO TESTE", email = "cpf.tecnico@sigh.test", cpf = "00000000055",
            roleIds = new[] { await RoleIdAsync(SystemRoles.HrRegistrar) }, managementUnitIds = Array.Empty<Guid>()
        });
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a faixa técnica não é aceita no cadastro manual");
    }

    [Theory]
    [InlineData(1, "admin-recover", "--company", "TRAP")]
    [InlineData(1, "admin-recover", "--email", "x@usga.com.br")]
    [InlineData(1, "admin-recover", "--company", "TRAP", "--email")]
    [InlineData(1, "admin-recover", "--empresa", "TRAP", "--email", "x@usga.com.br")]
    [InlineData(2, "admin-recover", "--company", "XYZ", "--email", "x@usga.com.br")]
    [InlineData(2, "admin-recover", "--company", "TRAP", "--email", "x@gmail.com")]
    public async Task AdminRecover_RefusesInvalidInput(int expectedExit, params string[] args)
    {
        var output = new StringWriter();
        (await AdminRecoveryCommand.RunAsync(Factory.Services, args, output)).Should().Be(expectedExit, output.ToString());
        (await AuditAsync(SecurityAuditActions.AdministratorRecovered)).Should().BeEmpty();
    }

    [Fact]
    public void AdminRecover_HasNoHttpEndpoint()
    {
        Scope.AdministratorWithoutDataTests.AllRoutes().Select(r => r.Template)
            .Should().NotContain(t => t.Contains("recover", StringComparison.OrdinalIgnoreCase) || t.Contains("bootstrap", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 5. alerta de administrador ----------

    [Fact]
    public async Task AdminStatus_WarnsWhenTheCompanyHasASingleCurrentAdministrator()
    {
        await RemoveFixtureAdministratorsAsync();
        var only = await AccessTestHelpers.CreateUserAsync(Factory, "unico.admin@sigh.test", SystemRoles.Administrator);
        using var client = await AccessTestHelpers.LoginAsync(Factory, only.Email, CompanyDefaults.UsgaCode);

        var status = await StatusAsync(client);

        status.GetProperty("currentAdministrators").GetInt32().Should().Be(2, "o único criado aqui e o do seed, ainda sem senha");
        status.GetProperty("singleAdministrator").GetBoolean().Should().BeFalse();

        await RemoveSeedAdministratorAsync();
        status = await StatusAsync(client);
        status.GetProperty("currentAdministrators").GetInt32().Should().Be(1);
        status.GetProperty("singleAdministrator").GetBoolean().Should().BeTrue();
        status.GetProperty("endingSoon").GetBoolean().Should().BeFalse("sem prazo");
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(16, false)]
    public async Task AdminStatus_WarnsWhenTheLastAdministratorEndsInLessThan15Days(int days, bool warns)
    {
        await RemoveFixtureAdministratorsAsync();
        await RemoveSeedAdministratorAsync();
        var until = DateTimeOffset.UtcNow.AddDays(days);
        var a = await AccessTestHelpers.CreateUserAsync(Factory, "admin.prazo.a@sigh.test", [new RoleGrant(SystemRoles.Administrator, ValidUntil: until)]);
        await AccessTestHelpers.CreateUserAsync(Factory, "admin.prazo.b@sigh.test", [new RoleGrant(SystemRoles.Administrator, ValidUntil: until.AddDays(-2))]);
        using var client = await AccessTestHelpers.LoginAsync(Factory, a.Email, CompanyDefaults.UsgaCode);

        var status = await StatusAsync(client);

        status.GetProperty("currentAdministrators").GetInt32().Should().Be(2);
        status.GetProperty("singleAdministrator").GetBoolean().Should().BeFalse();
        status.GetProperty("lastValidUntil").GetDateTimeOffset().Should().BeCloseTo(until, TimeSpan.FromSeconds(1));
        status.GetProperty("endingSoon").GetBoolean().Should().Be(warns);
    }

    [Fact]
    public async Task NobodyChangesTheirOwnProfiles_StillHolds()
    {
        var me = await AccessTestHelpers.CreateUserAsync(Factory, "proprio.admin@sigh.test", SystemRoles.Administrator);
        using var client = await AccessTestHelpers.LoginAsync(Factory, me.Email, CompanyDefaults.UsgaCode);

        var response = await client.PostAsJsonAsync($"/api/v1/users/{me.Id}/roles", new { roleId = await RoleIdAsync(SystemRoles.Legal) });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString().Should().Contain("próprios perfis");
    }

    // ---------- apoio ----------

    private static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/users/admin-status");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rows = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        return rows.EnumerateArray().Single(r => r.GetProperty("companyCode").GetString() == CompanyDefaults.UsgaCode).Clone();
    }

    /// <summary>Os usuários de fixture admin@ e approver@ têm o perfil Administrador: retirados para medir o alerta.</summary>
    private async Task RemoveFixtureAdministratorsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var links = await db.UserRoles.Where(ur => ur.Role.Name == SystemRoles.Administrator
            && (ur.UserId == CustomWebApplicationFactory.AdminUserId || ur.UserId == CustomWebApplicationFactory.ApproverUserId)).ToListAsync();
        db.UserRoles.RemoveRange(links);
        await db.SaveChangesAsync();
    }

    private async Task RemoveSeedAdministratorAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        (await db.Users.SingleAsync(u => u.Email == CustomWebApplicationFactory.SeedAdminEmail)).Status = UserStatus.Inactive;
        await db.SaveChangesAsync();
    }

    /// <summary>Encerra (vigência no passado) todo perfil Administrador da empresa.</summary>
    private async Task RemoveAdministratorsAsync(Guid companyId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var links = await db.UserRoles.Where(ur => ur.CompanyId == companyId && ur.Role.Name == SystemRoles.Administrator).ToListAsync();
        foreach (var link in links)
            link.ValidUntil = DateTimeOffset.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<CompanyAdminResult>> ProvisionAsync(IEnumerable<CompanyAdminSeed> seeds)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICompanyAdminProvisioner>().ProvisionMissingAsync(seeds);
    }

    private async Task<Guid> RoleIdAsync(string name)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<SighDbContext>().Roles.SingleAsync(r => r.Name == name)).Id;
    }

    private async Task<Guid> CreateViaApiAsync(HttpClient admin, string email, string cpf)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            fullName = "PESSOA " + email.Split('@')[0].ToUpperInvariant() + " TESTE", email, cpf, roleIds = new[] { await RoleIdAsync(SystemRoles.HrRegistrar) }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    private async Task<List<EmailOutboxMessage>> OutboxAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SighDbContext>().EmailOutbox.AsNoTracking().ToListAsync();
    }

    private async Task<List<AuditLog>> AuditAsync(string action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SighDbContext>().AuditLogs.AsNoTracking().Where(a => a.Action == action).ToListAsync();
    }

    private static string? Detail(AuditLog log, string key) => JsonDocument.Parse(log.NewValues!).RootElement.GetProperty(key).GetString();

    private static string TokenFrom(string body)
    {
        var match = Regex.Match(body, "/reset-password\\?token=([^&\\s]+)");
        match.Success.Should().BeTrue("o e-mail traz o link de definição de senha");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }
}
