using Microsoft.AspNetCore.Mvc;
using SIGH.Application.Audit;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Common.Models;
using SIGH.Infrastructure.Authorization;

namespace SIGH.Api.Controllers;

/// <summary>
/// Consulta da trilha de auditoria. Somente leitura: a auditoria só recebe inserções e não há
/// endpoint de alteração nem de exclusão (AuditLogInsertOnlyTests garante isso).
/// </summary>
[Route("api/v1/audit-logs")]
[Tags("Audit")]
public class AuditLogsController : BaseController
{
    private readonly IAuditLogQueryService _queryService;

    public AuditLogsController(IAuditLogQueryService queryService)
    {
        _queryService = queryService;
    }

    /// <summary>Lista paginada (mais recentes primeiro), com filtros por entidade, id, usuário, ação e período.</summary>
    [HttpGet]
    [CompanyRoute(CompanyRouteKind.Read)]
    [Permission(PermissionCodes.Audit.View)]
    [ProducesResponseType(typeof(Result<PagedResult<AuditLogDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Result<PagedResult<AuditLogDto>>>> Search(
        [FromQuery] string? entityName,
        [FromQuery] string? entityId,
        [FromQuery] Guid? userId,
        [FromQuery] string? action,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? reference,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _queryService.SearchAsync(
            new AuditLogFilter(entityName, entityId, userId, action, from, to, pageNumber, pageSize, reference), cancellationToken);
        return OkResult(result);
    }

    /// <summary>Seletor "Usuário" da auditoria: busca por nome ou e-mail (até 20). Exige Audit.View.</summary>
    [HttpGet("users")]
    [CompanyRoute(CompanyRouteKind.Read)]
    [Permission(PermissionCodes.Audit.View)]
    public async Task<ActionResult<Result<IReadOnlyList<AuditUserOptionDto>>>> SearchUsers([FromQuery] string? search, CancellationToken cancellationToken = default) =>
        OkResult(await _queryService.SearchUsersAsync(search, cancellationToken));

    /// <summary>
    /// Auditoria de ACESSO (Administrador): contas, perfis, gerências de usuários, gerências, motivos e eventos de
    /// segurança. Sem dados de funcionário nem de medida (nem busca por medida ou matrícula).
    /// </summary>
    [HttpGet("access")]
    [CompanyRoute(CompanyRouteKind.Read)]
    [Permission(PermissionCodes.Audit.ViewAccess)]
    [ProducesResponseType(typeof(Result<PagedResult<AuditLogDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Result<PagedResult<AuditLogDto>>>> SearchAccess(
        [FromQuery] string? entityName,
        [FromQuery] string? entityId,
        [FromQuery] Guid? userId,
        [FromQuery] string? action,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        OkResult(await _queryService.SearchAccessAsync(
            new AuditLogFilter(entityName, entityId, userId, action, from, to, pageNumber, pageSize), cancellationToken));

    /// <summary>Seletor "Usuário" da auditoria de acesso (até 20). Exige Audit.ViewAccess.</summary>
    [HttpGet("access/users")]
    [CompanyRoute(CompanyRouteKind.Read)]
    [Permission(PermissionCodes.Audit.ViewAccess)]
    public async Task<ActionResult<Result<IReadOnlyList<AuditUserOptionDto>>>> SearchAccessUsers([FromQuery] string? search, CancellationToken cancellationToken = default) =>
        OkResult(await _queryService.SearchAccessUsersAsync(search, cancellationToken));
}
