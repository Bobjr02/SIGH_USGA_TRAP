using System.Security;
using Microsoft.EntityFrameworkCore;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Interfaces;
using SIGH.Application.Security;
using SIGH.Domain.Entities;
using SIGH.Domain.Enums;
using SIGH.Domain.Exceptions;

namespace SIGH.Application.Users.Access;

/// <summary>Estado da vigência de um vínculo no momento da consulta.</summary>
public enum ValidityState
{
    Active = 1,
    /// <summary>Ainda vai começar (ValidFrom no futuro).</summary>
    Scheduled = 2,
    /// <summary>Venceu (ValidUntil no passado).</summary>
    Expired = 3
}

public sealed record RoleOptionDto(Guid Id, string Name, string Label, string Description, IReadOnlyList<string> Permissions);

public sealed record UserRoleDto(
    Guid Id, Guid RoleId, string RoleName, string RoleLabel,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, ValidityState State,
    Guid CompanyId = default, string? CompanyCode = null, string? CompanyName = null);

public sealed record UserManagementUnitDto(
    Guid Id, Guid ManagementUnitId, string? Code, string Name,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, ValidityState State,
    Guid CompanyId = default, string? CompanyName = null);

/// <summary>
/// Acesso de um usuário NAS EMPRESAS DA REQUISIÇÃO (a do administrador): perfis e gerências de outra empresa nunca
/// aparecem. <see cref="HasAccessInOtherCompany"/> só diz se existe perfil vigente fora delas (para explicar por que
/// a conta não pode ser desativada), sem dizer qual.
/// </summary>
public sealed record UserAccessDto(
    Guid UserId, IReadOnlyList<UserRoleDto> Roles, IReadOnlyList<UserManagementUnitDto> ManagementUnits,
    bool HasAccessInOtherCompany = false);

public sealed record AssignRoleRequest(Guid RoleId, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil);

public sealed record AssignManagementUnitRequest(Guid ManagementUnitId, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil);

/// <summary>Vincula uma conta existente (de outra empresa do grupo) à empresa ativa, com perfis nela.</summary>
public sealed record LinkExistingUserRequest(string Email, List<Guid> RoleIds, List<Guid>? ManagementUnitIds = null);

/// <summary>Só o que a empresa do administrador pode ver: nome, e-mail e o acesso NESTA empresa.</summary>
public sealed record LinkExistingUserResponse(Guid UserId, string FullName, string Email, UserAccessDto Access);

public sealed record ManagementUnitDto(Guid Id, string? Code, string Name, bool IsActive, Guid CompanyId = default, string? CompanyName = null);

public sealed record RenameManagementUnitRequest(string Name);

public interface IUserAccessAdministrationService
{
    Task<IReadOnlyList<RoleOptionDto>> GetRolesAsync(CancellationToken cancellationToken = default);
    Task<UserAccessDto> GetUserAccessAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<UserAccessDto> AssignRoleAsync(Guid actorUserId, Guid userId, AssignRoleRequest request, CancellationToken cancellationToken = default);
    Task<UserAccessDto> RevokeRoleAsync(Guid actorUserId, Guid userId, Guid userRoleId, CancellationToken cancellationToken = default);
    Task<UserAccessDto> AssignManagementUnitAsync(Guid actorUserId, Guid userId, AssignManagementUnitRequest request, CancellationToken cancellationToken = default);
    Task<UserAccessDto> RevokeManagementUnitAsync(Guid actorUserId, Guid userId, Guid userManagementUnitId, CancellationToken cancellationToken = default);
    Task<LinkExistingUserResponse> LinkExistingUserAsync(Guid actorUserId, LinkExistingUserRequest request, CancellationToken cancellationToken = default);
}

