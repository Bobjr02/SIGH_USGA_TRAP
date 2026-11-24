using SIGH.Domain.Employees.Helpers;

namespace SIGH.Application.Users.Bootstrap;

/// <summary>
/// "CPF técnico" das contas de administrador criadas pelo seed (Seed:CompanyAdmins) ou pelo admin-recover: a conta
/// exige um CPF, mas ela não é de uma pessoa cadastrada pelo RH. É sempre 000000000 + 2 dígitos (00000000000,
/// 00000000002…00000000099): nenhum passa na validação de CPF real (<see cref="CpfValidator"/>), cada conta recebe um
/// diferente (o índice de Users.Cpf é único) e o valor nunca é mostrado: a API devolve vazio no lugar dele, a busca por
/// CPF não o encontra e o cadastro manual de usuário não aceita um CPF desta faixa.
/// </summary>
public static class TechnicalCpf
{
    public const string Prefix = "000000000";

    /// <summary>Candidatos, na ordem de uso. 00000000001 fica de fora: já foi usado por contas de teste antigas.</summary>
    public static IEnumerable<string> Candidates()
    {
        for (var i = 0; i < 100; i++)
            if (i != 1)
                yield return Prefix + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>O CPF é da faixa técnica (e não de uma pessoa)?</summary>
    public static bool IsTechnical(string? cpf) =>
        cpf is { Length: 11 } && cpf.StartsWith(Prefix, StringComparison.Ordinal) && char.IsAsciiDigit(cpf[9]) && char.IsAsciiDigit(cpf[10]);

    /// <summary>O CPF como a API o devolve: vazio quando é técnico.</summary>
    public static string ForDisplay(string cpf) => IsTechnical(cpf) ? string.Empty : cpf;
}
