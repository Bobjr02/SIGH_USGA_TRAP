using SIGH.Application.Common.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIGH.Application.Common.Models;
using SIGH.Application.Users.ChangeStatus;
using SIGH.Application.Users.CreateUser;
using SIGH.Application.Users.GetUserById;
using SIGH.Application.Users.GetUsers;
using SIGH.Application.Users.UnlockUser;
using SIGH.Infrastructure.Authorization;

namespace SIGH.Api.Controllers;

[Route("api/v1/users")]
[Tags("Users")]
[Authorize]
public class UsersController : BaseController
{
    private readonly ICreateUserService _createUserService;
    private readonly IChangeUserStatusService _changeUserStatusService;
    private readonly IUnlockUserService _unlockUserService;
    private readonly IGetUsersService _getUsersService;
    private readonly IGetUserByIdService _getUserByIdService;
    private readonly SIGH.Application.Users.Access.IUserAccessAdministrationService _access;
    private readonly SIGH.Application.Users.SetupLink.IResendSetupLinkService _setupLinks;
    private readonly SIGH.Application.Users.Access.IAdministratorStatusService _administratorStatus;

    public UsersController(
        ICreateUserService createUserService,
        IChangeUserStatusService changeUserStatusService,
        IUnlockUserService unlockUserService,
        IGetUsersService getUsersService,
        IGetUserByIdService getUserByIdService,
        SIGH.Application.Users.Access.IUserAccessAdministrationService access,
        SIGH.Application.Users.SetupLink.IResendSetupLinkService setupLinks,
        SIGH.Application.Users.Access.IAdministratorStatusService administratorStatus)
    {
        _setupLinks = setupLinks;
        _administratorStatus = administratorStatus;
        _access = access;
        _createUserService = createUserService;
        _changeUserStatusService = changeUserStatusService;
        _unlockUserService = unlockUserService;
        _getUsersService = getUsersService;
        _getUserByIdService = getUserByIdService;
    }

    [HttpPost]
    [CompanyRoute(CompanyRouteKind.Write)]
    [Permission(PermissionCodes.Users.Create)]
    [ProducesResponseType(typeof(Result<CreateUserResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Result<CreateUserResponse>>> CreateUser([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await _createUserService.CreateUserAsync(request, cancellationToken);
        return CreatedResult(nameof(GetUserById), new { id = result.Id }, result, "Usuário criado com sucesso.");
    }

    /// <summary>
    /// Vincula à empresa ativa uma conta que já existe (e-mail de outra empresa do grupo), atribuindo perfis nela.
    /// Não cria outra conta e não mostra os perfis da outra empresa.
    /// </summary>
    [HttpPost("link")]
    [CompanyRoute(CompanyRouteKind.Write)]
    [Permission(PermissionCodes.Users.Create)]
    [Permission(PermissionCodes.Users.ManageRoles)]
    [ProducesResponseType(typeof(Result<SIGH.Application.Users.Access.LinkExistingUserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Result<SIGH.Application.Users.Access.LinkExistingUserResponse>>> LinkExistingUser(
        [FromBody] SIGH.Application.Users.Access.LinkExistingUserRequest request, CancellationToken cancellationToken) =>
        OkResult(await _access.LinkExistingUserAsync(GetUserId(), request, cancellationToken), "Usuário vinculado a esta empresa.");

    /// <summary>
    /// Reenvia o link de definição de senha (uso único, 48 h; o anterior deixa de valer) a quem ainda não definiu a
    /// senha. Quem já definiu usa "Esqueci minha senha". O administrador nunca vê o link nem a senha.
    /// </summary>
    [HttpPost("{id:guid}/setup-link")]
    [CompanyRoute(CompanyRouteKind.Write)]
    [Permission(PermissionCodes.Users.Create)]
    [ProducesResponseType(typeof(Result<SIGH.Application.Users.SetupLink.ResendSetupLinkResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Result<SIGH.Application.Users.SetupLink.ResendSetupLinkResponse>>> ResendSetupLink([FromRoute] Guid id, CancellationToken cancellationToken) =>
        OkResult(await _setupLinks.ResendAsync(GetUserId(), id, cancellationToken), "Link de definição de senha reenviado por e-mail.");

    /// <summary>
    /// Situação dos administradores (alerta da tela de usuários): um só Administrador vigente, ou o acesso do último
    /// terminando em menos de 15 dias. Uma linha por empresa da requisição.
    /// </summary>
    [HttpGet("admin-status")]
    [CompanyRoute(CompanyRouteKind.Read)]
    [Permission(PermissionCodes.Users.View)]
    [ProducesResponseType(typeof(Result<IReadOnlyList<SIGH.Application.Users.Access.AdministratorStatusDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Result<IReadOnlyList<SIGH.Application.Users.Access.AdministratorStatusDto>>>> GetAdministratorStatus(CancellationToken cancellationToken) =>
        OkResult(await _administratorStatus.GetAsync(cancellationToken));

    [HttpPatch("{id:guid}/status")]
    [CompanyRoute(CompanyRouteKind.Write)]
    [Permission(PermissionCodes.Users.ChangeStatus)]
    [ProducesResponseType(typeof(Result<ChangeUserStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Result<ChangeUserStatusResponse>>> ChangeStatus([FromRoute] Guid id, [FromBody] ChangeUserStatusRequest request, CancellationToken cancellationToken)
    {
        var updatedRequest = request with { UserId = id };
        var result = await _changeUserStatusService.ChangeUserStatusAsync(updatedRequest, cancellationToken);
        return OkResult(result, "Status do usuário alterado com sucesso.");
    }

    [HttpPost("{id:guid}/unlock")]
    [CompanyRoute(CompanyRouteKind.Write)]
    [Permission(PermissionCodes.Users.Unlock)]
    [ProducesResponseType(typeof(Result<UnlockUserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Result<UnlockUserResponse>>> UnlockUser([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        // O usuário alvo vem da rota e o ator vem exclusivamente do token.
        var updatedRequest = new UnlockUserRequest(id, GetUserId());
        var result = await _unlockUserService.UnlockUserAsync(updatedRequest, cancellationToken);
        return OkResult(result, "Usuário desbloqueado com sucesso.");
    }

    [HttpGet]
    [CompanyRoute(CompanyRouteKind.Read)]
    [Permission(PermissionCodes.Users.View)]
    [ProducesResponseType(typeof(Result<PagedList<UserSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Result<PagedList<UserSummaryDto>>>> GetUsers([FromQuery] GetUsersQuery query, CancellationToken cancellationToken)
    {
        var result = await _getUsersService.GetUsersAsync(query, cancellationToken);
        return OkResult(result, "Listagem de usuários obtida com sucesso.");
    }

    [HttpGet("{id:guid}", Name = nameof(GetUserById))]
    [CompanyRoute(CompanyRouteKind.Read)]
    [Permission(PermissionCodes.Users.View)]
    [ProducesResponseType(typeof(Result<UserDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Result<UserDetailDto>>> GetUserById([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var result = await _getUserByIdService.GetUserByIdAsync(id, cancellationToken);
        return OkResult(result, "Dados do usuário obtidos com sucesso.");
    }
}