public interface IManagementUnitAdministrationService
{
    Task<IReadOnlyList<ManagementUnitDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ManagementUnitDto> RenameAsync(Guid id, RenameManagementUnitRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Regras de atribuição que valem para qualquer caminho (perfis, gerências e status do usuário):
/// ninguém altera o próprio acesso; nunca fica o sistema sem um Administrador ativo.
/// </summary>
public static class UserAccessRules
{
    public const string SelfChangeMessage = "Ninguém pode alterar os próprios perfis, gerências ou status. Peça a outro administrador.";
    public const string LastAdministratorMessage = "Não é possível retirar ou desativar o último Administrador ativo.";
    public const string ManagerWithoutUnitMessage = "O perfil Gestor exige pelo menos uma gerência desta empresa vinculada ao usuário.";
    public const string LastCompanyAdministratorMessage = "Não é possível retirar ou desativar o último Administrador ativo desta empresa.";
    public const string OtherCompanyAccessMessage =
        "Esta pessoa também tem acesso a outra empresa. Retire os perfis desta empresa em vez de desativar ou desbloquear a conta: ela perde o acesso só a esta empresa.";

    public static void EnsureNotSelf(Guid actorUserId, Guid targetUserId)
    {
        if (actorUserId == targetUserId)
            throw new SecurityException(SelfChangeMessage);
    }

    /// <summary>Administradores ativos DA EMPRESA: status Ativo e perfil Administrator vigente agora nela.</summary>
    public static IQueryable<Guid> ActiveAdministratorIds(IApplicationDbContext db, DateTimeOffset now, Guid companyId) =>
        db.UserRoles
            .Where(ur => ur.CompanyId == companyId && ur.Role.Name == SystemRoles.Administrator && !ur.Role.IsDeleted
                         && (ur.ValidFrom == null || ur.ValidFrom <= now)
                         && (ur.ValidUntil == null || ur.ValidUntil > now)
                         && ur.User.Status == UserStatus.Active && !ur.User.IsDeleted)
            .Select(ur => ur.UserId)
            .Distinct();

    /// <summary>
    /// Administradores VIGENTES da empresa: perfil Administrator vigente agora e conta utilizável (ativa, aguardando o
    /// primeiro acesso ou bloqueada temporariamente; não inativa nem suspensa). Usado pelo primeiro administrador da
    /// publicação (Seed:CompanyAdmins) e pelo alerta da tela de usuários.
    /// </summary>
    public static IQueryable<UserRole> CurrentAdministratorAssignments(IApplicationDbContext db, DateTimeOffset now, Guid companyId) =>
        db.UserRoles
            .Where(ur => ur.CompanyId == companyId && ur.Role.Name == SystemRoles.Administrator && !ur.Role.IsDeleted
                         && (ur.ValidFrom == null || ur.ValidFrom <= now)
                         && (ur.ValidUntil == null || ur.ValidUntil > now)
                         && !ur.User.IsDeleted
                         && ur.User.Status != UserStatus.Inactive && ur.User.Status != UserStatus.Suspended);

    /// <summary>Lança se <paramref name="targetUserId"/> for o único Administrador ativo da empresa.</summary>
    public static async Task EnsureNotLastActiveAdministratorAsync(IApplicationDbContext db, Guid targetUserId, DateTimeOffset now, Guid companyId, CancellationToken cancellationToken)
    {
        var admins = await ActiveAdministratorIds(db, now, companyId).ToListAsync(cancellationToken);
        if (admins.Contains(targetUserId) && admins.Count == 1)
            throw new BusinessRuleValidationException(LastCompanyAdministratorMessage);
    }

    /// <summary>A conta inteira vai perder o acesso: não pode ser o último administrador de NENHUMA empresa.</summary>
    public static async Task EnsureNotLastActiveAdministratorAnywhereAsync(IApplicationDbContext db, Guid targetUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var companies = await db.UserRoles
            .Where(ur => ur.UserId == targetUserId && ur.Role.Name == SystemRoles.Administrator)
            .Select(ur => ur.CompanyId).Distinct().ToListAsync(cancellationToken);
        foreach (var companyId in companies)
            await EnsureNotLastActiveAdministratorAsync(db, targetUserId, now, companyId, cancellationToken);
    }

    public static ValidityState StateAt(IHasValidity item, DateTimeOffset now) =>
        item.ValidFrom is { } from && from > now ? ValidityState.Scheduled
        : item.IsExpiredAt(now) ? ValidityState.Expired
        : ValidityState.Active;
}

public sealed class UserAccessAdministrationService : IUserAccessAdministrationService
{
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly ISecurityAuditLogger _securityAudit;
    private readonly ICompanyContext _companies;

    public UserAccessAdministrationService(IApplicationDbContext db, IDateTimeProvider clock, ISecurityAuditLogger securityAudit, ICompanyContext companies)
    {
        _db = db;
        _clock = clock;
        _securityAudit = securityAudit;
        _companies = companies;
    }

    /// <summary>Empresa em que o administrador está mexendo (a ativa) e garante que ele enxerga o usuário nela.</summary>
    private async Task<Guid> WriteCompanyForAsync(Guid userId, CancellationToken cancellationToken)
    {
        var companyId = await _companies.RequireWriteCompanyAsync(cancellationToken);
        if (!await UserCompanyScope.IsVisibleAsync(_db, userId, [companyId], cancellationToken))
            throw new KeyNotFoundException("Usuário não encontrado.");
        return companyId;
    }

    public async Task<IReadOnlyList<RoleOptionDto>> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        var roles = await _db.Roles.AsNoTracking().ToListAsync(cancellationToken);
        // Só os perfis de sistema, na ordem da definição.
        return SystemRoles.All
            .Select(def => (def, role: roles.FirstOrDefault(r => r.Name == def.Name)))
            .Where(x => x.role is not null)
            .Select(x => new RoleOptionDto(x.role!.Id, x.def.Name, x.def.Label, x.def.Description, x.def.Permissions))
            .ToList();
    }

    public async Task<UserAccessDto> GetUserAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await EnsureUserExistsAsync(userId, cancellationToken);
        var companyIds = (await _companies.GetRequestGrantsAsync(cancellationToken)).Select(g => g.CompanyId).ToList();
        if (!await UserCompanyScope.IsVisibleAsync(_db, userId, companyIds, cancellationToken))
            throw new KeyNotFoundException("Usuário não encontrado.");
        return await AccessInAsync(userId, companyIds, cancellationToken);
    }

