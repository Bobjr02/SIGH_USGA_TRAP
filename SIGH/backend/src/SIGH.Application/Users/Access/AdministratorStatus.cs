using Microsoft.EntityFrameworkCore;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Interfaces;
using SIGH.Application.Interfaces;

namespace SIGH.Application.Users.Access;

/// <summary>
/// Situação dos administradores de uma empresa, para o alerta da tela de usuários.
/// <see cref="CurrentAdministrators"/>: com perfil Administrador vigente agora e conta utilizável.
/// <see cref="LastValidUntil"/>: quando termina o acesso do último deles (nulo se algum não tem prazo).
/// </summary>
public sealed record AdministratorStatusDto(
    string CompanyCode,
    string CompanyName,
    int CurrentAdministrators,
    DateTimeOffset? LastValidUntil,
    bool SingleAdministrator,
    bool EndingSoon);

public interface IAdministratorStatusService
{
    /// <summary>Uma linha por empresa da requisição (a ativa, ou todas no modo "Todas") em que o usuário vê usuários.</summary>
    Task<IReadOnlyList<AdministratorStatusDto>> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class AdministratorStatusService : IAdministratorStatusService
{
    /// <summary>Alerta quando o acesso do último administrador termina em menos de 15 dias.</summary>
    public static readonly TimeSpan EndingSoonWindow = TimeSpan.FromDays(15);

    private readonly IApplicationDbContext _db;
    private readonly ICompanyContext _companies;
    private readonly IDateTimeProvider _clock;

    public AdministratorStatusService(IApplicationDbContext db, ICompanyContext companies, IDateTimeProvider clock)
    {
        _db = db;
        _companies = companies;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AdministratorStatusDto>> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var result = new List<AdministratorStatusDto>();
        foreach (var grant in await _companies.GetRequestGrantsAsync(cancellationToken))
        {
            var assignments = await UserAccessRules.CurrentAdministratorAssignments(_db, now, grant.CompanyId)
                .Select(ur => new { ur.UserId, ur.ValidUntil })
                .ToListAsync(cancellationToken);

            // Uma pessoa conta uma vez; o acesso dela termina no maior prazo (sem prazo = nunca).
            var people = assignments.GroupBy(a => a.UserId)
                .Select(g => g.Any(a => a.ValidUntil is null) ? (DateTimeOffset?)null : g.Max(a => a.ValidUntil))
                .ToList();
            DateTimeOffset? lastValidUntil = people.Count == 0 || people.Any(v => v is null) ? null : people.Max();

            result.Add(new AdministratorStatusDto(
                grant.Company.Code,
                grant.Company.Name,
                people.Count,
                lastValidUntil,
                SingleAdministrator: people.Count <= 1,
                EndingSoon: lastValidUntil is { } until && until - now < EndingSoonWindow));
        }
        return result;
    }
}
