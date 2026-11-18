using Microsoft.EntityFrameworkCore;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Interfaces;
using SIGH.Domain.Exceptions;

namespace SIGH.Application.Common.Authorization;

/// <summary>Empresa cadastrada (seed Seed:Companies).</summary>
public sealed record CompanyInfo(Guid Id, string Code, string Name, string ManagementUnitPrefix);

/// <summary>
/// O que o usuário tem numa empresa: perfis e permissões VIGENTES agora e as gerências vigentes daquela empresa.
/// Só existe para empresas em que o usuário tem pelo menos um perfil vigente.
/// </summary>
public sealed record CompanyGrant(
    CompanyInfo Company,
    IReadOnlyList<string> Roles,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<Guid> ManagementUnitIds,
    DateTimeOffset FirstGrantedAt)
{
    public Guid CompanyId => Company.Id;

    public bool Has(string permission) => Permissions.Contains(permission);
}

/// <summary>Empresa ativa da requisição: uma empresa específica, ou "Todas" (só leitura).</summary>
public sealed record CompanySelection(bool IsAll, Guid? CompanyId)
{
    public static readonly CompanySelection None = new(false, null);
}

/// <summary>
/// Contexto de empresa da requisição. Fonte ÚNICA das permissões: perfis vigentes por empresa, lidos do banco
/// (nunca do token nem do que o cliente envia). A empresa ativa chega no cabeçalho <see cref="HeaderName"/>
/// e é sempre validada contra as empresas em que o usuário tem perfil vigente.
/// </summary>
public interface ICompanyContext
{
    public const string HeaderName = "X-SIGH-Company";

    /// <summary>Valor do cabeçalho para o modo "Todas" (só leitura).</summary>
    public const string AllCompanies = "TODAS";

    /// <summary>Empresas em que o usuário tem perfil vigente, na ordem do primeiro perfil.</summary>
    Task<IReadOnlyList<CompanyGrant>> GetGrantsAsync(CancellationToken cancellationToken = default);

    /// <summary>Empresa ativa validada. Lança <see cref="CompanyNotAccessibleException"/> (404) se o cabeçalho
    /// pedir uma empresa em que o usuário não tem perfil vigente.</summary>
    Task<CompanySelection> GetSelectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Empresas que esta requisição pode LER: a empresa ativa (ou todas, no modo "Todas") em que o usuário tem
    /// TODAS as permissões exigidas pelo endpoint (registradas por <see cref="AuthorizeAsync"/>).
    /// </summary>
    Task<IReadOnlyList<CompanyGrant>> GetRequestGrantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Usado pelo PermissionHandler: registra a permissão exigida pelo endpoint e diz se ela existe em alguma
    /// empresa da requisição. As leituras ficam restritas às empresas em que ela existe.
    /// </summary>
    Task<bool> AuthorizeAsync(string permission, CancellationToken cancellationToken = default);

    /// <summary>A permissão existe, vigente, na empresa informada?</summary>
    Task<bool> HasPermissionAsync(Guid companyId, string permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empresa em que uma ESCRITA acontece: a empresa ativa específica. No modo "Todas" (ou sem empresa) lança
    /// <see cref="CompanyRequiredException"/>.
    /// </summary>
    Task<Guid> RequireWriteCompanyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Garante que a escrita sobre um recurso da empresa <paramref name="resourceCompanyId"/> é permitida: a empresa
    /// ativa é específica, é a do recurso e o usuário tem a permissão nela. Fora disso, 404 (o recurso "não existe").
    /// </summary>
    Task EnsureCanWriteAsync(Guid resourceCompanyId, string permission, CancellationToken cancellationToken = default);
}

/// <summary>A empresa pedida não está ao alcance do usuário: 404, como qualquer recurso fora do alcance.</summary>
public sealed class CompanyNotAccessibleException : KeyNotFoundException
{
    public const string ErrorCode = "COMPANY_NOT_FOUND";

    public CompanyNotAccessibleException() : base("Empresa não encontrada.") { }
}

/// <summary>Escrita no modo "Todas" (ou sem empresa ativa): é preciso escolher uma empresa.</summary>
public sealed class CompanyRequiredException : BusinessRuleValidationException
{
    public const string ErrorCode = "COMPANY_REQUIRED";

    /// <summary>Leitura que só faz sentido em uma empresa (ou requisição sem empresa escolhida).</summary>
    public const string SelectCompanyMessage = "Selecione uma empresa no topo da tela.";

    public CompanyRequiredException()
        : base("O modo \"Todas\" é só para consulta. Escolha uma empresa no topo da tela para registrar, aprovar ou administrar.") { }

    public CompanyRequiredException(string message) : base(message) { }
}

public sealed class CompanyContext : ICompanyContext
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IRequestContext _request;
    private readonly IDateTimeProvider _clock;
    private readonly ActiveCompanyHolder? _holder;
    private readonly HashSet<string> _required = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<CompanyGrant>? _grants;
    private CompanySelection? _selection;

    public CompanyContext(IApplicationDbContext db, ICurrentUserService currentUser, IRequestContext request, IDateTimeProvider clock, ActiveCompanyHolder? holder = null)
    {
        _db = db;
        _currentUser = currentUser;
        _request = request;
        _clock = clock;
        _holder = holder;
    }

    private CompanySelection Remember(CompanySelection selection)
    {
        if (_holder is not null)
            _holder.CompanyId = selection.IsAll ? null : selection.CompanyId;
        return selection;
    }

