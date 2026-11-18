using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SIGH.Application.Common.Authorization;

namespace SIGH.Infrastructure.Authorization;

/// <summary>
/// Avalia a permissão no contexto de empresa da requisição (<see cref="ICompanyContext"/>): ela precisa existir,
/// vigente, na empresa ativa (ou em alguma empresa, no modo "Todas"). As permissões vêm do banco, nunca do token.
/// A permissão aprovada também restringe as leituras às empresas em que ela existe; as escritas são conferidas
/// de novo na empresa do RECURSO pelos serviços.
/// </summary>
public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IHttpContextAccessor _accessor;

    public PermissionHandler(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User == null || !context.User.Identity?.IsAuthenticated == true)
            return;

        var http = context.Resource as HttpContext ?? _accessor.HttpContext;
        var companies = http?.RequestServices.GetService<ICompanyContext>();
        if (companies is null)
            return;

        if (await companies.AuthorizeAsync(requirement.Permission, http!.RequestAborted))
            context.Succeed(requirement);
    }
}
