using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Interfaces;
using SIGH.Application.Options;
using SIGH.Application.Security;
using SIGH.Application.Users.Access;
using SIGH.Application.Users.SetupLink;
using SIGH.Domain.Employees.Entities;
using SIGH.Domain.Entities;
using SIGH.Domain.Enums;
using SIGH.Domain.Exceptions;

namespace SIGH.Application.Users.Bootstrap;

/// <summary>Primeiro administrador de uma empresa (Seed:CompanyAdmins:N:CompanyCode/Email/Name).</summary>
public sealed record CompanyAdminSeed(string CompanyCode, string Email, string? Name = null);

/// <summary>O que foi feito para um <see cref="CompanyAdminSeed"/>.</summary>
public sealed record CompanyAdminResult(string CompanyCode, string Email, bool Granted, bool AccountCreated, string Message);

/// <summary>
/// Primeiro administrador de cada empresa na publicação e recuperação de administrador pelo TI. Nos dois casos a conta
/// recebe SÓ o perfil Administrador naquela empresa e um link de definição de senha por e-mail: ninguém conhece a senha.
/// Tudo é registrado na auditoria, com a origem ("publicação" ou "linha de comando").
/// </summary>
public interface ICompanyAdminProvisioner
{
    /// <summary>
    /// Para cada empresa SEM Administrador vigente: cria a conta (se não existir) ou só concede o perfil, e envia o
    /// link. Empresa que já tem administrador não muda. Entrada inválida é registrada no log e ignorada (o startup
    /// não cai por causa dela).
    /// </summary>
    Task<IReadOnlyList<CompanyAdminResult>> ProvisionMissingAsync(IEnumerable<CompanyAdminSeed> seeds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recuperação pelo TI (comando admin-recover, só na linha de comando do servidor): concede Administrador naquela
    /// empresa MESMO que ela já tenha outro, reativa a conta se estiver inativa e envia o link. Os outros administradores
    /// vigentes da empresa recebem um e-mail avisando quem recebeu o perfil e quando. Entrada inválida lança
    /// <see cref="BusinessRuleValidationException"/>.
    /// </summary>
    Task<CompanyAdminResult> RecoverAsync(CompanyAdminSeed seed, CancellationToken cancellationToken = default);
}

public sealed class CompanyAdminProvisioner : ICompanyAdminProvisioner
{
    public const string OriginPublication = "publicação";
    public const string OriginCommandLine = "linha de comando";

    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IRandomStringGenerator _random;
    private readonly IDateTimeProvider _clock;
    private readonly IAccountSetupLinkSender _links;
    private readonly SIGH.Application.Email.IEmailOutbox _outbox;
    private readonly ISecurityAuditLogger _audit;
    private readonly AuthOptions _auth;
    private readonly ILogger<CompanyAdminProvisioner> _logger;

    public CompanyAdminProvisioner(
        IApplicationDbContext db,
        IPasswordHasher hasher,
        IRandomStringGenerator random,
        IDateTimeProvider clock,
        IAccountSetupLinkSender links,
        SIGH.Application.Email.IEmailOutbox outbox,
        ISecurityAuditLogger audit,
        IOptions<AuthOptions> auth,
        ILogger<CompanyAdminProvisioner> logger)
    {
        _db = db;
        _hasher = hasher;
        _random = random;
        _clock = clock;
        _links = links;
        _outbox = outbox;
        _audit = audit;
        _auth = auth.Value;
        _logger = logger;
    }

