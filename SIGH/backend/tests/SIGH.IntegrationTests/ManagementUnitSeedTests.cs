using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Employees.Common;
using SIGH.Application.Interfaces;
using SIGH.Domain.Employees.Entities;
using SIGH.Domain.Entities;
using SIGH.IntegrationTests.Infrastructure;
using SIGH.Persistence.Context;
using SIGH.Persistence.Seeds;
using Xunit;

namespace SIGH.IntegrationTests;

/// <summary>Seed das chaves organizacionais: a lista oficial do cliente (20 chaves), por código, em cada empresa.</summary>
public class ManagementUnitSeedTests : IntegrationTestBase
{
    public ManagementUnitSeedTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task TheSeed_CreatesExactlyTheTwentyOfficialKeys_EachInTheCompanyOfItsPrefix_WithTheOfficialName()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();

        var units = await db.ManagementUnits.AsNoTracking().Where(m => m.Code != null && m.IsActive).ToListAsync();

        units.Select(u => (u.CompanyId, u.Code, u.Name)).Should().BeEquivalentTo(OfficialManagementUnits.All.Select(o =>
            (o.CompanyCode == CompanyDefaults.UsgaCode ? DatabaseSeeder.DefaultCompanyId : DatabaseSeeder.TrapicheCompanyId, (string?)o.Code, o.Name)));
    }

    [Fact]
    public async Task RunningTheSeedAgain_UpdatesOldNames_KeepsEditedNames_Reactivates_AndDeactivatesOnlyUnlinkedUnofficialKeys()
    {
        Guid linkedUnofficial, unlinkedUnofficial, usga131, usga132, trap260;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            var byCode = await db.ManagementUnits.Where(m => m.Code != null).ToDictionaryAsync(m => m.Code!);
            byCode["USGA131"].UpdateName("Industrial");            // nome do seed antigo: volta ao oficial
            byCode["USGA132"].UpdateName("Manutenção Editada");    // editado por alguém: mantido
            byCode["TRAP260"].Deactivate();                         // oficial inativa: reativada
            (usga131, usga132, trap260) = (byCode["USGA131"].Id, byCode["USGA132"].Id, byCode["TRAP260"].Id);

            // Chaves fora da lista (de bancos antigos): uma com gestor vinculado, outra sem vínculo nenhum.
            var linked = ManagementUnit.Create(DatabaseSeeder.TrapicheCompanyId, "TRAP998", "TRAP998");
            var unlinked = ManagementUnit.Create(DatabaseSeeder.TrapicheCompanyId, "TRAP999", "TRAP999");
            db.ManagementUnits.AddRange(linked, unlinked);
            await db.SaveChangesAsync();
            (linkedUnofficial, unlinkedUnofficial) = (linked.Id, unlinked.Id);
        }
        var manager = await AccessTestHelpers.CreateUserAsync(Factory, "gestor.chave.antiga@sigh.test",
            [new RoleGrant(SystemRoles.Manager, CompanyId: DatabaseSeeder.TrapicheCompanyId)]);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            db.UserManagementUnits.Add(new UserManagementUnit { UserId = manager.Id, ManagementUnitId = linkedUnofficial });
            await db.SaveChangesAsync();
        }

        var logger = new CapturingLogger();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            await new DatabaseSeeder(sp.GetRequiredService<SighDbContext>(), sp.GetRequiredService<IPasswordHasher>(),
                sp.GetRequiredService<IConfiguration>(), logger).SeedAsync();
        }

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SighDbContext>();
            var units = await db.ManagementUnits.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(m => m.Id);
            units[usga131].Name.Should().Be("Gerência Industrial");
            units[usga132].Name.Should().Be("Manutenção Editada");
            units[trap260].IsActive.Should().BeTrue();
            units[unlinkedUnofficial].IsActive.Should().BeFalse("fora da lista e sem vínculos: desativada");
            units[unlinkedUnofficial].IsDeleted.Should().BeFalse("nunca apagada");
            units[linkedUnofficial].IsActive.Should().BeTrue("tem gestor vinculado");
        }
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("TRAP998")
            && e.Message.Contains("0 funcionários") && e.Message.Contains("0 medidas") && e.Message.Contains("1 gestores"));
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning && e.Message.Contains("TRAP999"));
    }

    private sealed class CapturingLogger : ILogger<DatabaseSeeder>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
