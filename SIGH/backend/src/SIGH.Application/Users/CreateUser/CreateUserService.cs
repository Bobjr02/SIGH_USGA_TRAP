using Microsoft.Extensions.Options;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Common.Validators;
using SIGH.Application.Interfaces;
using SIGH.Application.Options;
using SIGH.Domain.Entities;
using SIGH.Domain.Enums;
using SIGH.Domain.Exceptions;
using SIGH.Domain.Repositories;
using SIGH.Application.Security;
using SIGH.Application.Users.SetupLink;

namespace SIGH.Application.Users.CreateUser;

public class CreateUserService : ICreateUserService
{
    private readonly IApplicationDbContext _context;
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAccountSetupLinkSender _links;
    private readonly IRandomStringGenerator _random;
    private readonly AuthOptions _authOptions;
    private readonly SIGH.Application.Common.Authorization.ICompanyContext? _companies;
    private readonly ISecurityAuditLogger? _audit;

    public CreateUserService(
        IApplicationDbContext context,
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IAccountSetupLinkSender links,
        IRandomStringGenerator random,
        IOptions<AuthOptions> authOptions,
        SIGH.Application.Common.Authorization.ICompanyContext? companies = null,
        ISecurityAuditLogger? audit = null)
    {
        _context = context;
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _links = links;
        _random = random;
        _authOptions = authOptions.Value;
        _companies = companies;
        _audit = audit;
    }

    public async Task<CreateUserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var cpf = request.Cpf.Trim();

        // A conta nasce na empresa ATIVA do administrador, com os perfis nela.
        // Nunca escolhe empresa por conta própria: sem contexto de empresa não há como criar.
        var companyId = await (_companies ?? throw new InvalidOperationException("Contexto de empresa indisponível."))
            .RequireWriteCompanyAsync(cancellationToken);

        bool emailExists = await _userRepository.ExistsByEmailAsync(email, cancellationToken);
        if (emailExists)
        {
            // E-mail de uma conta que o administrador não enxerga (outra empresa do grupo): oferece o vínculo, sem
            // criar outra conta e sem mostrar os perfis que a pessoa tem na outra empresa.
            var existingId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                System.Linq.Queryable.Select(System.Linq.Queryable.Where(_context.Users, u => u.Email == email), u => (Guid?)u.Id), cancellationToken);
            if (existingId is { } id && !await Access.UserCompanyScope.IsVisibleAsync(_context, id, [companyId], cancellationToken))
                throw new ExistingUserCanBeLinkedException();
            throw new BusinessRuleValidationException("Já existe um usuário cadastrado com este e-mail corporativo.");
        }

        // Contas NOVAS só com e-mail corporativo (Auth:AllowedEmailDomains; padrão @usga.com.br). Uma conta que já
        // existe continua podendo ser vinculada (acima), seja qual for o domínio.
        if (!_authOptions.IsEmailDomainAllowed(email))
            throw new BusinessRuleValidationException(_authOptions.EmailDomainMessage());

        bool cpfExists = await _userRepository.ExistsByCpfAsync(cpf, cancellationToken);
        if (cpfExists)
        {
            throw new BusinessRuleValidationException("Já existe um usuário cadastrado com este CPF.");
        }

        // Verificar validade das Roles fornecidas
        var roles = await _userRepository.GetRolesByIdsAsync(request.RoleIds, cancellationToken);

        if (roles.Count != request.RoleIds.Distinct().Count())
        {
            throw new BusinessRuleValidationException("Um ou mais perfis (Roles) fornecidos são inválidos ou inexistentes.");
        }

        if (roles.Any(r => Common.Authorization.SystemRoles.Find(r.Name) is null))
        {
            throw new BusinessRuleValidationException("Só os perfis de sistema podem ser atribuídos.");
        }

        var managementUnitIds = (request.ManagementUnitIds ?? []).Distinct().ToList();
        if (roles.Any(r => r.Name == Common.Authorization.SystemRoles.Manager) && managementUnitIds.Count == 0)
        {
            throw new BusinessRuleValidationException(Access.UserAccessRules.ManagerWithoutUnitMessage);
        }

        if (managementUnitIds.Count > 0)
        {
            // Só gerências da empresa em que a conta está sendo criada.
            var found = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                _context.ManagementUnits, m => managementUnitIds.Contains(m.Id) && m.CompanyId == companyId, cancellationToken);
            if (found != managementUnitIds.Count)
                throw new BusinessRuleValidationException("Uma ou mais gerências informadas não existem.");
        }

        // Senha aleatória que ninguém conhece: o acesso só é possível pelo link de definição de senha do e-mail.
        var passwordHash = _passwordHasher.HashPassword(_random.GenerateRandomString(64));

        var user = new User
        {
            FullName = request.FullName.Trim(),
            Email = email,
            Cpf = cpf,
            PasswordHash = passwordHash,
            Status = UserStatus.PendingFirstAccess,
            MustChangePassword = true,
            FailedLoginAttempts = 0,
            CreatedInCompanyId = companyId
        };

        foreach (var role in roles)
        {
            user.UserRoles.Add(new UserRole
            {
                UserId = user.Id,
                RoleId = role.Id,
                CompanyId = companyId
            });
        }

        foreach (var managementUnitId in managementUnitIds)
        {
            user.ManagementUnits.Add(new UserManagementUnit { UserId = user.Id, ManagementUnitId = managementUnitId });
        }

        user.PasswordHistories.Add(new PasswordHistory
        {
            UserId = user.Id,
            PasswordHash = passwordHash
        });

        await _userRepository.AddAsync(user, cancellationToken);

        // E-mail de conta criada, com o link de definição de senha (nunca a senha). Mesma transação.
        await _links.SendAccountCreatedAsync(user, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        if (_audit is not null)
            await _audit.RecordAsync(SecurityAuditActions.AccountSetupLinkSent, null,
                new { reason = "conta criada", email = user.Email }, cancellationToken, entityName: nameof(User), entityId: user.Id.ToString());

        return new CreateUserResponse(
            user.Id,
            user.FullName,
            user.Email,
            user.Cpf,
            user.Status.ToString(),
            user.MustChangePassword
        );
    }
}

/// <summary>
/// O e-mail já é de uma conta de outra empresa do grupo: a tela oferece "vincular este usuário à empresa"
/// (POST /api/v1/users/link). Resposta 409 com o código <see cref="ErrorCode"/>.
/// </summary>
public sealed class ExistingUserCanBeLinkedException : BusinessRuleValidationException
{
    public const string ErrorCode = "USER_EXISTS_IN_ANOTHER_COMPANY";

    public ExistingUserCanBeLinkedException()
        : base("Já existe uma conta com este e-mail em outra empresa do grupo. Você pode vincular este usuário a esta empresa, atribuindo perfis aqui, sem criar outra conta.") { }
}
