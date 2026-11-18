using Microsoft.EntityFrameworkCore;
using SIGH.Application.Common.Models;
using SIGH.Application.Disciplinary.DTOs;
using SIGH.Application.Disciplinary.InfractionTypes.GetInfractionTypes;
using SIGH.Application.Interfaces.Repositories.Disciplinary;
using SIGH.Domain.Disciplinary.Entities;
using SIGH.Persistence.Context;

namespace SIGH.Persistence.Repositories.Disciplinary;

public class InfractionTypeRepository : IInfractionTypeRepository
{
    private readonly SighDbContext _context;
    private readonly SIGH.Application.Common.Authorization.ICompanyContext? _companies;

    public InfractionTypeRepository(SighDbContext context, SIGH.Application.Common.Authorization.ICompanyContext? companies = null)
    {
        _context = context;
        _companies = companies;
    }

    /// <summary>Motivos visíveis: os das empresas da requisição (cada empresa tem o próprio catálogo).</summary>
    private async Task<IQueryable<InfractionType>> VisibleAsync(IQueryable<InfractionType> source, CancellationToken cancellationToken)
    {
        if (_companies is null)
            return source;
        var ids = (await _companies.GetRequestGrantsAsync(cancellationToken)).Select(g => g.CompanyId).ToList();
        return source.Where(i => ids.Contains(i.CompanyId));
    }

    public async Task<InfractionType?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await (await VisibleAsync(_context.InfractionTypes, cancellationToken))
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<bool> ExistsByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalizedCode = code.Trim().ToUpperInvariant();
        var companyId = _companies is null ? (Guid?)null : await _companies.RequireWriteCompanyAsync(cancellationToken);
        return await _context.InfractionTypes
            .AsNoTracking()
            .AnyAsync(i => i.Code == normalizedCode && (companyId == null || i.CompanyId == companyId), cancellationToken);
    }

    public async Task AddAsync(InfractionType infractionType, CancellationToken cancellationToken = default)
    {
        // Motivo novo nasce na empresa ativa (a do administrador).
        if (_companies is not null)
            infractionType.AssignToCompany(await _companies.RequireWriteCompanyAsync(cancellationToken));
        await _context.InfractionTypes.AddAsync(infractionType, cancellationToken);
    }

    public async Task<PagedResult<InfractionTypeDto>> GetPagedAsync(GetInfractionTypesQuery query, CancellationToken cancellationToken = default)
    {
        var dbQuery = (await VisibleAsync(_context.InfractionTypes.AsNoTracking(), cancellationToken)).AsQueryable();

        if (query.IsActive.HasValue)
        {
            dbQuery = dbQuery.Where(i => i.IsActive == query.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = query.SearchTerm.Trim().ToLower();
            dbQuery = dbQuery.Where(i => i.Name.ToLower().Contains(term) || i.Code.ToLower().Contains(term));
        }

        var totalItems = await dbQuery.CountAsync(cancellationToken);

        var page = query.PageNumber <= 0 ? 1 : query.PageNumber;
        var pageSize = query.PageSize <= 0 ? 10 : query.PageSize;

        var items = await dbQuery
            .OrderBy(i => i.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new InfractionTypeDto(
                i.Id,
                i.Code,
                i.Name,
                i.DefaultSeverity,
                i.RequiresFormalInvestigation,
                i.AllowsTerminationRecommendation,
                i.Description,
                i.LegalReference,
                i.IsActive))
            .ToListAsync(cancellationToken);

        return new PagedResult<InfractionTypeDto>(items, page, pageSize, totalItems);
    }
}
