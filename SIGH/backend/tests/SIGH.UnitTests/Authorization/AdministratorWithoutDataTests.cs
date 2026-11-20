using FluentAssertions;
using Microsoft.Extensions.Configuration;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Options;
using SIGH.Application.Users.Bootstrap;
using Xunit;

namespace SIGH.UnitTests.Authorization;

/// <summary>
/// O Administrador só gerencia acessos; os outros quatro perfis ficam EXATAMENTE como antes (a lista abaixo é a matriz
/// de antes desta mudança). Domínios de e-mail e a leitura de Seed:CompanyAdmins (com as chaves antigas).
/// </summary>
public class AdministratorWithoutDataTests
{
    private static readonly string[] Reports =
    [
        PermissionCodes.DisciplinaryReports.ViewDashboard, PermissionCodes.DisciplinaryReports.ViewMeasures,
        PermissionCodes.DisciplinaryReports.ViewEmployeeHistory, PermissionCodes.DisciplinaryReports.Export,
    ];

    /// <summary>As permissões listadas mais as 4 de relatórios (todos os perfis abaixo as têm).</summary>
    private static string[] Perms(params string[] permissions) => [.. permissions, .. Reports];

    public static IEnumerable<object[]> UnchangedRoles() =>
    [
        [SystemRoles.HrRegistrar, Perms(
            PermissionCodes.DisciplinaryRecords.View, PermissionCodes.DisciplinaryRecords.ViewAll, PermissionCodes.DisciplinaryRecords.Register,
            PermissionCodes.Employees.View, PermissionCodes.DisciplinaryCases.AddEvidence,
            PermissionCodes.Notifications.View, PermissionCodes.Notifications.Read)],
        [SystemRoles.HrApprover, Perms(
            PermissionCodes.DisciplinaryRecords.View, PermissionCodes.DisciplinaryRecords.ViewAll, PermissionCodes.DisciplinaryRecords.Approve,
            PermissionCodes.Employees.View, PermissionCodes.DisciplinaryCases.AddEvidence, PermissionCodes.DisciplinaryCases.RemoveEvidence,
            PermissionCodes.Notifications.View, PermissionCodes.Notifications.Read)],
        [SystemRoles.Legal, Perms(
            PermissionCodes.DisciplinaryRecords.View, PermissionCodes.DisciplinaryRecords.ViewAll, PermissionCodes.Employees.View,
            PermissionCodes.Audit.View)],
        [SystemRoles.Manager, Perms(PermissionCodes.DisciplinaryRecords.View, PermissionCodes.Employees.View)],
    ];

    [Theory]
    [MemberData(nameof(UnchangedRoles))]
    public void TheOtherRoles_AreUnchanged(string role, string[] permissions) =>
        SystemRoles.Find(role)!.Permissions.Should().BeEquivalentTo(permissions);

    [Fact]
    public void Administrator_HasNoEmployeeMeasureReportNotificationOrMeasureAuditPermission()
    {
        var admin = SystemRoles.Find(SystemRoles.Administrator)!.Permissions;
        var dataPermissions = PermissionCodes.All.Select(p => p.Code)
            .Where(c => c.StartsWith("Employees.", StringComparison.Ordinal) && c != PermissionCodes.Employees.Import
                        || c.StartsWith("Disciplinary.Records.", StringComparison.Ordinal)
                        || c.StartsWith("Disciplinary.Cases.", StringComparison.Ordinal)
                        || c.StartsWith("Disciplinary.Reports.", StringComparison.Ordinal)
                        || c.StartsWith("Notifications.", StringComparison.Ordinal)
                        || c == PermissionCodes.Audit.View)
            .ToList();

        dataPermissions.Should().NotBeEmpty();
        admin.Should().NotIntersectWith(dataPermissions);
        admin.Should().Contain([PermissionCodes.Audit.ViewAccess, PermissionCodes.Employees.Import, PermissionCodes.DisciplinaryHistory.Import]);
    }

    [Theory]
    [InlineData("pessoa@usga.com.br", true)]
    [InlineData("Pessoa@USGA.COM.BR", true)]
    [InlineData("pessoa@gmail.com", false)]
    [InlineData("pessoa@filial.usga.com.br", false)]
    [InlineData("pessoa@usga.com.br.outro", false)]
    [InlineData("usga.com.br", false)]
    [InlineData("pessoa@", false)]
    public void WithoutConfiguration_OnlyUsgaComBrIsAccepted(string email, bool allowed) =>
        new AuthOptions().IsEmailDomainAllowed(email).Should().Be(allowed);

    [Fact]
    public void ConfiguredDomains_ReplaceTheDefault_AndTheMessageListsThem()
    {
        var options = new AuthOptions { AllowedEmailDomains = ["usga.com.br", " @Demo.Local "] };

        options.IsEmailDomainAllowed("x@demo.local").Should().BeTrue();
        options.IsEmailDomainAllowed("x@usga.com.br").Should().BeTrue();
        options.EmailDomainMessage().Should().Be("Use um e-mail corporativo: @usga.com.br, @demo.local.");
    }

    [Fact]
    public void CompanyAdmins_AreRead_AndTheOldKeysBecomeEntriesOfTheSameMechanism()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:CompanyAdmins:0:CompanyCode"] = "trap",
            ["Seed:CompanyAdmins:0:Email"] = "admin.trap@usga.com.br",
            ["Seed:CompanyAdmins:0:Name"] = "ADMIN DA TRAPICHE",
            ["Seed:CompanyAdmins:1:CompanyCode"] = "USGA",
            ["Seed:CompanyAdmins:1:Email"] = "",
            ["Seed:Companies:TRAP:AdminEmail"] = "antigo.trap@usga.com.br",
            ["Seed:Companies:TRAP:AdminPassword"] = "ignorada",
            ["Seed:AdminEmail"] = "admin@usga.com.br",
            ["Seed:AdminPassword"] = "ignorada",
        }).Build();

        CompanyAdminProvisioner.ReadConfiguration(configuration).Should().Equal(
            new CompanyAdminSeed("TRAP", "admin.trap@usga.com.br", "ADMIN DA TRAPICHE"),
            new CompanyAdminSeed("TRAP", "antigo.trap@usga.com.br"),
            new CompanyAdminSeed("USGA", "admin@usga.com.br", "Administrador do Sistema"));
    }
}
