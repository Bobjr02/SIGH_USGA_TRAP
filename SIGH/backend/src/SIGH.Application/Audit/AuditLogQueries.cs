using SIGH.Application.Common.Models;

namespace SIGH.Application.Audit;

public record AuditLogFilter(
    string? EntityName = null,
    string? EntityId = null,
    Guid? UserId = null,
    string? Action = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int PageNumber = 1,
    int PageSize = 50,
    /// <summary>Número da medida (MED-…) ou matrícula do funcionário: troca o "Id da entidade" na tela.</summary>
    string? Reference = null);

/// <summary>Usuário para o seletor da auditoria (busca por nome).</summary>
public record AuditUserOptionDto(Guid Id, string FullName, string Email);

public record AuditLogDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Action,
    string Entity,
    string? EntityId,
    Guid? UserId,
    string? UserName,
    string? IpAddress,
    string? CorrelationId,
    string? OldValues,
    string? NewValues,
    /// <summary>Empresa do registro (coluna Empresa no modo "Todas"); nulo nos eventos sem empresa (ex.: login).</summary>
    string? CompanyName = null);

/// <summary>
/// Auditoria de ACESSO (Audit.ViewAccess, do Administrador): só eventos de contas, perfis, gerências de usuários,
/// gerências, motivos e segurança (login, senha, bloqueio). Nada de funcionário, medida, anexo ou importação.
/// </summary>
public static class AccessAuditPolicy
{
    /// <summary>Entidades (AuditLogs.Entity) que entram na auditoria de acesso.</summary>
    public static readonly IReadOnlyCollection<string> Entities =
    [
        "User", "UserRole", "UserManagementUnit", "ManagementUnit", "Role", "Permission", "RolePermission",
        "InfractionType", "AccessControl",
    ];

    /// <summary>Eventos que nunca entram, mesmo gravados numa entidade de acesso (o download cita a medida).</summary>
    public static readonly IReadOnlyCollection<string> ExcludedActions =
    [
        SIGH.Application.Security.SecurityAuditActions.EvidenceDownloaded,
    ];
}

/// <summary>Consulta somente leitura da trilha de auditoria.</summary>
public interface IAuditLogQueryService
{
    public const int MaxPageSize = 200;

    Task<PagedResult<AuditLogDto>> SearchAsync(AuditLogFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Até 20 usuários cujo nome ou e-mail contém o termo (seletor "Usuário" da auditoria).</summary>
    Task<IReadOnlyList<AuditUserOptionDto>> SearchUsersAsync(string? term, CancellationToken cancellationToken = default);

    /// <summary>
    /// Auditoria de acesso (<see cref="AccessAuditPolicy"/>): só as empresas da requisição em que o usuário tem
    /// Audit.ViewAccess. Eventos sem empresa (login, senha) entram se forem de uma pessoa ligada a essas empresas.
    /// O filtro <see cref="AuditLogFilter.Reference"/> (medida ou matrícula) é ignorado.
    /// </summary>
    Task<PagedResult<AuditLogDto>> SearchAccessAsync(AuditLogFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Seletor "Usuário" da auditoria de acesso: pessoas ligadas às empresas com Audit.ViewAccess.</summary>
    Task<IReadOnlyList<AuditUserOptionDto>> SearchAccessUsersAsync(string? term, CancellationToken cancellationToken = default);

    /// <summary>
    /// Linha do tempo de um processo: registros do processo e das subentidades (ocorrências,
    /// envolvidos, evidências — inclusive removidas —, decisões e medidas), mais recentes primeiro.
    /// Null se o processo não existir.
    /// </summary>
    Task<PagedResult<AuditLogDto>?> GetDisciplinaryCaseTrailAsync(Guid caseId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
}
