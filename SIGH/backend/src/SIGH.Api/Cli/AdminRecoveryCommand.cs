using SIGH.Application.Users.Bootstrap;
using SIGH.Domain.Exceptions;

namespace SIGH.Api.Cli;

/// <summary>
/// Recuperação de administrador pelo TI, SÓ na linha de comando do servidor (não há endpoint):
/// <c>dotnet SIGH.Api.dll admin-recover --company TRAP --email pessoa@usga.com.br [--name "Nome"]</c>.
/// Concede o perfil Administrador naquela empresa (mesmo que ela já tenha outro), reativa a conta se estiver
/// inativa e envia o link de definição de senha (o e-mail sai pela outbox, enviada pela API em execução).
/// A auditoria registra a origem "linha de comando".
/// </summary>
public static class AdminRecoveryCommand
{
    public const string Name = "admin-recover";

    public const string Usage =
        "Uso: dotnet SIGH.Api.dll admin-recover --company <código da empresa> --email <e-mail> [--name \"Nome completo\"]";

    public static bool IsCommand(string[] args) => args.Length > 0 && string.Equals(args[0], Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Executa o comando. Devolve o código de saída: 0 sucesso, 1 uso incorreto, 2 recusado.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, out var company, out var email, out var name, out var problem))
        {
            await output.WriteLineAsync(problem);
            await output.WriteLineAsync(Usage);
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var provisioner = scope.ServiceProvider.GetRequiredService<ICompanyAdminProvisioner>();
        try
        {
            var result = await provisioner.RecoverAsync(new CompanyAdminSeed(company!, email!, name), cancellationToken);
            await output.WriteLineAsync(
                $"{result.Email}: {result.Message} na empresa {result.CompanyCode}. " +
                "O link de definição de senha foi enviado por e-mail (vale 48 h e pode ser usado uma única vez).");
            return 0;
        }
        catch (BusinessRuleValidationException ex)
        {
            await output.WriteLineAsync($"Recusado: {ex.Message}");
            return 2;
        }
    }

    private static bool TryParse(string[] args, out string? company, out string? email, out string? name, out string problem)
    {
        company = email = name = null;
        problem = string.Empty;
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                problem = $"Falta o valor de {key}.";
                return false;
            }
            var value = args[++i].Trim();
            switch (key.ToLowerInvariant())
            {
                case "--company": company = value; break;
                case "--email": email = value; break;
                case "--name": name = value; break;
                default:
                    problem = $"Opção desconhecida: {key}.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(email))
        {
            problem = "Informe --company e --email.";
            return false;
        }
        return true;
    }
}
