using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Interfaces;
using SIGH.Domain.Entities;
using SIGH.Domain.Enums;
using SIGH.Persistence.Context;
using SIGH.Persistence.Seeds;
using Microsoft.Extensions.Options;
using SIGH.Application.Options;
using SIGH.Application.Security;
using SIGH.Application.Users.Bootstrap;
using SIGH.Application.Users.SetupLink;
using Xunit;

namespace SIGH.UnitTests.Persistence;

public class DatabaseSeederTests
{
    private const string AdminEmail = "admin@sigh.com.br";

    private static SighDbContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<SighDbContext>().UseInMemoryDatabase(dbName).Options);

    /// <summary>
    /// Seeder com o serviço real do primeiro administrador. <paramref name="adminPassword"/> é a chave antiga
    /// Seed:AdminPassword: configurada ou não, é ignorada (ninguém conhece a senha do administrador).
    /// </summary>
    private static DatabaseSeeder CreateSeeder(SighDbContext context, string? adminPassword, string? adminEmail = AdminEmail, Mock<IAccountSetupLinkSender>? links = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Seed:AdminEmail"] = adminEmail,
            ["Seed:DefaultCompanyName"] = "Empresa Teste",
            ["Seed:AdminPassword"] = adminPassword
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.HashPassword(It.IsAny<string>())).Returns<string>(p => $"hashed:{p}");
        var random = new Mock<IRandomStringGenerator>();
        random.Setup(r => r.GenerateRandomString(It.IsAny<int>())).Returns("aleatoria-que-ninguem-conhece");
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(c => c.UtcNow).Returns(() => DateTimeOffset.UtcNow);

        var provisioner = new CompanyAdminProvisioner(
            context, hasher.Object, random.Object, clock.Object, (links ?? new Mock<IAccountSetupLinkSender>()).Object,
            Mock.Of<ISecurityAuditLogger>(), Options.Create(new AuthOptions { AllowedEmailDomains = ["sigh.com.br"] }),
            NullLogger<CompanyAdminProvisioner>.Instance);

        return new DatabaseSeeder(context, hasher.Object, configuration, NullLogger<DatabaseSeeder>.Instance, provisioner);
    }

    [Fact]
    public async Task SeedAsync_OnEmptyDatabase_CreatesPermissionsRoleCompanyAndAdmin()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var context = CreateContext(dbName);
        var links = new Mock<IAccountSetupLinkSender>();

        await CreateSeeder(context, "SenhaForte@123", links: links).SeedAsync();

        context.Permissions.Select(p => p.Code).Should().BeEquivalentTo(PermissionCodes.All.Select(p => p.Code));

        var role = await context.Roles.SingleAsync(r => r.Name == DatabaseSeeder.AdministratorRoleName);
        (await PermissionsOf(context, SystemRoles.Administrator))
            .Should().BeEquivalentTo(SystemRoles.Find(SystemRoles.Administrator)!.Permissions);
        context.Roles.Select(r => r.Name).Should().BeEquivalentTo(SystemRoles.All.Select(r => r.Name));

        var company = await context.Companies.SingleAsync();
        company.Id.Should().Be(DatabaseSeeder.DefaultCompanyId);
        company.Name.Should().Be("Empresa Teste");

        var admin = await context.Users.Include(u => u.UserRoles).SingleAsync();
        admin.Email.Should().Be(AdminEmail);
        // A senha configurada (Seed:AdminPassword) é ignorada: a conta nasce com uma senha aleatória e o link.
        admin.PasswordHash.Should().Be("hashed:aleatoria-que-ninguem-conhece");
        admin.MustChangePassword.Should().BeTrue();
        admin.Status.Should().Be(UserStatus.PendingFirstAccess);
        admin.UserRoles.Should().ContainSingle(ur => ur.RoleId == role.Id && ur.CompanyId == DatabaseSeeder.DefaultCompanyId);
        links.Verify(l => l.SendAdministratorAccessAsync(It.Is<User>(u => u.Email == AdminEmail), "Empresa Teste", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SeedAsync_EmailOutsideTheAllowedDomains_DoesNotCreateTheAdmin()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString());

        await CreateSeeder(context, adminPassword: null, adminEmail: "admin@gmail.com").SeedAsync();

        (await context.Users.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SeedAsync_RunTwice_DoesNotDuplicateData()
    {
        var dbName = Guid.NewGuid().ToString();

        await using (var context = CreateContext(dbName))
            await CreateSeeder(context, "SenhaForte@123").SeedAsync();

        await using (var context = CreateContext(dbName))
        {
            await CreateSeeder(context, "SenhaForte@123").SeedAsync();

            (await context.Permissions.CountAsync()).Should().Be(PermissionCodes.All.Count);
            (await context.RolePermissions.CountAsync()).Should().Be(SystemRoles.All.Sum(r => r.Permissions.Count));
            (await context.Roles.CountAsync()).Should().Be(SystemRoles.All.Count);
            (await context.Companies.CountAsync()).Should().Be(1);
            (await context.Users.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task SeedAsync_WhenPermissionsAlreadyExistWithoutUsers_InsertsOnlyMissingOnes()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var context = CreateContext(dbName);
        context.Permissions.Add(new Permission { Code = PermissionCodes.Users.View, Name = "x", Description = "x" });
        await context.SaveChangesAsync();

        await CreateSeeder(context, adminPassword: null).SeedAsync();

        (await context.Permissions.CountAsync()).Should().Be(PermissionCodes.All.Count);
        (await context.Permissions.CountAsync(p => p.Code == PermissionCodes.Users.View)).Should().Be(1);
    }

    [Fact]
    public async Task SeedAsync_WithoutAdminEmail_DoesNotCreateAdminButSeedsTheRest()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var context = CreateContext(dbName);

        await CreateSeeder(context, adminPassword: null, adminEmail: null).SeedAsync();

        (await context.Users.AnyAsync()).Should().BeFalse();
        (await context.Permissions.CountAsync()).Should().Be(PermissionCodes.All.Count);
        (await context.Companies.AnyAsync(c => c.Id == DatabaseSeeder.DefaultCompanyId)).Should().BeTrue();
    }

    [Fact]
    public async Task SeedAsync_WhenAdminEmailChangesAfterAdminWasSeeded_DoesNotCreateSecondAdmin()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var context = CreateContext(dbName))
        {
            await CreateSeeder(context, adminPassword: "Senha@Inicial1").SeedAsync();
        }

        await using (var context = CreateContext(dbName))
        {
            var act = () => CreateSeeder(context, adminPassword: "Senha@Inicial1", adminEmail: "outro.admin@sigh.com.br").SeedAsync();

            await act.Should().NotThrowAsync();
            (await context.Users.Select(u => u.Email).ToListAsync()).Should().Equal(AdminEmail);
        }
    }

    [Fact]
    public async Task Administrator_OnlyManagesAccess_AndNeverReadsEmployeeOrMeasureData()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await CreateSeeder(context, "SenhaForte@123").SeedAsync();

        var admin = await PermissionsOf(context, SystemRoles.Administrator);
        admin.Should().BeEquivalentTo(
        [
            PermissionCodes.Users.View, PermissionCodes.Users.Create, PermissionCodes.Users.ChangeStatus,
            PermissionCodes.Users.Unlock, PermissionCodes.Users.ManageRoles, PermissionCodes.ManagementUnits.Manage,
            PermissionCodes.InfractionTypes.View, PermissionCodes.InfractionTypes.Create, PermissionCodes.InfractionTypes.Update,
            PermissionCodes.InfractionTypes.Activate, PermissionCodes.InfractionTypes.Deactivate,
            PermissionCodes.Audit.ViewAccess,
            // Exceção decidida pelo cliente: o Administrador continua importando funcionários e histórico.
            PermissionCodes.Employees.Import, PermissionCodes.DisciplinaryHistory.Import,
        ]);
        admin.Should().NotContain(
        [
            PermissionCodes.Employees.View, PermissionCodes.DisciplinaryRecords.View, PermissionCodes.DisciplinaryRecords.ViewAll,
            PermissionCodes.DisciplinaryRecords.Register, PermissionCodes.DisciplinaryRecords.Approve,
            PermissionCodes.DisciplinaryReports.ViewDashboard, PermissionCodes.DisciplinaryReports.ViewMeasures,
            PermissionCodes.DisciplinaryReports.ViewEmployeeHistory, PermissionCodes.DisciplinaryReports.Export,
            PermissionCodes.Audit.View, PermissionCodes.Notifications.View,
        ]);
    }

    [Fact]
    public async Task NoSeededRole_ReceivesTheStepByStepProcessPermissions_ExceptEvidence()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await CreateSeeder(context, "SenhaForte@123").SeedAsync();

        var granted = await context.RolePermissions.Select(rp => rp.Permission.Code).Distinct().ToListAsync();
        var legacy = typeof(PermissionCodes.DisciplinaryCases).GetFields()
            .Select(f => (string)f.GetValue(null)!)
            .Except([PermissionCodes.DisciplinaryCases.AddEvidence, PermissionCodes.DisciplinaryCases.RemoveEvidence]);
        granted.Should().NotIntersectWith(legacy);
    }

    [Fact]
    public async Task OnlyTheManagerAndTheAdministrator_HaveNoFullScope()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString());
        await CreateSeeder(context, "SenhaForte@123").SeedAsync();

        foreach (var role in SystemRoles.All)
        {
            var permissions = await PermissionsOf(context, role.Name);
            permissions.Should().BeEquivalentTo(role.Permissions, $"o perfil {role.Name} fica exatamente com a lista fixa");
            permissions.Contains(PermissionCodes.DisciplinaryRecords.ViewAll).Should().Be(role.Name is not (SystemRoles.Manager or SystemRoles.Administrator));
        }
    }

    [Fact]
    public async Task ExistingAdministratorWithAllPermissions_LosesTheExtraOnes_AndStaysAdministrator()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var context = CreateContext(dbName))
        {
            // Banco antigo: o seed dava TODAS as permissões ao Administrator.
            var role = new Role { Name = DatabaseSeeder.AdministratorRoleName, Description = "antigo" };
            context.Roles.Add(role);
            foreach (var definition in PermissionCodes.All)
            {
                var permission = new Permission { Code = definition.Code, Name = definition.Name, Description = definition.Description };
                context.Permissions.Add(permission);
                context.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            }
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(dbName))
        {
            await CreateSeeder(context, "SenhaForte@123").SeedAsync();

            (await PermissionsOf(context, SystemRoles.Administrator))
                .Should().BeEquivalentTo(SystemRoles.Find(SystemRoles.Administrator)!.Permissions);
            // Retirada lógica: o vínculo antigo fica na história.
            (await context.RolePermissions.IgnoreQueryFilters().CountAsync(rp => rp.IsDeleted)).Should().BeGreaterThan(0);
            var admin = await context.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).SingleAsync();
            admin.UserRoles.Select(ur => ur.Role.Name).Should().Equal(SystemRoles.Administrator);
        }
    }

    private static Task<List<string>> PermissionsOf(SIGH.Persistence.Context.SighDbContext context, string roleName) =>
        context.RolePermissions.Where(rp => rp.Role.Name == roleName).Select(rp => rp.Permission.Code).ToListAsync();
}
