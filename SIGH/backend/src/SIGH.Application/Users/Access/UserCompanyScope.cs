using Microsoft.EntityFrameworkCore;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;

namespace SIGH.Application.Users.Access;

/// <summary>
/// Quais usuários um administrador enxerga: os que têm (ou tiveram) perfil numa das empresas dele, ou cuja conta
/// foi criada nelas. Fora disso o usuário "não existe" (404) — o e-mail não identifica a empresa.
/// </summary>
public static class UserCompanyScope
{
    public static IQueryable<Guid> VisibleUserIds(IApplicationDbContext db, IReadOnlyCollection<Guid> companyIds)
    {
        var ids = companyIds.ToList();
        // IgnoreQueryFilters: um perfil retirado (exclusão lógica) também conta ("tiveram perfil").
        var byRole = db.UserRoles.IgnoreQueryFilters().Where(ur => ids.Contains(ur.CompanyId)).Select(ur => ur.UserId);
        var byCreation = db.Users.Where(u => u.CreatedInCompanyId != null && ids.Contains(u.CreatedInCompanyId.Value)).Select(u => u.Id);
        return byRole.Union(byCreation);
    }

    public static async Task<bool> IsVisibleAsync(IApplicationDbContext db, Guid userId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken) =>
        companyIds.Count > 0 && await VisibleUserIds(db, companyIds).AnyAsync(id => id == userId, cancellationToken);

    /// <summary>Lança 404 se o usuário não estiver ao alcance das empresas da requisição.</summary>
    public static async Task EnsureVisibleAsync(IApplicationDbContext db, ICompanyContext companies, Guid userId, CancellationToken cancellationToken)
    {
        var companyIds = (await companies.GetRequestGrantsAsync(cancellationToken)).Select(g => g.CompanyId).ToList();
        if (!await IsVisibleAsync(db, userId, companyIds, cancellationToken))
            throw new KeyNotFoundException("Usuário não encontrado.");
    }

    /// <summary>Empresas em que o usuário tem perfil vigente agora.</summary>
    public static Task<List<Guid>> CompaniesWithValidRolesAsync(IApplicationDbContext db, Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.Role.IsDeleted
                         && (ur.ValidFrom == null || ur.ValidFrom <= now)
                         && (ur.ValidUntil == null || ur.ValidUntil > now))
            .Select(ur => ur.CompanyId)
            .Distinct()
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Ação sobre a CONTA inteira (desativar, reativar, desbloquear): só se o administrador tem a permissão em TODAS
    /// as empresas em que a pessoa tem perfil vigente. Com perfil vigente em outra empresa, ele só pode retirar os
    /// perfis da empresa dele.
    /// </summary>
    public static async Task EnsureAccountWideActionAllowedAsync(
        IApplicationDbContext db, ICompanyContext companies, Guid userId, string permission, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var companyId in await CompaniesWithValidRolesAsync(db, userId, now, cancellationToken))
        {
            if (!await companies.HasPermissionAsync(companyId, permission, cancellationToken))
                throw new Domain.Exceptions.BusinessRuleValidationException(UserAccessRules.OtherCompanyAccessMessage);
        }
    }
}
