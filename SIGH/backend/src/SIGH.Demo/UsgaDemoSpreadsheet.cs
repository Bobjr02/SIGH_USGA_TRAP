using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SIGH.Demo;

/// <summary>
/// A carga inicial FICTÍCIA da USGA do pacote de referência (docs/referencias) tem funcionários só em chaves com 3
/// dígitos. Na demonstração, a mesma planilha ganha 3 funcionários fictícios nas chaves da lista oficial que faltam
/// nela (USGA, USGA010 e USGA161), para a demonstração mostrar os dois formatos de chave. O arquivo do pacote não muda
/// (os testes usam as contagens dele); as linhas são acrescentadas na cópia em memória, nas mesmas 72 colunas.
/// </summary>
public static class UsgaDemoSpreadsheet
{
    public sealed record DemoEmployee(string Number, string Name, string Cpf, string Unit, string JobCode, string JobName, string DeptCode, string DeptName, string Admission);

    public static readonly IReadOnlyList<DemoEmployee> ExtraEmployees =
    [
        new("50090101", "RENATA ALVES DE LIMA TESTE", DemoDataSeeder.SyntheticCpf("500901010"), "USGA", "70000901", "ASSISTENTE ADMINISTRATIVO", "10101", "ESCRITORIO SERRA GRANDE", "02/03/2015"),
        new("50090102", "PAULO HENRIQUE MOTA TESTE", DemoDataSeeder.SyntheticCpf("500901020"), "USGA010", "70000902", "ASSESSOR DA SUPERINTENDENCIA", "10010", "SUPERINTENDENCIA", "14/07/2012"),
        new("50090103", "CLAUDIA REGINA SOUTO TESTE", DemoDataSeeder.SyntheticCpf("500901030"), "USGA161", "70000903", "ANALISTA COMERCIAL", "16101", "ESCRITORIO MACEIO", "21/01/2019"),
    ];

    /// <summary>Valor de cada coluna (pelo cabeçalho; a primeira ocorrência, se repetido) nas linhas acrescentadas.</summary>
    private static Dictionary<string, string> Values(DemoEmployee e) => new(StringComparer.Ordinal)
    {
        ["Matrícula"] = e.Number,
        ["Nome"] = e.Name,
        ["Status"] = "Ativo",
        ["Grupo_Emp"] = "Industria",
        ["Cargo"] = e.JobCode,
        ["Descr.Cargo"] = e.JobName,
        ["Uni.Org"] = e.DeptCode,
        ["Descr.Unid.Org."] = e.DeptName,
        ["Data admissão"] = e.Admission,
        ["CPF"] = e.Cpf,
        ["Sexo"] = "Feminino",
        ["Nome mãe"] = "MAE FICTICIA",
        ["Nome pai"] = "PAI FICTICIO",
        ["Endereço"] = "RUA FICTICIA",
        ["Tipo de contrato"] = "01",
        ["Descrição de contrato"] = "Prazo Indeterminado",
        ["Chave Organização"] = e.Unit,
    };

    /// <summary>Cópia da planilha do SAP com <see cref="ExtraEmployees"/> no fim da primeira aba.</summary>
    public static byte[] WithExtraEmployees(byte[] referenceXlsx)
    {
        using var buffer = new MemoryStream();
        buffer.Write(referenceXlsx);
        buffer.Position = 0;
        using (var document = SpreadsheetDocument.Open(buffer, isEditable: true))
        {
            var workbook = document.WorkbookPart!;
            var sheet = workbook.Workbook!.Sheets!.Elements<Sheet>().First();
            var worksheet = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
            var data = worksheet.Worksheet!.GetFirstChild<SheetData>()!;
            var strings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ToList();

            string TextOf(Cell cell) => cell.DataType?.Value == CellValues.SharedString && strings is not null
                ? strings[int.Parse(cell.InnerText, System.Globalization.CultureInfo.InvariantCulture)].InnerText
                : cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;

            var header = data.Elements<Row>().First();
            var columns = new List<(string Column, string Header)>();
            foreach (var cell in header.Elements<Cell>())
                columns.Add((new string(cell.CellReference!.Value!.TakeWhile(char.IsLetter).ToArray()), TextOf(cell).Trim()));

            var rowIndex = data.Elements<Row>().Max(r => r.RowIndex!.Value);
            foreach (var employee in ExtraEmployees)
            {
                rowIndex++;
                var values = Values(employee);
                var row = new Row { RowIndex = rowIndex };
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (column, name) in columns)
                {
                    if (!used.Add(name) || !values.TryGetValue(name, out var value))
                        continue;
                    row.AppendChild(new Cell
                    {
                        CellReference = $"{column}{rowIndex}",
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(new Text(value))
                    });
                }
                data.AppendChild(row);
            }

            if (worksheet.Worksheet.GetFirstChild<SheetDimension>() is { } dimension)
                dimension.Reference = $"A1:{columns[^1].Column}{rowIndex}";
            worksheet.Worksheet.Save();
        }
        return buffer.ToArray();
    }
}
