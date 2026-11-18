using SIGH.Application.Common.Authorization;
using SIGH.Application.Interfaces;

namespace SIGH.Infrastructure.Services;

/// <summary>
/// Adaptador do <see cref="ICompanyContext"/> para os endpoints que recebem um CompanyId (relatórios antigos e
/// notificações). As empresas autorizadas são as da requisição (empresa ativa, ou todas no modo "Todas", com a
/// permissão da rota). Sem empresa pedida: a empresa ativa; no modo "Todas", a primeira empresa da requisição.
/// </summary>
public class AuthorizedCompanyProvider : IAuthorizedCompanyProvider
{
    private readonly ICompanyContext _companies;

    public AuthorizedCompanyProvider(ICompanyContext companies)
    {
        _companies = companies;
    }

    public async Task<IReadOnlyList<Guid>> GetAuthorizedCompanyIdsAsync(CancellationToken cancellationToken = default) =>
        (await _companies.GetRequestGrantsAsync(cancellationToken)).Select(g => g.CompanyId).ToList();

    public async Task<bool> IsCompanyAuthorizedAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty) return false;
        return (await GetAuthorizedCompanyIdsAsync(cancellationToken)).Contains(companyId);
    }

    public async Task<Guid> GetAuthorizedCompanyIdAsync(Guid? requestedCompanyId, CancellationToken cancellationToken = default)
    {
        var authorizedIds = await GetAuthorizedCompanyIdsAsync(cancellationToken);
        if (authorizedIds.Count == 0)
            throw new UnauthorizedAccessException("Usuário não possui empresas autorizadas.");

        if (requestedCompanyId is { } requested && requested != Guid.Empty)
        {
            if (!authorizedIds.Contains(requested))
                throw new UnauthorizedAccessException("Acesso não autorizado à empresa informada.");
            return requested;
        }

        var selection = await _companies.GetSelectionAsync(cancellationToken);
        return selection.CompanyId is { } active && authorizedIds.Contains(active) ? active : authorizedIds[0];
    }
}
