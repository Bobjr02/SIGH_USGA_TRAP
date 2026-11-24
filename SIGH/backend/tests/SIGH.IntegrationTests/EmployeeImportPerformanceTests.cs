using System.Diagnostics;
using System.Net;
using FluentAssertions;
using SIGH.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using R = SIGH.IntegrationTests.Infrastructure.HrXlsxBuilder.Row;

namespace SIGH.IntegrationTests;

/// <summary>
/// Testes que medem tempo: rodam SOZINHOS (coleção sem paralelismo, executada depois das outras), para o tempo medir
/// a importação e não a disputa de CPU e banco com as outras classes de teste rodando ao mesmo tempo.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Desempenho (sem paralelismo)";
}

/// <summary>Desempenho da importação do SAP: 6000 linhas x 72 colunas, cada etapa abaixo de 15 s.</summary>
[Collection(PerformanceCollection.Name)]
public class EmployeeImportPerformanceTests : IntegrationTestBase
{
    /// <summary>As 12 chaves oficiais da USGA, nos dois formatos.</summary>
    private static readonly string[] Units =
        SIGH.Application.Employees.Common.OfficialManagementUnits.ForPrefix("USGA").Select(u => u.Code).ToArray();

    private readonly ITestOutputHelper _output;

    public EmployeeImportPerformanceTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
        : base(factory)
    {
        _output = output;
    }

    [Fact]
    public async Task SixThousandRowsWith72Columns_PreviewAndConfirmEachUnder15Seconds()
    {
        using var admin = await EmployeeImportTests.AdminAsync(Factory);
        // 21 colunas de base + 51 extras = 72 colunas.
        var builder = new HrXlsxBuilder(extraColumns: 51);
        for (var i = 0; i < 6000; i++)
        {
            var mu = Units[i % Units.Length];
            builder.Add(new R((51000000 + i).ToString(), $"Pessoa Sintética {i:0000}", HrXlsxBuilder.Cpf(1000 + i),
                Status: i % 50 == 0 ? "Afastado" : "Ativo", JobCode: $"5000{i % 40:0000}", JobName: $"Cargo {i % 40}",
                DeptCode: $"6000{i % 120:0000}", DeptName: $"Setor {i % 120}", ManagementUnit: mu, Group: i % 2 == 0 ? "Campo" : "Indústria",
                Team: $"T{i % 6}"));
        }
        var file = builder.Build();
        _output.WriteLine($"Arquivo sintético: {file.Length / 1024} KB, 6000 linhas x 72 colunas");

        var watch = Stopwatch.StartNew();
        var preview = await EmployeeImportTests.PreviewAsync(admin, file);
        var previewTime = watch.Elapsed;
        preview.Summary.Created.Should().Be(6000);
        preview.Errors.Should().BeEmpty();

        watch.Restart();
        var confirm = await EmployeeImportTests.ConfirmAsync(admin, preview.BatchId, file);
        var confirmTime = watch.Elapsed;
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());

        // Segunda importação (tudo sem mudança): também precisa ser rápida.
        watch.Restart();
        var again = await EmployeeImportTests.PreviewAsync(admin, file);
        var secondPreviewTime = watch.Elapsed;
        again.Summary.Unchanged.Should().Be(6000);

        _output.WriteLine($"Pré-visualização (6000 novos): {previewTime.TotalSeconds:0.00} s");
        _output.WriteLine($"Confirmação (6000 criações): {confirmTime.TotalSeconds:0.00} s");
        _output.WriteLine($"Pré-visualização (6000 sem mudança): {secondPreviewTime.TotalSeconds:0.00} s");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sigh-import-timings.txt"),
            $"preview={previewTime.TotalSeconds:0.00};confirm={confirmTime.TotalSeconds:0.00};preview2={secondPreviewTime.TotalSeconds:0.00}");

        previewTime.Should().BeLessThan(TimeSpan.FromSeconds(15));
        confirmTime.Should().BeLessThan(TimeSpan.FromSeconds(15));
        secondPreviewTime.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }
}
