namespace SIGH.Domain.Employees;

/// <summary>
/// Formato da Chave Organização (a "gerência" do SAP). Só dois formatos valem, sempre em maiúsculas e sem espaços:
/// o prefixo da empresa sozinho (USGA, TRAP) ou o prefixo seguido de exatamente 3 dígitos (USGA010, TRAP180).
/// A empresa é definida pelo prefixo (<see cref="Entities.Company.ManagementUnitPrefix"/>).
/// </summary>
public static class ManagementUnitCodes
{
    public const int SuffixLength = 3;

    /// <summary>A chave está num dos dois formatos para o prefixo informado? (TRAP12, TRAP1234, USGATRAP, "TRAP 230" e trap230 não.)</summary>
    public static bool IsValid(string? code, string? prefix) => TryParse(code, prefix, out _);

    /// <summary>
    /// Lê a chave no formato do prefixo. <paramref name="number"/> é o sufixo numérico, ou null na chave sem sufixo (USGA).
    /// </summary>
    public static bool TryParse(string? code, string? prefix, out int? number)
    {
        number = null;
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(prefix) || !IsUpperLetters(prefix)
            || !code.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var suffix = code.AsSpan(prefix.Length);
        if (suffix.Length == 0)
            return true;
        if (suffix.Length != SuffixLength)
            return false;
        foreach (var c in suffix)
            if (!char.IsAsciiDigit(c))
                return false;
        number = int.Parse(suffix, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Texto de ajuda com os dois formatos aceitos ("TRAP ou TRAP seguido de 3 dígitos, como TRAP180").</summary>
    public static string FormatHint(string prefix) => $"{prefix} ou {prefix} seguido de 3 dígitos, como {prefix}131";

    /// <summary>
    /// Ordem de exibição: por prefixo; dentro do prefixo, a chave sem sufixo primeiro e depois os números em ordem
    /// (USGA, USGA010, USGA131…; TRAP, TRAP180…TRAP300). Fora do formato vai depois, em ordem alfabética; nulo por último.
    /// </summary>
    public static int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        var (px, nx) = Split(x);
        var (py, ny) = Split(y);
        var byPrefix = string.CompareOrdinal(px, py);
        if (byPrefix != 0) return byPrefix;

        // Mesmo prefixo: sem sufixo (-1) antes de número (0..999) antes de fora do formato (int.MaxValue).
        var byNumber = nx.CompareTo(ny);
        return byNumber != 0 ? byNumber : string.CompareOrdinal(x, y);
    }

    public static IComparer<string?> Comparer { get; } = Comparer<string?>.Create(Compare);

    private static (string Prefix, int Rank) Split(string code)
    {
        var letters = 0;
        while (letters < code.Length && char.IsAsciiLetterUpper(code[letters])) letters++;
        var prefix = code[..letters];
        if (letters == 0) return (code, int.MaxValue);
        return TryParse(code, prefix, out var number) ? (prefix, number ?? -1) : (prefix, int.MaxValue);
    }

    private static bool IsUpperLetters(string text)
    {
        foreach (var c in text)
            if (!char.IsAsciiLetterUpper(c))
                return false;
        return true;
    }
}