    public async Task<IReadOnlyList<CompanyGrant>> GetGrantsAsync(CancellationToken cancellationToken = default)
    {
        if (_grants is not null)
            return _grants;
        if (_currentUser.UserId is not { } userId)
            return _grants = Array.Empty<CompanyGrant>();
        return _grants = await LoadGrantsAsync(_db, userId, _clock.UtcNow, cancellationToken);
    }

    /// <summary>Perfis e gerências vigentes de um usuário, por empresa (também usado por /auth/me e pelo seed).</summary>
    public static async Task<IReadOnlyList<CompanyGrant>> LoadGrantsAsync(IApplicationDbContext db, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var roles = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.Role.IsDeleted
                         && (ur.ValidFrom == null || ur.ValidFrom <= now)
                         && (ur.ValidUntil == null || ur.ValidUntil > now))
            .Select(ur => new
            {
                ur.CompanyId,
                ur.Role.Name,
                ur.CreatedAt,
                Permissions = ur.Role.RolePermissions
                    .Where(rp => !rp.IsDeleted && !rp.Permission.IsDeleted)
                    .Select(rp => rp.Permission.Code)
                    .ToList()
            })
            .ToListAsync(cancellationToken);
        if (roles.Count == 0)
            return Array.Empty<CompanyGrant>();

        var companyIds = roles.Select(r => r.CompanyId).Distinct().ToList();
        var companies = await db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id) && c.IsActive)
            .Select(c => new CompanyInfo(c.Id, c.Code, c.Name, c.ManagementUnitPrefix))
            .ToListAsync(cancellationToken);

        // Gerência só conta na empresa a que pertence.
        var units = await db.UserManagementUnits.AsNoTracking()
            .Where(x => x.UserId == userId && companyIds.Contains(x.ManagementUnit.CompanyId)
                        && (x.ValidFrom == null || x.ValidFrom <= now)
                        && (x.ValidUntil == null || x.ValidUntil > now))
            .Select(x => new { x.ManagementUnitId, x.ManagementUnit.CompanyId })
            .ToListAsync(cancellationToken);

        return companies
            .Select(company =>
            {
                var mine = roles.Where(r => r.CompanyId == company.Id).ToList();
                return new CompanyGrant(
                    company,
                    mine.Select(r => r.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                    mine.SelectMany(r => r.Permissions).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    units.Where(u => u.CompanyId == company.Id).Select(u => u.ManagementUnitId).Distinct().ToList(),
                    mine.Min(r => r.CreatedAt));
            })
            .OrderBy(g => g.FirstGrantedAt)
            .ThenBy(g => g.Company.Code, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<CompanySelection> GetSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (_selection is not null)
            return _selection;

        var grants = await GetGrantsAsync(cancellationToken);
        var requested = _request.CompanyHeader?.Trim();

        if (string.IsNullOrEmpty(requested))
        {
            // Sem cabeçalho: só é inequívoco com UMA empresa. Com duas ou mais, nunca escolhe por conta própria: 400
            // COMPANY_REQUIRED (a tela sempre manda o cabeçalho; clientes da API precisam mandar).
            return _selection = grants.Count switch
            {
                0 => Remember(CompanySelection.None),
                1 => Remember(new CompanySelection(false, grants.Single().CompanyId)),
                _ => throw new CompanyRequiredException(CompanyRequiredException.SelectCompanyMessage),
            };
        }

        if (string.Equals(requested, ICompanyContext.AllCompanies, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            if (grants.Count == 0)
                throw new CompanyNotAccessibleException();
            return _selection = Remember(new CompanySelection(true, null));
        }

        var match = grants.FirstOrDefault(g =>
            string.Equals(g.Company.Code, requested, StringComparison.OrdinalIgnoreCase)
            || (Guid.TryParse(requested, out var id) && g.CompanyId == id));

        // Nunca confia no cliente: empresa sem perfil vigente (ou inexistente) é 404.
        if (match is null)
            throw new CompanyNotAccessibleException();

        return _selection = Remember(new CompanySelection(false, match.CompanyId));
    }

    public async Task<IReadOnlyList<CompanyGrant>> GetRequestGrantsAsync(CancellationToken cancellationToken = default)
    {
        var grants = await GetGrantsAsync(cancellationToken);
        var selection = await GetSelectionAsync(cancellationToken);

        IEnumerable<CompanyGrant> selected = selection.IsAll
            ? grants
            : grants.Where(g => g.CompanyId == selection.CompanyId);

        return selected.Where(g => _required.All(g.Has)).ToList();
    }

    public async Task<bool> AuthorizeAsync(string permission, CancellationToken cancellationToken = default)
    {
        _required.Add(permission);
        return (await GetRequestGrantsAsync(cancellationToken)).Count > 0;
    }

    public async Task<bool> HasPermissionAsync(Guid companyId, string permission, CancellationToken cancellationToken = default) =>
        (await GetGrantsAsync(cancellationToken)).Any(g => g.CompanyId == companyId && g.Has(permission));

    public async Task<Guid> RequireWriteCompanyAsync(CancellationToken cancellationToken = default)
    {
        var selection = await GetSelectionAsync(cancellationToken);
        if (selection.IsAll || selection.CompanyId is not { } companyId)
            throw new CompanyRequiredException();
        return companyId;
    }

    public async Task EnsureCanWriteAsync(Guid resourceCompanyId, string permission, CancellationToken cancellationToken = default)
    {
        var active = await RequireWriteCompanyAsync(cancellationToken);
        if (resourceCompanyId != active || !await HasPermissionAsync(resourceCompanyId, permission, cancellationToken))
            throw new KeyNotFoundException("Registro não encontrado.");
    }
}
