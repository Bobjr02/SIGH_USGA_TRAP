using FluentAssertions;
using SIGH.Application.Disciplinary.History;
using SIGH.Application.Employees.Common;
using SIGH.Application.Users.Bootstrap;
using SIGH.Domain.Disciplinary.Enums;
using SIGH.Domain.Employees.Helpers;
using Xunit;

namespace SIGH.UnitTests.Disciplinary;

/// <summary>GERÊNCIA da planilha de penalidades: chave, nome oficial, nome sem "Gerência", nomes antigos e rejeições.</summary>
public class HistoryUnitResolutionTests
{
    private static readonly Guid Employee = Guid.NewGuid();

    private static readonly List<HistoryUnitRef> TrapUnits = OfficialManagementUnits.ForPrefix("TRAP")
        .Select(u => new HistoryUnitRef(Guid.NewGuid(), u.Code, u.Name))
        .Append(new HistoryUnitRef(Guid.NewGuid(), "TRAP199", "TRAP199", IsActive: false))
        .ToList();

    private static readonly List<HistoryUnitRef> UsgaUnits = OfficialManagementUnits.ForPrefix("USGA")
        .Select(u => new HistoryUnitRef(Guid.NewGuid(), u.Code, u.Name))
        .ToList();

    private static HistoryReference Reference(List<HistoryUnitRef> units, string prefix, IEnumerable<HistoryUnitRef> otherCompany) => new(
        new Dictionary<string, HistoryEmployeeRef> { ["70090001"] = new(Employee, "70090001", "PESSOA TESTE", units[1].Id) },
        units,
        new Dictionary<string, HistoryInfractionRef>(),
        new Dictionary<string, ExistingHistorical>(),
        [],
        otherCompany.SelectMany(u => HistoryImportPlanner.UnitKeys(u.Code, u.Name))
            .Where(k => k.Length > 0 && !units.SelectMany(o => HistoryImportPlanner.UnitKeys(o.Code, o.Name)).Contains(k)).ToHashSet(),
        prefix);

    private static HistoryImportPlan Plan(string unitText, bool usga = false)
    {
        var row = new HistoryRow(2, "70090001", new DateOnly(2025, 3, 10), DisciplinaryMeasureType.WrittenWarning, "ADVERTÊNCIA ESCRITA",
            "ATRASO", "atraso", unitText, null, null, null, null, null, []);
        var reference = usga ? Reference(UsgaUnits, "USGA", TrapUnits) : Reference(TrapUnits, "TRAP", UsgaUnits);
        return HistoryImportPlanner.Plan([(2, "70090001", row, null)], reference);
    }

    [Theory]
    [InlineData("TRAP230", "TRAP230")]
    [InlineData("TRAP", "TRAP")]
    [InlineData("Gerência Industrial", "TRAP230")]
    [InlineData("INDUSTRIAL", "TRAP230")]
    [InlineData("GERENCIA DE RELACAO DO TRABALHO", "TRAP250")]
    [InlineData("RELAÇÃO DO TRABALHO", "TRAP250")]
    [InlineData("Diretoria", "TRAP180")]
    public void TheUnit_IsFoundByCodeOrName(string text, string code)
    {
        var plan = Plan(text);

        plan.Rejected.Should().BeEmpty();
        plan.ToImport.Should().ContainSingle().Which.UnitAtTime.Code.Should().Be(code);
    }

    [Theory]
    [InlineData("AGRÍCOLA URBANO", "USGA137")]
    [InlineData("AGRICOLA RURICOLA", "USGA141")]
    [InlineData("MANUTENÇÃO", "USGA132")]
    [InlineData("ADMINISTRATIVO", "USGA135")]
    [InlineData("Serra Grande Escritório", "USGA")]
    [InlineData("USGA010", "USGA010")]
    public void TheOldNamesOfTheUsgaSpreadsheets_StillWork(string text, string code)
    {
        Plan(text, usga: true).ToImport.Should().ContainSingle().Which.UnitAtTime.Code.Should().Be(code);
    }

    [Theory]
    [InlineData("TRAP12", "fora do formato")]
    [InlineData("TRAP1234", "fora do formato")]
    [InlineData("TRAP 230", "fora do formato")]
    [InlineData("TRAP999", "não está na lista oficial")]
    [InlineData("TRAP199", "inativa")]
    public void OwnCodesOutsideTheOfficialList_OrInactive_AreRejected(string text, string message)
    {
        var plan = Plan(text);

        plan.ToImport.Should().BeEmpty();
        plan.Rejected.Should().ContainSingle().Which.Message.Should().Contain(message);
        plan.OtherCompanyRows.Should().Be(0);
    }

    [Theory]
    [InlineData("USGA131")]
    [InlineData("MANUTENÇÃO")]
    [InlineData("Escritório Maceió")]
    public void UnitsOfTheOtherCompany_AreRejectedAsOtherCompany(string text)
    {
        var plan = Plan(text);

        plan.Rejected.Should().ContainSingle().Which.Message.Should().Contain("outra empresa");
        plan.OtherCompanyRows.Should().Be(1);
    }

    [Fact]
    public void AnUnknownName_StillOnlyWarns_AndUsesTheCurrentUnit()
    {
        var plan = Plan("GERENCIA QUE NAO EXISTE");

        plan.Rejected.Should().BeEmpty();
        plan.Warnings.Should().Contain(w => w.Message.Contains("não corresponde"));
    }
}

public class TechnicalCpfTests
{
    [Fact]
    public void TechnicalCpfs_AreMarked_Unique_AndNeverAValidCpf()
    {
        var all = TechnicalCpf.Candidates().ToList();

        all.Should().HaveCount(99).And.OnlyHaveUniqueItems().And.Contain("00000000000").And.NotContain("00000000001");
        all.Should().OnlyContain(c => TechnicalCpf.IsTechnical(c) && !CpfValidator.IsValid(c));
        TechnicalCpf.ForDisplay("00000000002").Should().BeEmpty();
        TechnicalCpf.ForDisplay("39053344705").Should().Be("39053344705");
        TechnicalCpf.IsTechnical("39053344705").Should().BeFalse();
    }
}
