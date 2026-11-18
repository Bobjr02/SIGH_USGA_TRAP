using SIGH.Application.Common.Authorization;
using SIGH.Application.Interfaces;

namespace SIGH.Persistence.Repositories;

/// <summary>
/// Escopo do usuário da requisição, calculado a partir do <see cref="ICompanyContext"/> (perfis vigentes por empresa,
/// lidos do banco). Sem usuário autenticado o escopo é vazio (nada visível). Resolvido uma vez por requisição.
/// </summary>
public sealed class AccessScope : IAccessScope
{
    private readonly ICurrentUserService _currentUser;
    private readonly ICompanyContext _companies;
    private AccessScopeFilter? _filter;

    public AccessScope(ICurrentUserService currentUser, ICompanyContext companies)
    {
        _currentUser = currentUser;
        _companies = companies;
    }

    public bool AppliesToRequest => _currentUser.IsAuthenticated;

    public async Task<AccessScopeFilter> GetFilterAsync(CancellationToken cancellationToken = default)
    {
        if (_filter is not null)
            return _filter;

        if (!_currentUser.IsAuthenticated)
            return _filter = AccessScopeFilter.Nothing;

        var grants = await _companies.GetRequestGrantsAsync(cancellationToken);
        var full = grants.Where(g => g.Has(PermissionCodes.DisciplinaryRecords.ViewAll)).Select(g => g.CompanyId).ToList();
        var units = grants.Where(g => !g.Has(PermissionCodes.DisciplinaryRecords.ViewAll)).SelectMany(g => g.ManagementUnitIds).Distinct().ToList();
        return _filter = new AccessScopeFilter(false, full, units);
    }

    public async Task<IReadOnlyList<Guid>> GetCompanyIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated)
            return Array.Empty<Guid>();
        return (await _companies.GetRequestGrantsAsync(cancellationToken)).Select(g => g.CompanyId).ToList();
    }
}
