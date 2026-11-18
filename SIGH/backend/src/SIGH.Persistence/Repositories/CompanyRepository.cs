using Microsoft.EntityFrameworkCore;
using SIGH.Domain.Employees.Entities;
using SIGH.Domain.Repositories;
using SIGH.Persistence.Context;

namespace SIGH.Persistence.Repositories;

public class CompanyRepository : ICompanyRepository
{
    private readonly SighDbContext _context;
    private readonly SIGH.Application.Common.Authorization.IAccessScope? _scope;

    public CompanyRepository(SighDbContext context, SIGH.Application.Common.Authorization.IAccessScope? scope = null)
    {
        _context = context;
        _scope = scope;
    }

    /// <summary>Empresas da requisição: uma empresa enviada pelo cliente fora delas "não existe".</summary>
    private async Task<IQueryable<Company>> ReachableAsync(CancellationToken cancellationToken)
    {
        IQueryable<Company> companies = _context.Companies;
        if (_scope is null || !_scope.AppliesToRequest)
            return companies;
        var ids = (await _scope.GetCompanyIdsAsync(cancellationToken)).ToList();
        return companies.Where(c => ids.Contains(c.Id));
    }

    public async Task<Company?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await (await ReachableAsync(cancellationToken))
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await (await ReachableAsync(cancellationToken))
            .AsNoTracking()
            .AnyAsync(c => c.Id == id, cancellationToken);
    }
}
