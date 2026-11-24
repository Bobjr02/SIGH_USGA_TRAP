using FluentAssertions;
using SIGH.Application.Employees.Common;
using SIGH.Application.Employees.Import;
using SIGH.Demo;
using SIGH.Infrastructure.Import;
using Xunit;

namespace SIGH.UnitTests.Employees.Import;

/// <summary>As planilhas fictícias da demonstração, lidas e planejadas como na importação real.</summary>
public class DemoSpreadsheetsTests
{
    private static ReferenceSnapshot OfficialRefs(string prefix) => new(
        new Dictionary<string, string>(), new Dictionary<string, string>(),
        OfficialManagementUnits.ForPrefix(prefix).Select(u => u.Code).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static EmployeeImportPlan Plan(byte[] xlsx, string prefix)
    {
        var sheet = new ExcelHrSpreadsheetReader().Read(new MemoryStream(xlsx));
        return EmployeeImportPlanner.Plan(sheet.Rows, sheet.Warnings, [], OfficialRefs(prefix), new DateOnly(2026, 11, 23), 10, prefix,
            new ImportOptions().IgnoredManagementUnitCodeSet());
    }

    private static string ReferenceFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SIGH", "docs", "referencias")))
            dir = dir.Parent;
        dir.Should().NotBeNull("o teste roda dentro do repositório");
        return Path.Combine([dir!.FullName, "SIGH", "docs", "referencias", .. parts]);
    }

    [Fact]
    public void TheTrapicheFile_UsesBothFormats_AndIgnoresTheTwoAutonomousRows()
    {
        var plan = Plan(TrapicheDemoSpreadsheets.EmployeesXlsx(), "TRAP");

        plan.ErrorCount.Should().Be(0, string.Join("; ", plan.Issues.Select(i => i.Message)));
        plan.IgnoredRows.Should().Be(2);
        plan.TotalRows.Should().Be(TrapicheDemoSpreadsheets.Employees.Count + 2);
        plan.Of(EmployeeChangeKind.Created).Select(c => c.Row!.ManagementUnitCode).Distinct()
            .Should().BeEquivalentTo(["TRAP", "TRAP180", "TRAP220", "TRAP230"]);
        TrapicheDemoSpreadsheets.NumbersIn("TRAP220").Should().HaveCount(6);
        TrapicheDemoSpreadsheets.NumbersIn("TRAP230").Should().HaveCount(6);
    }

    [Fact]
    public void TheUsgaDemoFile_AddsEmployeesInUsgaUsga010AndUsga161_ToTheReferenceLoad()
    {
        var reference = File.ReadAllBytes(ReferenceFile("importacao-funcionarios", "funcionarios_carga_inicial.xlsx"));

        var original = Plan(reference, "USGA");
        var plan = Plan(UsgaDemoSpreadsheet.WithExtraEmployees(reference), "USGA");

        plan.ErrorCount.Should().Be(0, string.Join("; ", plan.Issues.Select(i => i.Message)));
        plan.TotalRows.Should().Be(original.TotalRows + 3);
        plan.Of(EmployeeChangeKind.Created).Where(c => c.Row!.ManagementUnitCode is "USGA" or "USGA010" or "USGA161")
            .Select(c => c.EmployeeNumber).Should().BeEquivalentTo(UsgaDemoSpreadsheet.ExtraEmployees.Select(e => e.Number));
    }
}
