using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;
using SIGH.Domain.Repositories;

namespace SIGH.Application.Users.GetUserById;

public class GetUserByIdService : IGetUserByIdService
{
    private readonly IUserRepository _userRepository;
    private readonly IApplicationDbContext? _db;
    private readonly ICompanyContext? _companies;

    public GetUserByIdService(IUserRepository userRepository, IApplicationDbContext? db = null, ICompanyContext? companies = null)
    {
        _userRepository = userRepository;
        _db = db;
        _companies = companies;
    }

    public async Task<UserDetailDto> GetUserByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdWithRolesAndPermissionsAsync(id, cancellationToken);

        if (user == null)
        {
            throw new KeyNotFoundException($"Usuário com o ID {id} não foi encontrado.");
        }

        // Fora das empresas do administrador o usuário "não existe"; os perfis de outra empresa nunca aparecem.
        List<Guid>? companyIds = null;
        if (_db is not null && _companies is not null)
        {
            companyIds = (await _companies.GetRequestGrantsAsync(cancellationToken)).Select(g => g.CompanyId).ToList();
            if (!await SIGH.Application.Users.Access.UserCompanyScope.IsVisibleAsync(_db, id, companyIds, cancellationToken))
                throw new KeyNotFoundException($"Usuário com o ID {id} não foi encontrado.");
        }
        bool InScope(SIGH.Domain.Entities.UserRole ur) => companyIds is null || companyIds.Contains(ur.CompanyId);

        var roles = user.UserRoles
            .Where(ur => !ur.IsDeleted && ur.Role != null && !ur.Role.IsDeleted && InScope(ur))
            .Select(ur => ur.Role.Name)
            .Distinct()
            .ToList();

        var permissions = user.UserRoles
            .Where(ur => !ur.IsDeleted && ur.Role != null && !ur.Role.IsDeleted && InScope(ur))
            .SelectMany(ur => ur.Role.RolePermissions)
            .Where(rp => !rp.IsDeleted && rp.Permission != null && !rp.Permission.IsDeleted)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToList();

        return new UserDetailDto(
            user.Id,
            user.FullName,
            user.Email,
            user.Cpf,
            user.Status.ToString(),
            user.MustChangePassword,
            user.FailedLoginAttempts,
            user.LockoutEnd,
            user.LastLoginAt,
            roles,
            permissions,
            user.CreatedAt,
            user.UpdatedAt
        );
    }
}
