using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SIGH.Application.Authentication.PasswordTokens;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Email;
using SIGH.Application.Options;
using SIGH.Application.Security;
using SIGH.Domain.Entities;
using SIGH.Domain.Enums;
using SIGH.Domain.Exceptions;

namespace SIGH.Application.Users.SetupLink;

/// <summary>
/// Link de definição de senha (uso único, guardado só como hash, validade Auth:AccountSetupTokenHours = 48 h).
/// É o ÚNICO jeito de uma conta nova ganhar senha: nenhum administrador conhece nem define a senha de outra pessoa.
/// Não grava: o token e o e-mail entram no SaveChanges do caso de uso.
/// </summary>
public interface IAccountSetupLinkSender
{
    /// <summary>E-mail "Sua conta no SIGH foi criada", com o link.</summary>
    Task SendAccountCreatedAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>E-mail "Acesso de administrador no SIGH - empresa", com o link (primeiro administrador e recuperação).</summary>
    Task SendAdministratorAccessAsync(User user, string companyName, CancellationToken cancellationToken = default);
}

public sealed class AccountSetupLinkSender : IAccountSetupLinkSender
{
    private readonly IPasswordTokenService _tokens;
    private readonly IEmailOutbox _outbox;
    private readonly AppOptions _app;

    public AccountSetupLinkSender(IPasswordTokenService tokens, IEmailOutbox outbox, IOptions<AppOptions> app)
    {
        _tokens = tokens;
        _outbox = outbox;
        _app = app.Value;
    }

    public async Task SendAccountCreatedAsync(User user, CancellationToken cancellationToken = default)
    {
        var (link, hours) = await IssueAsync(user, cancellationToken);
        _outbox.Enqueue(user.Email, EmailTemplates.AccountCreated(user.FullName, user.Email, link, hours, _app.BuildUrl("/login")));
    }

    public async Task SendAdministratorAccessAsync(User user, string companyName, CancellationToken cancellationToken = default)
    {
        var (link, hours) = await IssueAsync(user, cancellationToken);
        _outbox.Enqueue(user.Email, EmailTemplates.AdministratorAccess(user.FullName, user.Email, companyName, link, hours, _app.BuildUrl("/login")));
    }

    private async Task<(string Link, int Hours)> IssueAsync(User user, CancellationToken cancellationToken)
    {
        // Emitir invalida os links anteriores ainda não usados: só o último vale.
        var raw = await _tokens.IssueAsync(user.Id, Domain.Entities.PasswordTokenPurpose.AccountSetup, cancellationToken);
        var hours = (int)_tokens.LifetimeOf(Domain.Entities.PasswordTokenPurpose.AccountSetup).TotalHours;
        return (_app.BuildUrl($"/reset-password?token={Uri.EscapeDataString(raw)}&setup=1"), hours);
    }
}

public sealed record ResendSetupLinkResponse(Guid UserId, string Email, int ValidHours);

/// <summary>Reenvio do link de definição de senha pela tela de usuários (conta que ainda não definiu a senha).</summary>
public interface IResendSetupLinkService
{
    Task<ResendSetupLinkResponse> ResendAsync(Guid actorUserId, Guid userId, CancellationToken cancellationToken = default);
}

public sealed class ResendSetupLinkService : IResendSetupLinkService
{
    public const string AlreadyDefinedMessage =
        "Esta pessoa já definiu a senha. Se ela esqueceu, use \"Esqueci minha senha\" na tela de login.";

    private readonly IApplicationDbContext _db;
    private readonly ICompanyContext _companies;
    private readonly IAccountSetupLinkSender _links;
    private readonly IPasswordTokenService _tokens;
    private readonly ISecurityAuditLogger _audit;

    public ResendSetupLinkService(IApplicationDbContext db, ICompanyContext companies, IAccountSetupLinkSender links, IPasswordTokenService tokens, ISecurityAuditLogger audit)
    {
        _db = db;
        _companies = companies;
        _links = links;
        _tokens = tokens;
        _audit = audit;
    }

    public async Task<ResendSetupLinkResponse> ResendAsync(Guid actorUserId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Só na empresa ativa (escrita) e só para quem o administrador enxerga nela; fora disso, 404.
        var companyId = await _companies.RequireWriteCompanyAsync(cancellationToken);
        if (!await Access.UserCompanyScope.IsVisibleAsync(_db, userId, [companyId], cancellationToken))
            throw new KeyNotFoundException("Usuário não encontrado.");
        if (userId == actorUserId)
            throw new BusinessRuleValidationException("Você não pode reenviar o link da sua própria conta.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new KeyNotFoundException("Usuário não encontrado.");
        if (user.Status != UserStatus.PendingFirstAccess)
            throw new BusinessRuleValidationException(AlreadyDefinedMessage);

        await _links.SendAccountCreatedAsync(user, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(SecurityAuditActions.AccountSetupLinkSent, actorUserId, new { reason = "reenvio pela tela de usuários", email = user.Email },
            cancellationToken, entityName: nameof(User), entityId: user.Id.ToString());

        return new ResendSetupLinkResponse(user.Id, user.Email, (int)_tokens.LifetimeOf(Domain.Entities.PasswordTokenPurpose.AccountSetup).TotalHours);
    }
}
