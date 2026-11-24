using FluentAssertions;
using SIGH.Application.Employees.Common;
using SIGH.Application.Employees.Import;
using SIGH.Domain.Employees;
using SIGH.Domain.Employees.Entities;
using SIGH.Domain.Employees.Enums;
using SIGH.Infrastructure.Import;
using SIGH.IntegrationTests.Infrastructure;
using Xunit;
using R = SIGH.IntegrationTests.Infrastructure.HrXlsxBuilder.Row;

namespace SIGH.UnitTests.Employees;

/// <summary>Formato e ordem da Chave Organização: o prefixo sozinho ou o prefixo + 3 dígitos.</summary>
public class ManagementUnitCodesTests
{
    public static readonly string[] Usga =
        ["USGA", "USGA010", "USGA131", "USGA132", "USGA133", "USGA134", "USGA135", "USGA136", "USGA137", "USGA138", "USGA141", "USGA161"];

    public static readonly string[] Trap = ["TRAP", "TRAP180", "TRAP220", "TRAP230", "TRAP240", "TRAP250", "TRAP260", "TRAP300"];

    public static TheoryData<string, string> Official()
    {
        var data = new TheoryData<string, string>();
        foreach (var code in Usga) data.Add(code, "USGA");
        foreach (var code in Trap) data.Add(code, "TRAP");
        return data;
    }

    [Theory]
    [MemberData(nameof(Official))]
    public void TheTwentyOfficialCodes_AreValid_ForTheirPrefix_AndNotForTheOther(string code, string prefix)
    {
        ManagementUnitCodes.IsValid(code, prefix).Should().BeTrue();
        ManagementUnitCodes.IsValid(code, prefix == "USGA" ? "TRAP" : "USGA").Should().BeFalse();
        ManagementUnitCodes.TryParse(code, prefix, out var number).Should().BeTrue();
        number.Should().Be(code.Length == prefix.Length ? null : int.Parse(code[prefix.Length..]));
    }

    [Theory]
    [InlineData("TRAP12")]
    [InlineData("TRAP1234")]
    [InlineData("TRAP 230")]
    [InlineData(" TRAP230")]
    [InlineData("TRAP230 ")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("trap230")]
    [InlineData("trap")]
    [InlineData("Trap230")]
    [InlineData("TRAPAUT")]
    [InlineData("TRAP23A")]
    [InlineData("TRAP-230")]
    [InlineData("USGATRAP")]
    [InlineData("USGA131")]
    public void InvalidCodes_AreRejected_ForTrap(string? code)
    {
        ManagementUnitCodes.IsValid(code, "TRAP").Should().BeFalse();
    }

    [Theory]
    [InlineData("USGATRAP")]
    [InlineData("USGA13")]
    [InlineData("USGA1310")]
    [InlineData("usga131")]
    [InlineData("usga")]
    [InlineData("USGA 131")]
    [InlineData("")]
    [InlineData("TRAP230")]
    public void InvalidCodes_AreRejected_ForUsga(string code)
    {
        ManagementUnitCodes.IsValid(code, "USGA").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("usga")]
    [InlineData("US1")]
    public void AnInvalidPrefix_AcceptsNothing(string? prefix)
    {
        ManagementUnitCodes.IsValid("USGA131", prefix).Should().BeFalse();
    }

    [Fact]
    public void Ordering_PutsTheCodeWithoutSuffixFirst_ThenTheNumbersInOrder()
    {
        var shuffled = Usga.Concat(Trap).Reverse().OrderBy(c => c.GetHashCode()).ToList();

        var sorted = shuffled.OrderBy(c => c, ManagementUnitCodes.Comparer).ToList();

        sorted.Where(c => c.StartsWith("USGA")).Should().Equal(Usga);
        sorted.Where(c => c.StartsWith("TRAP")).Should().Equal(Trap);
        sorted.Should().Equal(Trap.Concat(Usga), "por prefixo (TRAP antes de USGA) e, dentro dele, sem sufixo e depois os números");
    }