    /// <summary>Perfis e gerências do usuário só nas empresas informadas (nunca os de outra empresa).</summary>
    private async Task<UserAccessDto> AccessInAsync(Guid userId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var ids = companyIds.ToList();

        var roles = await _db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && ids.Contains(ur.CompanyId))
            .Join(_db.Companies, ur => ur.CompanyId, c => c.Id, (ur, c) => new { ur.Id, ur.RoleId, ur.Role.Name, ur.ValidFrom, ur.ValidUntil, ur.CompanyId, c.Code, CompanyName = c.Name })
            .ToListAsync(cancellationToken);

        var units = await _db.UserManagementUnits.AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.ManagementUnit.CompanyId))
            .Join(_db.Companies, x => x.ManagementUnit.CompanyId, c => c.Id, (x, c) => new
            {
                x.Id, x.ManagementUnitId, x.ManagementUnit.Code, x.ManagementUnit.Name, x.ValidFrom, x.ValidUntil, CompanyId = c.Id, CompanyName = c.Name
            })
            .ToListAsync(cancellationToken);

        var elsewhere = (await UserCompanyScope.CompaniesWithValidRolesAsync(_db, userId, now, cancellationToken)).Any(id => !ids.Contains(id));

        return new UserAccessDto(
            userId,
            roles
                .Select(r => new UserRoleDto(r.Id, r.RoleId, r.Name, SystemRoles.Find(r.Name)?.Label ?? r.Name, r.ValidFrom, r.ValidUntil,
                    UserAccessRules.StateAt(new Validity(r.ValidFrom, r.ValidUntil), now), r.CompanyId, r.Code, r.CompanyName))
                .OrderBy(r => r.CompanyCode, StringComparer.Ordinal)
                .ThenBy(r => r.RoleLabel, StringComparer.CurrentCulture)
                .ToList(),
            units
                .Select(u => new UserManagementUnitDto(u.Id, u.ManagementUnitId, u.Code, u.Name, u.ValidFrom, u.ValidUntil,
                    UserAccessRules.StateAt(new Validity(u.ValidFrom, u.ValidUntil), now), u.CompanyId, u.CompanyName))
                .OrderBy(u => u.Code, SIGH.Domain.Employees.ManagementUnitCodes.Comparer)
                .ToList(),
            elsewhere);
    }

    public async Task<UserAccessDto> AssignRoleAsync(Guid actorUserId, Guid userId, AssignRoleRequest request, CancellationToken cancellationToken = default)
    {
        UserAccessRules.EnsureNotSelf(actorUserId, userId);
        ValidityExtensions.EnsureValidityRange(request.ValidFrom, request.ValidUntil);
        var now = _clock.UtcNow;
        var companyId = await WriteCompanyForAsync(userId, cancellationToken);
        var user = await LoadUserAsync(userId, cancellationToken);
        return await AssignRoleInAsync(actorUserId, user, companyId, request, now, cancellationToken);
    }

    /// <summary>Atribui o perfil NA EMPRESA informada (a do administrador). Também usado pelo vínculo de usuário existente.</summary>
    public async Task<UserAccessDto> AssignRoleInAsync(Guid actorUserId, User user, Guid companyId, AssignRoleRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var userId = user.Id;
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == request.RoleId, cancellationToken)
                   ?? throw new BusinessRuleValidationException("Perfil não encontrado.");
        if (SystemRoles.Find(role.Name) is null)
            throw new BusinessRuleValidationException("Só os perfis de sistema podem ser atribuídos.");

        if (role.Name == SystemRoles.Manager && !await HasUsableManagementUnitAsync(userId, companyId, now, exceptLinkId: null, cancellationToken))
            throw new BusinessRuleValidationException(UserAccessRules.ManagerWithoutUnitMessage);

        var link = await _db.UserRoles.FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == role.Id && ur.CompanyId == companyId, cancellationToken);
        if (link is null)
        {
            link = new UserRole { UserId = userId, RoleId = role.Id, CompanyId = companyId, ValidFrom = request.ValidFrom, ValidUntil = request.ValidUntil };
            await _db.UserRoles.AddAsync(link, cancellationToken);
        }
        else
        {
            // Mesmo perfil de novo: só a validade muda. Limitar a validade do último administrador ativo seria retirá-lo.
            var stillValidNow = new Validity(request.ValidFrom, request.ValidUntil).IsValidAt(now);
            if (role.Name == SystemRoles.Administrator && !stillValidNow)
                await UserAccessRules.EnsureNotLastActiveAdministratorAsync(_db, userId, now, companyId, cancellationToken);
            link.ValidFrom = request.ValidFrom;
            link.ValidUntil = request.ValidUntil;
        }

        user.RotateSecurityStamp();
        await _db.SaveChangesAsync(cancellationToken);
        await _securityAudit.RecordAsync(SecurityAuditActions.RoleAssigned, actorUserId,
            new { targetUserId = userId, role = role.Name, companyId, validFrom = request.ValidFrom, validUntil = request.ValidUntil },
            cancellationToken, nameof(UserRole), link.Id.ToString());

        return await AccessInAsync(userId, [companyId], cancellationToken);
    }

    public async Task<UserAccessDto> RevokeRoleAsync(Guid actorUserId, Guid userId, Guid userRoleId, CancellationToken cancellationToken = default)
    {
        UserAccessRules.EnsureNotSelf(actorUserId, userId);
        var now = _clock.UtcNow;
        var companyId = await WriteCompanyForAsync(userId, cancellationToken);
        var user = await LoadUserAsync(userId, cancellationToken);

        // Só os perfis da empresa do administrador: o de outra empresa "não existe".
        var link = await _db.UserRoles.Include(ur => ur.Role)
                       .FirstOrDefaultAsync(ur => ur.Id == userRoleId && ur.UserId == userId && ur.CompanyId == companyId, cancellationToken)
                   ?? throw new KeyNotFoundException("Vínculo de perfil não encontrado.");

        if (link.Role.Name == SystemRoles.Administrator)
            await UserAccessRules.EnsureNotLastActiveAdministratorAsync(_db, userId, now, companyId, cancellationToken);

        link.IsDeleted = true;
        link.DeletedAt = now;
        link.DeletedBy = actorUserId;
        user.RotateSecurityStamp();
        await _db.SaveChangesAsync(cancellationToken);
        await _securityAudit.RecordAsync(SecurityAuditActions.RoleRevoked, actorUserId,
            new { targetUserId = userId, role = link.Role.Name, companyId, validFrom = link.ValidFrom, validUntil = link.ValidUntil },
            cancellationToken, nameof(UserRole), link.Id.ToString());

        return await AccessInAsync(userId, [companyId], cancellationToken);
    }

    public async Task<UserAccessDto> AssignManagementUnitAsync(Guid actorUserId, Guid userId, AssignManagementUnitRequest request, CancellationToken cancellationToken = default)
    {
        UserAccessRules.EnsureNotSelf(actorUserId, userId);
        ValidityExtensions.EnsureValidityRange(request.ValidFrom, request.ValidUntil);
        var now = _clock.UtcNow;
        var companyId = await WriteCompanyForAsync(userId, cancellationToken);
        var user = await LoadUserAsync(userId, cancellationToken);

        // A gerência tem de ser da empresa do administrador.
        var unit = await _db.ManagementUnits.FirstOrDefaultAsync(m => m.Id == request.ManagementUnitId && m.CompanyId == companyId, cancellationToken)
                   ?? throw new BusinessRuleValidationException("Gerência não encontrada.");

        var link = await _db.UserManagementUnits.FirstOrDefaultAsync(x => x.UserId == userId && x.ManagementUnitId == unit.Id, cancellationToken);
        if (link is null)
        {
            link = new UserManagementUnit { UserId = userId, ManagementUnitId = unit.Id, ValidFrom = request.ValidFrom, ValidUntil = request.ValidUntil };
            await _db.UserManagementUnits.AddAsync(link, cancellationToken);
        }
        else
        {
            if (new Validity(request.ValidFrom, request.ValidUntil).IsExpiredAt(now))
                await EnsureManagerKeepsAUnitAsync(userId, companyId, now, link.Id, cancellationToken);
            link.ValidFrom = request.ValidFrom;
            link.ValidUntil = request.ValidUntil;
        }

        user.RotateSecurityStamp();
        await _db.SaveChangesAsync(cancellationToken);
        await _securityAudit.RecordAsync(SecurityAuditActions.ManagementUnitAssigned, actorUserId,
            new { targetUserId = userId, managementUnit = unit.Code, companyId, validFrom = request.ValidFrom, validUntil = request.ValidUntil },
            cancellationToken, nameof(UserManagementUnit), link.Id.ToString());

        return await AccessInAsync(userId, [companyId], cancellationToken);
    }

    public async Task<UserAccessDto> RevokeManagementUnitAsync(Guid actorUserId, Guid userId, Guid userManagementUnitId, CancellationToken cancellationToken = default)
    {
        UserAccessRules.EnsureNotSelf(actorUserId, userId);
        var now = _clock.UtcNow;
        var companyId = await WriteCompanyForAsync(userId, cancellationToken);
        var user = await LoadUserAsync(userId, cancellationToken);

        var link = await _db.UserManagementUnits.Include(x => x.ManagementUnit)
                       .FirstOrDefaultAsync(x => x.Id == userManagementUnitId && x.UserId == userId && x.ManagementUnit.CompanyId == companyId, cancellationToken)
                   ?? throw new KeyNotFoundException("Vínculo de gerência não encontrado.");

        await EnsureManagerKeepsAUnitAsync(userId, companyId, now, link.Id, cancellationToken);

        link.IsDeleted = true;
        link.DeletedAt = now;
        link.DeletedBy = actorUserId;
        user.RotateSecurityStamp();
        await _db.SaveChangesAsync(cancellationToken);
        await _securityAudit.RecordAsync(SecurityAuditActions.ManagementUnitRevoked, actorUserId,
            new { targetUserId = userId, managementUnit = link.ManagementUnit.Code, companyId, validFrom = link.ValidFrom, validUntil = link.ValidUntil },
            cancellationToken, nameof(UserManagementUnit), link.Id.ToString());

        return await AccessInAsync(userId, [companyId], cancellationToken);
    }

    public async Task<LinkExistingUserResponse> LinkExistingUserAsync(Guid actorUserId, LinkExistingUserRequest request, CancellationToken cancellationToken = default)
    {
        var companyId = await _companies.RequireWriteCompanyAsync(cancellationToken);
        var email = (request?.Email ?? string.Empty).Trim().ToLowerInvariant();
        var roleIds = (request?.RoleIds ?? []).Distinct().ToList();
        if (roleIds.Count == 0)
            throw new BusinessRuleValidationException("Atribua ao menos um perfil nesta empresa.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken)
                   ?? throw new KeyNotFoundException("Nenhuma conta com este e-mail.");
        UserAccessRules.EnsureNotSelf(actorUserId, user.Id);
        if (user.Status == UserStatus.Inactive)
            throw new BusinessRuleValidationException("A conta com este e-mail está desativada. Peça a reativação ao administrador da outra empresa.");

        var now = _clock.UtcNow;
        var unitIds = (request!.ManagementUnitIds ?? []).Distinct().ToList();
        if (unitIds.Count > 0)
        {
            var units = await _db.ManagementUnits.Where(m => unitIds.Contains(m.Id) && m.CompanyId == companyId).Select(m => m.Id).ToListAsync(cancellationToken);
            if (units.Count != unitIds.Count)
                throw new BusinessRuleValidationException("Uma ou mais gerências informadas não existem nesta empresa.");
            foreach (var unitId in units)
            {
                if (!await _db.UserManagementUnits.AnyAsync(x => x.UserId == user.Id && x.ManagementUnitId == unitId, cancellationToken))
                    await _db.UserManagementUnits.AddAsync(new UserManagementUnit { UserId = user.Id, ManagementUnitId = unitId }, cancellationToken);
            }
            await _db.SaveChangesAsync(cancellationToken);
        }

        UserAccessDto access = await AccessInAsync(user.Id, [companyId], cancellationToken);
        foreach (var roleId in roleIds)
            access = await AssignRoleInAsync(actorUserId, user, companyId, new AssignRoleRequest(roleId, null, null), now, cancellationToken);

        return new LinkExistingUserResponse(user.Id, user.FullName, user.Email, access);
    }

    /// <summary>Quem tem o perfil Gestor (não vencido) NA EMPRESA não pode ficar sem nenhuma gerência (não vencida) dela.</summary>
    private async Task EnsureManagerKeepsAUnitAsync(Guid userId, Guid companyId, DateTimeOffset now, Guid removedLinkId, CancellationToken cancellationToken)
    {
        var isManager = await _db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.CompanyId == companyId && ur.Role.Name == SystemRoles.Manager
                                                          && (ur.ValidUntil == null || ur.ValidUntil > now), cancellationToken);
        if (isManager && !await HasUsableManagementUnitAsync(userId, companyId, now, removedLinkId, cancellationToken))
            throw new BusinessRuleValidationException(UserAccessRules.ManagerWithoutUnitMessage + " Retire antes o perfil Gestor.");
    }

    private Task<bool> HasUsableManagementUnitAsync(Guid userId, Guid companyId, DateTimeOffset now, Guid? exceptLinkId, CancellationToken cancellationToken) =>
        _db.UserManagementUnits.AnyAsync(x => x.UserId == userId && x.Id != exceptLinkId && x.ManagementUnit.CompanyId == companyId
                                              && (x.ValidUntil == null || x.ValidUntil > now), cancellationToken);

    private async Task<User> LoadUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
        ?? throw new KeyNotFoundException("Usuário não encontrado.");

    private async Task EnsureUserExistsAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
            throw new KeyNotFoundException("Usuário não encontrado.");
    }

    private sealed record Validity(DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil) : IHasValidity;
}

