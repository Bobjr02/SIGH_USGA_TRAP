using SIGH.Application.Authentication.Common;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Interfaces;
using SIGH.Domain.Enums;
using SIGH.Domain.Repositories;

namespace SIGH.Application.Authentication.CurrentUser;

public class GetCurrentUserService : IGetCurrentUserService
{
    private readonly IUserRepository _userRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IApplicationDbContext? _db;

    public GetCurrentUserService(IUserRepository userRepository, IDateTimeProvider dateTimeProvider, IApplicationDbContext? db = null)
    {
        _userRepository = userRepository;
        _dateTimeProvider = dateTimeProvider;
        _db = db;
    }

    public async Task<CurrentUserResponse> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdWithRolesAndPermissionsAsync(userId, cancellationToken);

        // Usuário removido ou desativado depois da emissão do token: a sessão não vale mais.
        if (user is null || user.Status is UserStatus.Inactive or UserStatus.Suspended)
        {
            throw new UnauthorizedAccessException("Sessão inválida: usuário não encontrado ou inativo.");
        }

        var now = _dateTimeProvider.UtcNow;
        IReadOnlyList<CurrentUserCompanyDto>? companies = null;
        if (_db is not null)
        {
            var grants = await CompanyContext.LoadGrantsAsync(_db, userId, now, cancellationToken);
            companies = grants
                .Select(g => new CurrentUserCompanyDto(
                    g.CompanyId, g.Company.Code, g.Company.Name, g.Company.ManagementUnitPrefix,
                    g.Roles, g.Permissions.OrderBy(p => p, StringComparer.Ordinal).ToList(), g.ManagementUnitIds))
                .ToList();
        }

        return new CurrentUserResponse(
            user.Id,
            user.FullName,
            user.Email,
            user.Status.ToString(),
            user.MustChangePassword,
            user.GetActiveRoleNames(now),
            user.GetActivePermissionCodes(now),
            companies);
    }
}
