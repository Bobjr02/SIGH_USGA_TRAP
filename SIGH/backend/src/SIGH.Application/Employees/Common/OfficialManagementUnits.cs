using SIGH.Application.Common.Authorization;
using SIGH.Domain.Employees;

namespace SIGH.Application.Employees.Common;

/// <summary>Chave Organização da lista oficial do cliente: código, nome de exibição e empresa (pelo prefixo).</summary>
public sealed record OfficialManagementUnit(string CompanyCode, string Code, string Name);

/// <summary>
/// Lista oficial das chaves organizacionais (as "gerências") enviada pelo cliente: 12 da USGA e 8 da Trapiche. O seed
/// cria ou atualiza exatamente estas (por código), cada uma na empresa do seu prefixo; a importação de funcionários e
/// de histórico só aceita chave desta lista (e ativa no banco).
/// </summary>
public static class OfficialManagementUnits
{
    public static IReadOnlyList<OfficialManagementUnit> All { get; } =
    [
        new(CompanyDefaults.UsgaCode, "USGA", "Serra Grande Escritório"),
        new(CompanyDefaults.UsgaCode, "USGA010", "Superintendência"),
        new(CompanyDefaults.UsgaCode, "USGA131", "Gerência Industrial"),
        new(CompanyDefaults.UsgaCode, "USGA132", "Gerência de Manutenção"),
        new(CompanyDefaults.UsgaCode, "USGA133", "Gerência de Engenharia Civil"),
        new(CompanyDefaults.UsgaCode, "USGA134", "Gerência Automotiva"),
        new(CompanyDefaults.UsgaCode, "USGA135", "Gerência Administrativa"),
        new(CompanyDefaults.UsgaCode, "USGA136", "Gerência de Assistência Social"),
        new(CompanyDefaults.UsgaCode, "USGA137", "Gerência Agrícola"),
        new(CompanyDefaults.UsgaCode, "USGA138", "Gerência de Patrimônio"),
        new(CompanyDefaults.UsgaCode, "USGA141", "Gerência Agrícola Rural"),
        new(CompanyDefaults.UsgaCode, "USGA161", "Escritório Maceió"),

        new(CompanyDefaults.TrapicheCode, "TRAP", "Trapiche Recife"),
        new(CompanyDefaults.TrapicheCode, "TRAP180", "Diretoria"),
        new(CompanyDefaults.TrapicheCode, "TRAP220", "Gerência Administrativa"),
        new(CompanyDefaults.TrapicheCode, "TRAP230", "Gerência Industrial"),
        new(CompanyDefaults.TrapicheCode, "TRAP240", "Gerência Automotiva"),
        new(CompanyDefaults.TrapicheCode, "TRAP250", "Gerência de Relação do Trabalho"),
        new(CompanyDefaults.TrapicheCode, "TRAP260", "Gerência Agrícola"),
        new(CompanyDefaults.TrapicheCode, "TRAP300", "Administração Usina"),
    ];

    private static readonly Dictionary<string, OfficialManagementUnit> ByCode = All.ToDictionary(u => u.Code, StringComparer.Ordinal);

    /// <summary>Chave da lista oficial (comparação exata: a chave é sempre em maiúsculas).</summary>
    public static bool Contains(string? code) => code is not null && ByCode.ContainsKey(code);

    public static OfficialManagementUnit? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var unit) ? unit : null;

    /// <summary>Chaves oficiais da empresa dona do prefixo (USGA, TRAP), na ordem de exibição.</summary>
    public static IReadOnlyList<OfficialManagementUnit> ForPrefix(string? prefix) =>
        All.Where(u => ManagementUnitCodes.IsValid(u.Code, prefix)).OrderBy(u => u.Code, ManagementUnitCodes.Comparer).ToList();

    /// <summary>
    /// Nomes das gerências da USGA antes da lista oficial (seed antigo e GERÊNCIA das planilhas de penalidades:
    /// "AGRÍCOLA URBANO" = USGA137). Continuam reconhecidos na importação do histórico.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LegacyNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Industrial"] = "USGA131",
        ["Manutenção"] = "USGA132",
        ["Engenharia Civil"] = "USGA133",
        ["Automotiva"] = "USGA134",
        ["Administrativo"] = "USGA135",
        ["Assistência Social"] = "USGA136",
        ["Agrícola Urbano"] = "USGA137",
        ["Patrimônio"] = "USGA138",
        ["Agrícola Rurícola"] = "USGA141",
    };
}
