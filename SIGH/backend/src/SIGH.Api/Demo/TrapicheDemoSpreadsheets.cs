using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SIGH.Api.Demo;

/// <summary>
/// Planilhas FICTÍCIAS da Trapiche, geradas por código (nada é versionado): a exportação do SAP e a planilha de
/// penalidades, no mesmo formato das da USGA. As Chaves Organização são TRAP131 e TRAP132 (os códigos reais da
/// Trapiche ainda não existem) e todos os nomes terminam com "TESTE".
/// </summary>
public static class TrapicheDemoSpreadsheets
{
    public sealed record DemoEmployee(string Number, string Name, string Cpf, string Unit, string Group, string JobCode, string JobName, string DeptCode, string DeptName, string Admission);

    /// <summary>Funcionários fictícios da Trapiche (matrículas 7009xxxx).</summary>
    public static readonly IReadOnlyList<DemoEmployee> Employees =
    [
        new("70090001", "JOAO DA SILVA TESTE", Cpf("700900001"), "TRAP131", "Indústria", "70001001", "Operador de Caldeira", "71001000", "Caldeiraria", "03/02/2020"),
        new("70090002", "MARIA DE SOUZA TESTE", Cpf("700900002"), "TRAP131", "Indústria", "70001002", "Operador de Moenda", "71002000", "Moagem", "10/03/2021"),
        new("70090003", "JOSE PEREIRA TESTE", Cpf("700900003"), "TRAP131", "Indústria", "70001003", "Eletricista", "71003000", "Manutenção Elétrica", "15/06/2019"),
        new("70090004", "ANA LIMA TESTE", Cpf("700900004"), "TRAP131", "Indústria", "70001001", "Operador de Caldeira", "71001000", "Caldeiraria", "01/08/2022"),
        new("70090005", "PEDRO ALVES TESTE", Cpf("700900005"), "TRAP131", "Indústria", "70001004", "Mecânico", "71004000", "Oficina", "20/01/2018"),
        new("70090006", "LUCIA FERREIRA TESTE", Cpf("700900006"), "TRAP131", "Indústria", "70001002", "Operador de Moenda", "71002000", "Moagem", "05/05/2023"),
        new("70090007", "CARLOS SANTOS TESTE", Cpf("700900007"), "TRAP132", "Campo", "70002001", "Trabalhador Rural", "72001000", "Corte de Cana", "12/02/2020"),
        new("70090008", "FRANCISCA ROCHA TESTE", Cpf("700900008"), "TRAP132", "Campo", "70002001", "Trabalhador Rural", "72001000", "Corte de Cana", "18/07/2021"),
        new("70090009", "ANTONIO GOMES TESTE", Cpf("700900009"), "TRAP132", "Campo", "70002002", "Tratorista", "72002000", "Mecanização Agrícola", "30/09/2019"),
        new("70090010", "RAIMUNDA COSTA TESTE", Cpf("700900010"), "TRAP132", "Campo", "70002001", "Trabalhador Rural", "72001000", "Corte de Cana", "04/04/2022"),
        new("70090011", "SEBASTIAO RIBEIRO TESTE", Cpf("700900011"), "TRAP132", "Campo", "70002003", "Motorista Canavieiro", "72003000", "Transporte", "22/11/2017"),
        new("70090012", "TEREZA MARTINS TESTE", Cpf("700900012"), "TRAP132", "Campo", "70002002", "Tratorista", "72002000", "Mecanização Agrícola", "14/03/2024"),
    ];

    private static readonly string[] EmployeeHeaders =
    [
        "Matrícula", "Nome", "CPF", "Status", "Grupo_Emp", "Cargo", "Descr.Cargo", "Uni.Org", "Descr.Unid.Org.",
        "Data admissão", "Data demissão", "Turma", "Descrição Turma", "Chave Organização", "Estado", "Estado", "Campo SAP 01"
    ];

    /// <summary>Exportação do SAP fictícia da Trapiche (aba Data, todas as células como texto).</summary>
    public static byte[] EmployeesXlsx(IEnumerable<DemoEmployee>? employees = null)
    {
        var rows = (employees ?? Employees).Select(e => new object[]
        {
            e.Number, e.Name, e.Cpf, "Ativo", e.Group, e.JobCode, e.JobName, e.DeptCode, e.DeptName,
            e.Admission, "", "999", e.Group == "Campo" ? "TURMA A" : "", e.Unit, "PE FICTICIO", "Solteiro FICTICIO", "FICTICIO"
        });
        return Build("Data", EmployeeHeaders, rows);
    }

