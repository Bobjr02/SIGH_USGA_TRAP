using Microsoft.EntityFrameworkCore;
using SIGH.Application.Audit;
using SIGH.Application.Common.Models;
using SIGH.Persistence.Context;

namespace SIGH.Persistence.Repositories;

public sealed class AuditLogQueryService : IAuditLogQueryService
{
    private readonly SighDbContext _context;
    private readonly SIGH.Application.Common.Authorization.IAccessScope? _scope;

    public AuditLogQueryService(SighDbContext context, SIGH.Application.Common.Authorization.IAccessScope? scope = null)
    {
        _context = context;
        _scope = scope;
    }

    public async Task<PagedResult<AuditLogDto>> SearchAsync(AuditLogFilter filter, CancellationToken cancellationToken = default)
    {
        var query = _context.AuditLogs.AsNoTracking();

        // A trilha geral mistura dados de todas as gerências: só as empresas em que o usuário tem escopo total
        // (e Audit.View, exigida pela rota). Eventos sem empresa (login, por exemplo) só para quem tem escopo
        // total em TODAS as empresas ativas. A trilha de UM processo usa o escopo do processo.
        if (_scope is not null)
        {
            var (companies, includeWithoutCompany) = await VisibleCompaniesAsync(cancellationToken);
            query = query.Where(a => (a.CompanyId != null && companies.Contains(a.CompanyId.Value))
                                     || (includeWithoutCompany && a.CompanyId == null));
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityName))
            query = query.Where(a => a.Entity == filter.EntityName.Trim());
        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            query = query.Where(a => a.EntityId == filter.EntityId.Trim());
        if (filter.UserId.HasValue)
            query = query.Where(a => a.UserId == filter.UserId);
        if (!string.IsNullOrWhiteSpace(filter.Action))
            query = query.Where(a => a.Action == filter.Action.Trim());
        if (filter.From.HasValue)
            query = query.Where(a => a.CreatedAt >= filter.From.Value);
        if (filter.To.HasValue)
            query = query.Where(a => a.CreatedAt <= filter.To.Value);

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            var ids = await ResolveReferenceAsync(filter.Reference.Trim(), cancellationToken);
            query = query.Where(a => a.EntityId != null && ids.Contains(a.EntityId));
        }

        return await PageAsync(query, filter.PageNumber, filter.PageSize, cancellationToken);
    }

    /// <summary>
    /// "MED-…": a medida e as subentidades dela (como na linha do tempo). Só dígitos: o funcionário com essa
    /// matrícula. Sem correspondência: lista vazia (nunca "tudo").
    /// </summary>
    private async Task<List<string>> ResolveReferenceAsync(string reference, CancellationToken cancellationToken)
    {
        // A mesma matrícula (ou número) pode existir nas duas empresas: só as das empresas visíveis.
        var companies = _scope is null ? null : (await VisibleCompaniesAsync(cancellationToken)).Companies;
        if (reference.StartsWith("MED-", StringComparison.OrdinalIgnoreCase))
        {
            var upper = reference.ToUpperInvariant();
            var caseIds = await _context.DisciplinaryCases.IgnoreQueryFilters()
                .Where(c => c.CaseNumber == upper && (companies == null || companies.Contains(c.CompanyId)))
                .Select(c => c.Id).ToListAsync(cancellationToken);
            var ids = new List<string>();
            foreach (var id in caseIds)
                ids.AddRange((await CaseEntityIdsAsync(id, cancellationToken)).Select(x => x.ToString()));
            return ids;
        }

        var employeeIds = await _context.Employees.IgnoreQueryFilters()
            .Where(e => e.EmployeeNumber == reference && (companies == null || companies.Contains(e.CompanyId)))
            .Select(e => e.Id).ToListAsync(cancellationToken);
        return employeeIds.Select(x => x.ToString()).ToList();
    }

    /// <summary>Empresas com escopo total na requisição; e se os eventos sem empresa entram (escopo total em todas).</summary>
    private async Task<(List<Guid> Companies, bool IncludeWithoutCompany)> VisibleCompaniesAsync(CancellationToken cancellationToken)
    {
        var filter = await _scope!.GetFilterAsync(cancellationToken);
        var companies = filter.FullCompanyIds.ToList();
        var active = await _context.Companies.AsNoTracking().Where(c => c.IsActive).Select(c => c.Id).ToListAsync(cancellationToken);
        return (companies, companies.Count > 0 && active.All(companies.Contains));
    }

    public async Task<IReadOnlyList<AuditUserOptionDto>> SearchUsersAsync(string? term, CancellationToken cancellationToken = default)
    {
        var query = _context.Users.IgnoreQueryFilters().AsNoTracking();
        if (_scope is not null)
        {
            // Só pessoas ligadas às empresas visíveis (perfil, mesmo retirado, ou conta criada nelas).
            var companies = (await VisibleCompaniesAsync(cancellationToken)).Companies;
            query = query.Where(u => (u.CreatedInCompanyId != null && companies.Contains(u.CreatedInCompanyId.Value))
                                     || _context.UserRoles.IgnoreQueryFilters().Any(ur => ur.UserId == u.Id && companies.Contains(ur.CompanyId)));
        }
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            query = query.Where(u => u.FullName.Contains(t) || u.Email.Contains(t));
        }

        return await query.OrderBy(u => u.FullName).Take(20)
            .Select(u => new AuditUserOptionDto(u.Id, u.FullName, u.Email))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<AuditLogDto>?> GetDisciplinaryCaseTrailAsync(Guid caseId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        var exists = await _context.DisciplinaryCases.IgnoreQueryFilters().AnyAsync(c => c.Id == caseId, cancellationToken);
        if (!exists)
            return null;

        var idStrings = (await CaseEntityIdsAsync(caseId, cancellationToken)).Select(id => id.ToString()).ToList();
        var query = _context.AuditLogs.AsNoTracking().Where(a => a.EntityId != null && idStrings.Contains(a.EntityId));

        return await PageAsync(query, pageNumber, pageSize, cancellationToken);
    }

    /// <summary>O processo e as subentidades dele (inclusive as removidas logicamente).</summary>
    private async Task<List<Guid>> CaseEntityIdsAsync(Guid caseId, CancellationToken cancellationToken)
    {
        // Subentidades removidas logicamente também fazem parte da história do processo.
        var ids = new List<Guid> { caseId };
        ids.AddRange(await _context.DisciplinaryOccurrences.IgnoreQueryFilters().Where(x => x.DisciplinaryCaseId == caseId).Select(x => x.Id).ToListAsync(cancellationToken));
        ids.AddRange(await _context.DisciplinaryCaseEmployees.IgnoreQueryFilters().Where(x => x.DisciplinaryCaseId == caseId).Select(x => x.Id).ToListAsync(cancellationToken));
        ids.AddRange(await _context.DisciplinaryEvidences.IgnoreQueryFilters().Where(x => x.DisciplinaryCaseId == caseId).Select(x => x.Id).ToListAsync(cancellationToken));
        ids.AddRange(await _context.DisciplinaryDecisions.IgnoreQueryFilters().Where(x => x.DisciplinaryCaseId == caseId).Select(x => x.Id).ToListAsync(cancellationToken));
        ids.AddRange(await _context.DisciplinaryMeasures.IgnoreQueryFilters().Where(x => x.DisciplinaryCaseId == caseId).Select(x => x.Id).ToListAsync(cancellationToken));
        return ids;
    }

    private async Task<PagedResult<AuditLogDto>> PageAsync(IQueryable<AuditLog> query, int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 1, IAuditLogQueryService.MaxPageSize);

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditLogDto(
                a.Id,
                a.CreatedAt,
                a.Action,
                a.Entity,
                a.EntityId,
                a.UserId,
                a.User != null ? a.User.FullName : null,
                a.IpAddress,
                a.CorrelationId,
                a.OldValues,
                a.NewValues,
                _context.Companies.Where(c => c.Id == a.CompanyId).Select(c => c.Name).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogDto>(items, pageNumber, pageSize, total);
    }
}