    [Fact]
    public void Ordering_PutsInvalidCodesAfterTheValidOnes_AndNullLast()
    {
        string?[] codes = [null, "TRAP1234", "TRAP300", "TRAP", "TRAP12", "TRAP180"];

        codes.OrderBy(c => c, ManagementUnitCodes.Comparer).Should().Equal("TRAP", "TRAP180", "TRAP300", "TRAP12", "TRAP1234", null);
    }

    [Fact]
    public void TheOfficialList_HasExactlyTheTwentyCodes_EachInTheCompanyOfItsPrefix()
    {
        OfficialManagementUnits.All.Select(u => u.Code).Should().Equal(Usga.Concat(Trap));
        OfficialManagementUnits.All.Should().OnlyContain(u => ManagementUnitCodes.IsValid(u.Code, u.CompanyCode));
        OfficialManagementUnits.ForPrefix("USGA").Select(u => u.Code).Should().Equal(Usga);
        OfficialManagementUnits.ForPrefix("TRAP").Select(u => u.Code).Should().Equal(Trap);
        OfficialManagementUnits.Find("TRAP250")!.Name.Should().Be("Gerência de Relação do Trabalho");
        OfficialManagementUnits.Find("USGA")!.Name.Should().Be("Serra Grande Escritório");
        OfficialManagementUnits.Contains("TRAP135").Should().BeFalse("só as 8 chaves da lista oficial da Trapiche");
        OfficialManagementUnits.Contains("TRAPAUT").Should().BeFalse("autônomos não fazem parte do SIGH");
    }

    [Fact]
    public void Company_OwnsOnlyCodesInItsTwoFormats()
    {
        var trap = new Company();
        trap.SetManagementUnitPrefix("TRAP");

        trap.OwnsManagementUnitCode("TRAP").Should().BeTrue();
        trap.OwnsManagementUnitCode("TRAP230").Should().BeTrue();
        trap.OwnsManagementUnitCode("TRAPAUT").Should().BeFalse();
        trap.OwnsManagementUnitCode("TRAP12").Should().BeFalse();
        trap.OwnsManagementUnitCode("USGA131").Should().BeFalse();
    }
}