    /// <summary>
    /// Lê Seed:CompanyAdmins e as chaves antigas, que viram entradas do mesmo mecanismo (bancos e configurações
    /// existentes continuam valendo): Seed:AdminEmail (USGA) e Seed:Companies:&lt;código&gt;:AdminEmail. Senhas
    /// configuradas (Seed:AdminPassword, Seed:Companies:&lt;código&gt;:AdminPassword) são ignoradas, com aviso.
    /// </summary>
    public static IReadOnlyList<CompanyAdminSeed> ReadConfiguration(IConfiguration configuration, ILogger? logger = null)
    {
        var result = new List<CompanyAdminSeed>();
        foreach (var section in configuration.GetSection("Seed:CompanyAdmins").GetChildren())
        {
            var code = section["CompanyCode"]?.Trim();
            var email = section["Email"]?.Trim();
            // Sem e-mail: entrada opcional deixada vazia (ex.: variável de ambiente não definida); nada a fazer.
            if (string.IsNullOrEmpty(email))
                continue;
            if (string.IsNullOrEmpty(code))
            {
                logger?.LogWarning("Seed:CompanyAdmins:{Index} sem CompanyCode: {Email} ignorado.", section.Key, email);
                continue;
            }
            result.Add(new CompanyAdminSeed(code.ToUpperInvariant(), email, section["Name"]?.Trim() is { Length: > 0 } name ? name : null));
        }

        foreach (var section in configuration.GetSection("Seed:Companies").GetChildren())
        {
            if (section["AdminEmail"]?.Trim() is { Length: > 0 } email)
                result.Add(new CompanyAdminSeed(section.Key.Trim().ToUpperInvariant(), email));
            if (!string.IsNullOrEmpty(section["AdminPassword"]))
                logger?.LogWarning("Seed:Companies:{Code}:AdminPassword é ignorada: o administrador define a própria senha pelo link enviado por e-mail.", section.Key);
        }

        if (configuration["Seed:AdminEmail"]?.Trim() is { Length: > 0 } usgaEmail)
            result.Add(new CompanyAdminSeed(CompanyDefaults.UsgaCode, usgaEmail, "Administrador do Sistema"));
        if (!string.IsNullOrEmpty(configuration["Seed:AdminPassword"]))
            logger?.LogWarning("Seed:AdminPassword é ignorada: o administrador define a própria senha pelo link enviado por e-mail.");

        return result;
    }

    public async Task<IReadOnlyList<CompanyAdminResult>> ProvisionMissingAsync(IEnumerable<CompanyAdminSeed> seeds, CancellationToken cancellationToken = default)
    {
        var results = new List<CompanyAdminResult>();
        foreach (var seed in seeds)
        {
            var code = seed.CompanyCode.Trim().ToUpperInvariant();
            var company = await FindCompanyAsync(code, cancellationToken);
            if (company is null)
            {
                _logger.LogWarning("Seed:CompanyAdmins: a empresa {Code} não existe; {Email} NÃO recebeu o perfil Administrador.", code, seed.Email);
                results.Add(new CompanyAdminResult(code, seed.Email, false, false, "empresa não encontrada"));
                continue;
            }

            if (await UserAccessRules.CurrentAdministratorAssignments(_db, _clock.UtcNow, company.Id).AnyAsync(cancellationToken))
            {
                results.Add(new CompanyAdminResult(code, seed.Email, false, false, "a empresa já tem administrador"));
                continue;
            }

            if (ValidateEmail(seed.Email) is { } problem)
            {
                _logger.LogError("Seed:CompanyAdmins: {Email} ({Code}) recusado: {Problem}", seed.Email, code, problem);
                results.Add(new CompanyAdminResult(code, seed.Email, false, false, problem));
                continue;
            }

            var result = await GrantAsync(company, seed, SecurityAuditActions.CompanyAdminProvisioned, OriginPublication, cancellationToken);
            _logger.LogInformation("Seed: primeiro administrador da empresa {Code}: {Email} ({What}); link de definição de senha enviado.",
                code, result.Email, result.AccountCreated ? "conta criada" : "conta existente");
            results.Add(result);
        }
        return results;
    }

    public async Task<CompanyAdminResult> RecoverAsync(CompanyAdminSeed seed, CancellationToken cancellationToken = default)
    {
        var code = seed.CompanyCode.Trim().ToUpperInvariant();
        var company = await FindCompanyAsync(code, cancellationToken)
            ?? throw new BusinessRuleValidationException($"Empresa {code} não encontrada.");
        if (ValidateEmail(seed.Email) is { } problem)
            throw new BusinessRuleValidationException(problem);

        // Administradores vigentes ANTES da concessão (fora a própria conta): são eles que recebem o aviso.
        var email = seed.Email.Trim().ToLowerInvariant();
        var now = _clock.UtcNow;
        var others = await UserAccessRules.CurrentAdministratorAssignments(_db, now, company.Id)
            .Where(ur => ur.User.Email != email)
            .Select(ur => new { ur.User.FullName, ur.User.Email })
            .Distinct()
            .ToListAsync(cancellationToken);

        var result = await GrantAsync(company, seed, SecurityAuditActions.AdministratorRecovered, OriginCommandLine, cancellationToken);

        if (others.Count > 0)
        {
            var grantedName = await _db.Users.IgnoreQueryFilters().Where(u => u.Email == email).Select(u => u.FullName).FirstAsync(cancellationToken);
            foreach (var admin in others)
                _outbox.Enqueue(admin.Email, SIGH.Application.Email.EmailTemplates.AdministratorRecoveredNotice(admin.FullName, company.Name, grantedName, email, now));
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("admin-recover: {Count} administradores da empresa {Code} avisados por e-mail.", others.Count, company.Code);
        }
        return result;
    }

