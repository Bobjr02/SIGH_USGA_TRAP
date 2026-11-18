using SIGH.Domain.Entities;
namespace SIGH.Application.Interfaces;

public interface IJwtTokenGenerator
{
    /// <summary>
    /// Access token com identidade, SecurityStamp e o próximo vencimento de acesso. Perfis e permissões NÃO vão no
    /// token: são POR EMPRESA e lidos do banco a cada requisição (ICompanyContext). <paramref name="roles"/> e
    /// <paramref name="permissions"/> são ignorados (mantidos só por compatibilidade de chamada).
    /// </summary>
    /// <param name="accessChangesAt">Próximo início/fim de vigência de perfil ou gerência (claim <c>acc_chg</c>).</param>
    string GenerateToken(User user, IEnumerable<string> roles, IEnumerable<string> permissions, DateTimeOffset? accessChangesAt = null);
}
