using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Interfaces;
using SIGH.Application.Users.Access;
using SIGH.Domain.Enums;
using SIGH.Domain.Exceptions;
using SIGH.Domain.Repositories;

namespace SIGH.Application.Users.UnlockUser;

public class UnlockUserService : IUnlockUserService
{
    private readonly IApplicationDbContext _context;
    private readonly IUserRepository _userRepository;
    private readonly ICompanyContext? _companies;
    private readonly IDateTimeProvider? _clock;

    public UnlockUserService(IApplicationDbContext context, IUserRepository userRepository, ICompanyContext? companies = null, IDateTimeProvider? clock = null)
    {
        _context = context;
        _userRepository = userRepository;
        _companies = companies;
        _clock = clock;
    }

    public async Task<UnlockUserResponse> UnlockUserAsync(UnlockUserRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(request.UserId, cancellationToken);

        if (user == null)
        {
            throw new BusinessRuleValidationException("Usuário não encontrado.");
        }

        // Mesma regra da desativação: o desbloqueio vale para a conta inteira (todas as empresas).
        if (_companies is not null)
        {
            var companyId = await _companies.RequireWriteCompanyAsync(cancellationToken);
            if (!await UserCompanyScope.IsVisibleAsync(_context, user.Id, [companyId], cancellationToken))
                throw new KeyNotFoundException("Usuário não encontrado.");
            await UserCompanyScope.EnsureAccountWideActionAllowedAsync(_context, _companies, user.Id, PermissionCodes.Users.Unlock,
                _clock?.UtcNow ?? DateTimeOffset.UtcNow, cancellationToken);
        }

        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;

        if (user.Status == UserStatus.Locked)
        {
            user.Status = user.MustChangePassword ? UserStatus.PendingFirstAccess : UserStatus.Active;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new UnlockUserResponse(true, "Usuário desbloqueado com sucesso.");
    }
}