    private Task<Company?> FindCompanyAsync(string code, CancellationToken cancellationToken) =>
        _db.Companies.FirstOrDefaultAsync(c => c.Code == code && c.IsActive, cancellationToken);

    private string? ValidateEmail(string email)
    {
        var value = email.Trim();
        if (!System.Net.Mail.MailAddress.TryCreate(value, out var parsed) || parsed.Address != value)
            return "e-mail inválido.";
        return _auth.IsEmailDomainAllowed(value) ? null : _auth.EmailDomainMessage();
    }

    private async Task<CompanyAdminResult> GrantAsync(Company company, CompanyAdminSeed seed, string action, string origin, CancellationToken cancellationToken)
    {
        var email = seed.Email.Trim().ToLowerInvariant();
        var adminRole = await _db.Roles.FirstAsync(r => r.Name == SystemRoles.Administrator, cancellationToken);
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        var created = false;
        var reactivated = false;
        if (user is null)
        {
            // Senha aleatória que ninguém conhece: o acesso só existe depois que a pessoa usa o link.
            var hash = _hasher.HashPassword(_random.GenerateRandomString(64));
            user = new User
            {
                FullName = seed.Name ?? $"Administrador do Sistema ({company.Code})",
                Email = email,
                Cpf = await TechnicalCpfAsync(cancellationToken),
                PasswordHash = hash,
                Status = UserStatus.PendingFirstAccess,
                MustChangePassword = true,
                FailedLoginAttempts = 0,
                CreatedInCompanyId = company.Id
            };
            user.PasswordHistories.Add(new PasswordHistory { UserId = user.Id, PasswordHash = hash });
            _db.Users.Add(user);
            created = true;
        }
        else
        {
            if (user.IsDeleted)
            {
                user.IsDeleted = false;
                user.DeletedAt = null;
                user.DeletedBy = null;
                reactivated = true;
            }
            if (user.Status is UserStatus.Inactive or UserStatus.Suspended)
            {
                user.Status = UserStatus.Active;
                reactivated = true;
            }
            user.RotateSecurityStamp();
        }

        // Um vínculo ativo por perfil e empresa: se já existe (vencido ou agendado), passa a valer sem prazo.
        var link = created ? null : await _db.UserRoles
            .FirstOrDefaultAsync(ur => ur.UserId == user.Id && ur.RoleId == adminRole.Id && ur.CompanyId == company.Id, cancellationToken);
        if (link is null)
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = adminRole.Id, CompanyId = company.Id });
        else
        {
            link.ValidFrom = null;
            link.ValidUntil = null;
        }

        await _links.SendAdministratorAccessAsync(user, company.Name, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(action, null,
            new { origin, company = company.Code, email, accountCreated = created, reactivated },
            cancellationToken, entityName: nameof(User), entityId: user.Id.ToString(), companyId: company.Id);

        return new CompanyAdminResult(company.Code, email, true, created,
            created ? "conta criada com o perfil Administrador" : "perfil Administrador concedido à conta existente");
    }

    /// <summary>CPF técnico livre (<see cref="TechnicalCpf"/>; o primeiro, 00000000000, é o do administrador do seed antigo).</summary>
    private async Task<string> TechnicalCpfAsync(CancellationToken cancellationToken)
    {
        var used = await _db.Users.IgnoreQueryFilters().Where(u => u.Cpf.StartsWith(TechnicalCpf.Prefix)).Select(u => u.Cpf).ToListAsync(cancellationToken);
        return TechnicalCpf.Candidates().FirstOrDefault(c => !used.Contains(c))
            ?? throw new InvalidOperationException("Não há CPF técnico livre para o administrador.");
    }
}