/// <summary>Chave Organização na importação de funcionários: formatos, lista oficial, inativa e autônomos ignorados.</summary>
public class EmployeeImportManagementUnitTests
{
    private static readonly ReferenceSnapshot TrapRefs = new(
        new Dictionary<string, string> { ["50001234"] = "Operador de Máquinas" },
        new Dictionary<string, string> { ["60001000"] = "Moagem" },
        new HashSet<string>(ManagementUnitCodesTests.Trap.Where(c => c != "TRAP300"), StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TRAP300" });

    private static readonly IReadOnlySet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TRAPAUT" };

    private static IReadOnlyList<HrRawRow> Rows(params R[] rows) =>
        new ExcelHrSpreadsheetReader().Read(new MemoryStream(new HrXlsxBuilder().AddRange(rows).Build())).Rows;

    private static R Row(int n, string unit) => new($"7009{n:0000}", $"PESSOA {n} TESTE", HrXlsxBuilder.Cpf(n), ManagementUnit: unit);

    private static EmployeeImportPlan Plan(IReadOnlyList<HrRawRow> rows) =>
        EmployeeImportPlanner.Plan(rows, [], [], TrapRefs, new DateOnly(2026, 11, 13), 10, "TRAP", Ignored);

    [Fact]
    public void BothFormats_AreAccepted()
    {
        var plan = Plan(Rows(Row(1, "TRAP"), Row(2, "TRAP180"), Row(3, "TRAP220"), Row(4, "TRAP230")));

        plan.ErrorCount.Should().Be(0, string.Join("; ", plan.Issues.Select(i => i.Message)));
        plan.Of(EmployeeChangeKind.Created).Select(c => c.Row!.ManagementUnitCode).Should().BeEquivalentTo(["TRAP", "TRAP180", "TRAP220", "TRAP230"]);
    }

    [Theory]
    [InlineData("TRAP12", "fora do formato")]
    [InlineData("TRAP1234", "fora do formato")]
    [InlineData("TRAP 230", "fora do formato")]
    [InlineData("trap230", "fora do formato")]
    [InlineData("TRAP135", "não está na lista oficial")]
    [InlineData("TRAP999", "não está na lista oficial")]
    [InlineData("TRAP300", "está inativa")]
    public void CodesOutsideTheOfficialList_OrInactive_AreRejectedInTheRow_WithAClearMessage(string unit, string message)
    {
        var plan = Plan(Rows(Row(1, unit), Row(2, "TRAP230")));

        var error = plan.Issues.Should().ContainSingle(i => i.Severity == HrIssueSeverity.Error).Subject;
        error.RowNumber.Should().Be(2);
        error.Message.Should().Contain(message).And.Contain(unit);
        plan.OtherCompanyRows.Should().Be(0, "a chave tem o prefixo da empresa");
        plan.Of(EmployeeChangeKind.Created).Should().ContainSingle();
    }

    [Fact]
    public void AnEmptyCode_IsRejectedAsRequired()
    {
        var plan = Plan(Rows(Row(1, "")));

        plan.Issues.Should().ContainSingle(i => i.Severity == HrIssueSeverity.Error).Which.Message.Should().Contain("Chave Organização obrigatório");
    }

    [Fact]
    public void AutonomousRows_AreIgnored_NotCountedAsErrors_NorForTheOtherCompanyAlert()
    {
        // 3 linhas de autônomos (uma até com CPF inválido), 1 de outra empresa e 1 válida.
        var rows = Rows(Row(1, "TRAPAUT"), Row(2, "TRAPAUT"), new R("70099999", "AUTONOMO CPF RUIM TESTE", "123", ManagementUnit: "TRAPAUT"),
            Row(4, "USGA131"), Row(5, "TRAP230"));

        var plan = Plan(rows);

        plan.IgnoredRows.Should().Be(3);
        plan.ErrorCount.Should().Be(1, "só a linha da USGA131 é erro");
        plan.OtherCompanyRows.Should().Be(1);
        plan.LooksLikeOtherCompany.Should().BeFalse("1 de 2 linhas consideradas não passa da metade");
        plan.Issues.Should().NotContain(i => i.Message.Contains("TRAPAUT"));
        plan.Of(EmployeeChangeKind.Created).Select(c => c.EmployeeNumber).Should().Equal("70090005");
    }

    [Fact]
    public void AFileOfAutonomousOnly_DoesNotLookLikeAnotherCompany()
    {
        var plan = Plan(Rows(Row(1, "TRAPAUT"), Row(2, "TRAPAUT")));

        plan.IgnoredRows.Should().Be(2);
        plan.LooksLikeOtherCompany.Should().BeFalse();
        plan.ErrorCount.Should().Be(0);
    }

    [Fact]
    public void IgnoredCodes_DefaultToTrapaut_AndCanBeConfigured()
    {
        new ImportOptions().IgnoredManagementUnitCodeSet().Should().BeEquivalentTo(["TRAPAUT"]);
        new ImportOptions { IgnoredManagementUnitCodes = ["XAUT", " "] }.IgnoredManagementUnitCodeSet().Should().BeEquivalentTo(["XAUT"]);
    }

    [Fact]
    public void WithoutTheIgnoredList_AnAutonomousRowIsAnError()
    {
        var plan = EmployeeImportPlanner.Plan(Rows(Row(1, "TRAPAUT")), [], [], TrapRefs, new DateOnly(2026, 11, 13), 10, "TRAP");

        plan.IgnoredRows.Should().Be(0);
        plan.Issues.Should().ContainSingle(i => i.Severity == HrIssueSeverity.Error).Which.Message.Should().Contain("fora do formato");
    }
}
