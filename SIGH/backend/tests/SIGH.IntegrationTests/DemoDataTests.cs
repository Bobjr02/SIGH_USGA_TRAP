using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIGH.Demo;
using SIGH.Application.Common.Models;
using SIGH.Application.Disciplinary.Records;
using SIGH.Application.Interfaces;
using SIGH.Domain.Disciplinary.Enums;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Context;
using Xunit;

namespace SIGH.IntegrationTests;

/// <summary>Parte C: dados de demonstração pelos serviços reais, idempotentes, e nunca fora de Development/Demo.</summary>
public class DemoDataTests : IntegrationTestBase
{
    private const string DemoPassword = "Demo#Senha2026ok";

    public DemoDataTests(CustomWebApplicationFactory factory) : base(factory) { }

    private static string ReferenceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "SIGH", "docs", "referencias");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("SIGH/docs/referencias não encontrado.");
    }

    private DemoDataSeeder Seeder(IConfiguration? configuration = null, Microsoft.Extensions.Logging.ILogger<DemoDataSeeder>? logger = null) =>
        new(Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            configuration ?? Factory.Services.GetRequiredService<IConfiguration>(),
            Options.Create(new DemoDataOptions { DemoData = true, DemoPassword = DemoPassword, DemoDataPath = ReferenceFolder() }),
            Factory.Services.GetRequiredService<IDateTimeProvider>(),
            logger ?? NullLogger<DemoDataSeeder>.Instance);

    /// <summary>Guarda as mensagens de log (para conferir que não há "Demo: falha").</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<DemoDataSeeder>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception.Message)));
    }

    /// <summary>
    /// A configuração do scripts/local-up (compose.app.yml): o primeiro administrador da USGA vem de Seed:CompanyAdmins,
    /// sem senha, e Seed:AdminEmail aponta para outra conta (o valor que o appsettings.json tinha). Antes da correção, a
    /// demonstração procurava o administrador por Seed:AdminEmail e falhava ("Demo: falha"). Agora ela acha o
    /// administrador do seed, dá a ele a senha de demonstração pelo link e cria tudo.
    /// </summary>
    [Fact]
    public async Task DemoData_WithTheLocalUpConfiguration_FindsTheSeedAdministrator_AndGivesHimTheDemoPassword()
    {
        var localUp = new ConfigurationBuilder()
            .AddConfiguration(Factory.Services.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:AdminEmail"] = "admin@sigh.com.br",
                ["Seed:CompanyAdmins:0:CompanyCode"] = "USGA",
                ["Seed:CompanyAdmins:0:Email"] = CustomWebApplicationFactory.SeedAdminEmail,
                ["Seed:CompanyAdmins:0:Name"] = "Administrador do Sistema",
            })
            .Build();
        var logger = new CapturingLogger();

        await Seeder(localUp, logger).SeedAsync();

        logger.Entries.Should().NotContain(e => e.Message.Contains("Demo: falha"));
        logger.Entries.Should().Contain(e => e.Message.Contains("Demo: dados de demonstração prontos"));
        var (admin, _) = await AuthTestHelpers.LoginAsync(Factory.CreateClient(), CustomWebApplicationFactory.SeedAdminEmail, DemoPassword);
        admin.MustChangePassword.Should().BeFalse();
        await AuthTestHelpers.LoginAsync(Factory.CreateClient(), "admin.usga@demo.local", DemoPassword);
        await AuthTestHelpers.LoginAsync(Factory.CreateClient(), "juridico@demo.local", DemoPassword);

        // Subir de novo não troca a senha de ninguém nem falha.
        var again = new CapturingLogger();
        await Seeder(localUp, again).SeedAsync();
        again.Entries.Should().NotContain(e => e.Message.Contains("Demo: falha"));
        await AuthTestHelpers.LoginAsync(Factory.CreateClient(), CustomWebApplicationFactory.SeedAdminEmail, DemoPassword);
    }

    /// <summary>Sem nenhum administrador da USGA configurado a demonstração não é criada (e a falha fica no log).</summary>
    [Fact]
    public async Task DemoData_WithoutAUsgaSeedAdministrator_LogsTheFailure()
    {
        var none = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:AdminEmail"] = "ninguem@sigh.test" }).Build();
        var logger = new CapturingLogger();

        await Seeder(none, logger).SeedAsync();

        logger.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Message.Contains("Demo: falha") && e.Message.Contains("Seed:CompanyAdmins"));
    }

    [Fact]
    public async Task DemoData_UsesTheRealServices_CoversEveryState_AndIsIdempotent()
    {
        await Seeder().SeedAsync();
        await Seeder().SeedAsync(); // subir de novo não duplica nada

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
        var usga = SIGH.Persistence.Seeds.DatabaseSeeder.DefaultCompanyId;
        var trap = SIGH.Persistence.Seeds.DatabaseSeeder.TrapicheCompanyId;
        (await db.Employees.CountAsync(e => e.CompanyId == usga)).Should().Be(40);
        (await db.Employees.CountAsync(e => e.CompanyId == trap)).Should().Be(TrapicheDemoSpreadsheets.Employees.Count);
        (await db.Employees.Where(e => e.CompanyId == trap).AllAsync(e => e.FullName.EndsWith("TESTE"))).Should().BeTrue();
        (await db.ManagementUnits.Where(m => m.CompanyId == trap).Select(m => m.Code).OrderBy(c => c).ToListAsync()).Should().Equal("TRAP131", "TRAP132");
        (await db.EmployeeImportBatches.CountAsync()).Should().Be(2, "uma carga por empresa, pelo importador, uma vez só");
        var demoUsers = await db.Users.Where(u => u.Email.EndsWith("@demo.local")).ToListAsync();
        demoUsers.Should().HaveCount(DemoDataSeeder.Users.Count + 1, "os usuários de demonstração e o admin.trap")
            .And.OnlyContain(u => !u.MustChangePassword && u.Status == SIGH.Domain.Enums.UserStatus.Active);
        var juridico = demoUsers.Single(u => u.Email == DemoDataSeeder.EmailOf("juridico"));
        (await db.UserRoles.Where(ur => ur.UserId == juridico.Id).Select(ur => ur.CompanyId).Distinct().CountAsync()).Should().Be(2, "Jurídico nas duas empresas");
        (await db.UserRoles.Where(ur => ur.User.Email == "auxiliar.trap@demo.local").Select(ur => ur.CompanyId).Distinct().ToListAsync()).Should().Equal(trap);

        // b. histórico fictício pelo importador do histórico, uma vez só por empresa.
        (await db.DisciplinaryCases.CountAsync(c => c.Kind == DisciplinaryCaseKind.Historical && c.CompanyId == usga)).Should().Be(14);
        (await db.DisciplinaryCases.CountAsync(c => c.Kind == DisciplinaryCaseKind.Historical && c.CompanyId == trap)).Should().Be(TrapicheDemoSpreadsheets.History.Count);
        (await db.DisciplinaryHistoryImportBatches.CountAsync()).Should().Be(2);
        (await db.InfractionTypes.CountAsync(i => i.Name == "USO DE CELULAR EM SERVIÇO")).Should().Be(1);

        foreach (var (company, total, approved, pending, rejected) in new[] { (usga, 8, 4, 2, 1), (trap, 5, 2, 1, 1) })
        {
            var records = await db.DisciplinaryCases.Include(c => c.Decisions).Include(c => c.Evidences)
                .Where(c => c.Kind == DisciplinaryCaseKind.Record && c.CompanyId == company).ToListAsync();
            records.Should().HaveCount(total);
            records.Count(c => c.Status == DisciplinaryCaseStatus.Completed).Should().Be(approved);
            records.Count(c => c.Status == DisciplinaryCaseStatus.Cancelled).Should().Be(1);
            records.Count(c => c.Status == DisciplinaryCaseStatus.AwaitingDecision && c.Decision != null).Should().Be(pending, "pendentes");
            records.Count(c => c.Status == DisciplinaryCaseStatus.AwaitingDecision && c.Decision == null).Should().Be(rejected, "rejeitada aguardando correção");
            records.Should().ContainSingle(c => c.Evidences.Any(e => e.HasFile && e.OriginalFileName == "carta-assinada-ficticia.pdf"));
            var recordIds = records.Select(r => r.Id).ToList();
            (await db.DisciplinaryMeasures.CountAsync(m => recordIds.Contains(m.DisciplinaryCaseId)
                && m.MeasureType == DisciplinaryMeasureType.Suspension && m.Status == DisciplinaryMeasureStatus.Applied)).Should().Be(1, "a suspensão aprovada no SIGH");
        }

        // Os usuários entram com a senha de demonstração, sem troca obrigatória, e o escopo vale.
        var client = Factory.CreateClient();
        // O administrador do seed também (pelo link de definição, internamente).
        await AuthTestHelpers.LoginAsync(Factory.CreateClient(), CustomWebApplicationFactory.SeedAdminEmail, DemoPassword);
        var (login, _) = await AuthTestHelpers.LoginAsync(client, "gestor.industrial@demo.local", DemoPassword);
        login.MustChangePassword.Should().BeFalse();
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        var seen = (await client.GetFromJsonAsync<Result<PagedResult<RecordSummaryDto>>>("/api/v1/disciplinary-records?pageSize=50"))!.Data!.Items;
        seen.Should().NotBeEmpty().And.OnlyContain(r => r.ManagementUnitCode == "USGA131");
        seen.Should().Contain(r => r.Origin == RecordOrigin.Historical, "o gestor vê o histórico da gerência dele");

        // O gestor da TRAP131 vê só a TRAP131; o jurídico, no modo "Todas", vê as duas empresas.
        var trapManager = Factory.CreateClient();
        trapManager.DefaultRequestHeaders.Authorization = new("Bearer", (await AuthTestHelpers.LoginAsync(trapManager, "gestor.trap131@demo.local", DemoPassword)).Body.AccessToken);
        var trapSeen = (await trapManager.GetFromJsonAsync<Result<PagedResult<RecordSummaryDto>>>("/api/v1/disciplinary-records?pageSize=50"))!.Data!.Items;
        trapSeen.Should().NotBeEmpty().And.OnlyContain(r => r.ManagementUnitCode == "TRAP131" && r.CompanyCode == "TRAP");

        var legal = Factory.CreateClient();
        legal.DefaultRequestHeaders.Authorization = new("Bearer", (await AuthTestHelpers.LoginAsync(legal, "juridico@demo.local", DemoPassword)).Body.AccessToken);
        legal.DefaultRequestHeaders.Add("X-SIGH-Company", "TODAS");
        var all = (await legal.GetFromJsonAsync<Result<PagedResult<RecordSummaryDto>>>("/api/v1/disciplinary-records?pageSize=100"))!.Data!.Items;
        all.Select(r => r.CompanyCode).Distinct().Should().BeEquivalentTo(["USGA", "TRAP"]);
    }

    [Fact]
    public void DemoData_InProduction_PreventsTheApiFromStarting()
    {
        using var production = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Seed:DemoData", "true");
            builder.UseSetting("Seed:DemoPassword", DemoPassword);
        });

        var previousExitCode = Environment.ExitCode;
        try
        {
            var start = () => production.CreateClient();

            // O Program valida as opções (ValidateOnStart) antes das migrations: a falha de Seed:DemoData é
            // registrada como erro fatal de configuração, o processo sai com código 1 e o servidor nunca sobe.
            start.Should().Throw<InvalidOperationException>().WithMessage("*not been started*");
            Environment.ExitCode.Should().Be(1);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }
}
