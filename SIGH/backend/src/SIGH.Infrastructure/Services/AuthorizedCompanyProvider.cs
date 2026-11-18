using SIGH.Application.Common.Authorization;
using SIGH.Application.Interfaces;

namespace SIGH.Infrastructure.Services;

/// <summary>
/// Adaptador do <see cref="ICompanyContext"/> para os endpoints que aceitam um <c>companyId</c> opcional. As empresas
/// autorizadas são as da requisição (empresa ativa, ou todas no modo "Todas", com a permissão da rota). Empresa pedida
/// fora delas é 404 (<see cref="CompanyNotAccessibleException"/>); sem empresa pedida valem todas as da requisição.
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

    public async Task<IReadOnlyList<Guid>> ResolveCompanyIdsAsync(Guid? requestedCompanyId, CancellationToken cancellationToken = default)
    {
        var authorizedIds = await GetAuthorizedCompanyIdsAsync(cancellationToken);
        if (authorizedIds.Count == 0)
            throw new CompanyNotAccessibleException();

        if (requestedCompanyId is { } requested && requested != Guid.Empty)
        {
            if (!authorizedIds.Contains(requested))
                throw new CompanyNotAccessibleException();
            return new[] { requested };
        }

        return authorizedIds;
    }
}
