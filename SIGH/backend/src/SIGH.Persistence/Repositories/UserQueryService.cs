using Microsoft.EntityFrameworkCore;
using SIGH.Application.Users.GetUsers;
using SIGH.Persistence.Context;

namespace SIGH.Persistence.Repositories;

public class UserQueryService : IUserQueryService
{
    private readonly SighDbContext _context;
    private readonly SIGH.Application.Common.Authorization.ICompanyContext? _companies;

    public UserQueryService(SighDbContext context, SIGH.Application.Common.Authorization.ICompanyContext? companies = null)
    {
        _context = context;
        _companies = companies;
    }

    public async Task<PagedList<UserSummaryDto>> GetUsersPagedAsync(GetUsersQuery query, CancellationToken cancellationToken = default)
    {
        var dbQuery = _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .AsNoTracking()
            .Where(u => !u.IsDeleted);

        // O administrador só vê os usuários das empresas dele (perfil, mesmo retirado, ou conta criada nelas).
        List<Guid>? companyIds = null;
        Dictionary<Guid, string> companyNames;
        if (_companies is not null)
        {
            var grants = await _companies.GetRequestGrantsAsync(cancellationToken);
            companyIds = grants.Select(g => g.CompanyId).ToList();
            companyNames = grants.ToDictionary(g => g.CompanyId, g => g.Company.Name);
            var visible = SIGH.Application.Users.Access.UserCompanyScope.VisibleUserIds(_context, companyIds);
            dbQuery = dbQuery.Where(u => visible.Contains(u.Id));
        }
        else
        {
            companyNames = await _context.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = query.SearchTerm.Trim().ToLower();
            dbQuery = dbQuery.Where(u =>
                u.FullName.ToLower().Contains(term) ||
                u.Email.ToLower().Contains(term) ||
                (u.Cpf.Contains(term) && !u.Cpf.StartsWith(SIGH.Application.Users.Bootstrap.TechnicalCpf.Prefix)));
        }

        if (query.Status.HasValue)
        {
            dbQuery = dbQuery.Where(u => u.Status == query.Status.Value);
        }

        var totalCount = await dbQuery.CountAsync(cancellationToken);

        var pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
        var pageSize = query.PageSize < 1 ? 10 : query.PageSize;

        var users = await dbQuery
            .OrderByDescending(u => u.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var userDtos = users.Select(u => new UserSummaryDto(
            u.Id,
            u.FullName,
            u.Email,
            SIGH.Application.Users.Bootstrap.TechnicalCpf.ForDisplay(u.Cpf),
            u.Status.ToString(),
            u.MustChangePassword,
            u.UserRoles
                .Where(ur => !ur.IsDeleted && ur.Role != null && !ur.Role.IsDeleted && (companyIds == null || companyIds.Contains(ur.CompanyId)))
                .Select(ur => ur.Role.Name)
                .Distinct()
                .ToList(),
            u.CreatedAt,
            u.LastLoginAt,
            u.UserRoles.Where(ur => !ur.IsDeleted).Select(ur => ur.CompanyId)
                .Append(u.CreatedInCompanyId ?? Guid.Empty)
                .Where(companyNames.ContainsKey)
                .Distinct()
                .Select(id => companyNames[id])
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToList()
        )).ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        return new PagedList<UserSummaryDto>(
            userDtos,
            pageNumber,
            pageSize,
            totalCount,
            totalPages
        );
    }
}
