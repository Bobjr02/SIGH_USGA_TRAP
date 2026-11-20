using System.Globalization;

namespace SIGH.Application.Common;

/// <summary>
/// Plural em português nas mensagens que vão para a tela, sem "(s)" nem "(is)": 1 → singular; 0 e 2 ou mais → plural.
/// Mesma regra do front (SIGH/frontend/src/utils/plural.ts).
/// </summary>
public static class PortuguesePlural
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Of(int count, string singular, string plural) => count == 1 ? singular : plural;

    /// <summary>"1 registro novo", "2 registros novos" (número no formato pt-BR: 1.248).</summary>
    public static string Count(int count, string singular, string plural) =>
        $"{count.ToString("N0", PtBr)} {Of(count, singular, plural)}";
}