    public sealed record DemoHistory(string Number, string Date, string Type, string Reason, string Infraction, string Days, string Period, string EndDate);

    /// <summary>Penalidades fictícias anteriores ao SIGH (planilha de penalidades da Trapiche).</summary>
    public static readonly IReadOnlyList<DemoHistory> History =
    [
        new("70090001", "10/03/2025", "ADVERTÊNCIA ESCRITA", "FALTAS INJUSTIFICADAS DE FORMA RECORRENTE", "ADVERTÊNCIA", "", "", ""),
        new("70090003", "22/05/2025", "ADVERTÊNCIA ESCRITA", "FALTA DE UTILIZAÇÃO DO EPI", "ADVERTÊNCIA", "", "", ""),
        new("70090003", "04/08/2025", "SUSPENSÃO 1", "FALTA DE UTILIZAÇÃO DO EPI", "SUSPENSÃO", "2", "05/08/2025 À 06/08/2025", "06/08/2025"),
        new("70090007", "18/09/2025", "ADVERTÊNCIA ESCRITA", "ATRASOS REITERADOS", "ADVERTÊNCIA", "", "", ""),
        new("70090009", "02/02/2026", "ADVERTÊNCIA ESCRITA", "FALTAS INJUSTIFICADAS DE FORMA RECORRENTE", "ADVERTÊNCIA", "", "", ""),
    ];

    private static readonly string[] HistoryHeaders =
    [
        "MATRÍCULA", "DATA", "TIPO DA INFRAÇÃO", "MOTIVO DA INFRAÇÃO", "DIAS DE SUSPENSÃO", "PERÍODO DA SUSPENSÃO",
        "NOME DO FUNCIONÁRIO", "CARGO", "SETOR", "DATA FIM DA SUSPENSÃO", "GERÊNCIA", "INFRAÇÃO", "Nº ADVERTÊNCIA", "Nº SUSPENSÃO"
    ];

    /// <summary>
    /// Planilha de penalidades fictícia. A GERÊNCIA é o NOME da gerência da Trapiche: como a importação de funcionários
    /// cria as gerências com o código como nome, aqui ela é "TRAP131"/"TRAP132".
    /// </summary>
    public static byte[] HistoryXlsx(IEnumerable<DemoHistory>? history = null, Func<DemoHistory, string>? unitOf = null)
    {
        var byNumber = Employees.ToDictionary(e => e.Number);
        var rows = (history ?? History).Select(h =>
        {
            var employee = byNumber.GetValueOrDefault(h.Number);
            return new object[]
            {
                h.Number, h.Date, h.Type, h.Reason, h.Days, h.Period, employee?.Name ?? "FUNCIONARIO TESTE", employee?.JobName ?? "",
                employee?.DeptName ?? "", h.EndDate, unitOf?.Invoke(h) ?? employee?.Unit ?? "", h.Infraction, "", ""
            };
        });
        return Build("Penalidades", HistoryHeaders, rows);
    }

    /// <summary>CPF válido (dígitos verificadores corretos) a partir de 9 dígitos. Sintético: não é de ninguém.</summary>
    public static string Cpf(string baseNineDigits) => DemoDataSeeder.SyntheticCpf(baseNineDigits);

    private static byte[] Build(string sheetName, IReadOnlyList<string> headers, IEnumerable<object[]> rows)
    {
        using var buffer = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(buffer, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var sheetPart = workbook.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            sheetPart.Worksheet = new Worksheet(data);
            workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(sheetPart), SheetId = 1, Name = sheetName }));

            data.AppendChild(new Row(headers.Select(h => (OpenXmlElement)Text(h))));
            foreach (var values in rows)
                data.AppendChild(new Row(values.Select(v => (OpenXmlElement)Text(Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty))));
            workbook.Workbook.Save();
        }
        return buffer.ToArray();
    }

    private static Cell Text(string value) => new() { DataType = CellValues.InlineString, InlineString = new InlineString(new Text(value)) };
}
