using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Interfaces;
using SIGH.Application.Users.Access;
using SIGH.Domain.Enums;
using SIGH.Domain.Exceptions;
using SIGH.Domain.Repositories;

namespace SIGH.Application.Users.ChangeStatus;

public class ChangeUserStatusService : IChangeUserStatusService
{
    private readonly IApplicationDbContext _context;
    private readonly IUserRepository _userRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ICurrentUserService? _currentUser;
    private readonly ICompanyContext? _companies;

    public ChangeUserStatusService(
        IApplicationDbContext context,
        IUserRepository userRepository,
        IDateTimeProvider dateTimeProvider,
        ICurrentUserService? currentUser = null,
        ICompanyContext? companies = null)
    {
        _context = context;
        _userRepository = userRepository;
        _dateTimeProvider = dateTimeProvider;
        _currentUser = currentUser;
        _companies = companies;
    }

    public async Task<ChangeUserStatusResponse> ChangeUserStatusAsync(ChangeUserStatusRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(request.UserId, cancellationToken);

        if (user == null)
        {
            throw new BusinessRuleValidationException("Usuário não encontrado.");
        }

        var losesAccess = request.NewStatus is UserStatus.Inactive or UserStatus.Locked or UserStatus.Suspended;

        if (_currentUser?.UserId is { } actorId)
            UserAccessRules.EnsureNotSelf(actorId, user.Id);

        // O status é da CONTA (todas as empresas): o administrador precisa enxergar o usuário na empresa ativa e ter a
        // permissão em todas as empresas em que a pessoa tem perfil vigente; senão só retira os perfis da empresa dele.
        if (_companies is not null)
        {
            var companyId = await _companies.RequireWriteCompanyAsync(cancellationToken);
            if (!await UserCompanyScope.IsVisibleAsync(_context, user.Id, [companyId], cancellationToken))
                throw new KeyNotFoundException("Usuário não encontrado.");
            await UserCompanyScope.EnsureAccountWideActionAllowedAsync(_context, _companies, user.Id,
                SIGH.Application.Common.Authorization.PermissionCodes.Users.ChangeStatus, _dateTimeProvider.UtcNow, cancellationToken);
        }

        if (losesAccess)
            await UserAccessRules.EnsureNotLastActiveAdministratorAnywhereAsync(_context, user.Id, _dateTimeProvider.UtcNow, cancellationToken);

        user.Status = request.NewStatus;

        // Se o novo status inativa/bloqueia o usuário, revoga os tokens e sessões ativas
        // e troca o SecurityStamp: o access token em uso para de valer na próxima requisição.
        if (losesAccess)
        {
            var now = _dateTimeProvider.UtcNow;
            user.RotateSecurityStamp();

            var activeTokens = await _userRepository.GetActiveRefreshTokensByUserIdAsync(user.Id, cancellationToken);

            foreach (var token in activeTokens)
            {
                token.RevokedAt = now;
                token.ReasonRevoked = $"Alteração de status do usuário para {request.NewStatus}: {request.Reason}";
            }

            var activeSessions = await _userRepository.GetActiveUserSessionsByUserIdAsync(user.Id, cancellationToken);

            foreach (var session in activeSessions)
            {
                session.IsRevoked = true;
                session.RevokedAt = now;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new ChangeUserStatusResponse(true, $"Status do usuário alterado com sucesso para {request.NewStatus}.");
    }
}