public sealed class ManagementUnitAdministrationService : IManagementUnitAdministrationService
{
    private readonly IApplicationDbContext _db;
    private readonly ICompanyContext _companies;

    public ManagementUnitAdministrationService(IApplicationDbContext db, ICompanyContext companies)
    {
        _db = db;
        _companies = companies;
    }

    /// <summary>Gerências das empresas da requisição (a do administrador; todas as dele no modo "Todas").</summary>
    public async Task<IReadOnlyList<ManagementUnitDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var companyIds = (await _companies.GetRequestGrantsAsync(cancellationToken)).Select(g => g.CompanyId).ToList();
        var units = await _db.ManagementUnits.AsNoTracking()
            .Where(m => companyIds.Contains(m.CompanyId))
            .Join(_db.Companies, m => m.CompanyId, c => c.Id, (m, c) => new { m, c })
            .Select(x => new { CompanyCode = x.c.Code, Dto = new ManagementUnitDto(x.m.Id, x.m.Code, x.m.Name, x.m.IsActive, x.c.Id, x.c.Name) })
            .ToListAsync(cancellationToken);
        // Por empresa; dentro dela, a chave sem sufixo primeiro e depois os números (USGA, USGA010, USGA131…).
        return units.OrderBy(x => x.CompanyCode, StringComparer.Ordinal)
            .ThenBy(x => x.Dto.Code, SIGH.Domain.Employees.ManagementUnitCodes.Comparer)
            .Select(x => x.Dto).ToList();
    }

    public async Task<ManagementUnitDto> RenameAsync(Guid id, RenameManagementUnitRequest request, CancellationToken cancellationToken = default)
    {
        var companyId = await _companies.RequireWriteCompanyAsync(cancellationToken);
        var unit = await _db.ManagementUnits.FirstOrDefaultAsync(m => m.Id == id && m.CompanyId == companyId, cancellationToken)
                   ?? throw new KeyNotFoundException("Gerência não encontrada.");
        unit.UpdateName(request.Name ?? string.Empty);
        await _db.SaveChangesAsync(cancellationToken);
        return new ManagementUnitDto(unit.Id, unit.Code, unit.Name, unit.IsActive, unit.CompanyId);
    }
}
