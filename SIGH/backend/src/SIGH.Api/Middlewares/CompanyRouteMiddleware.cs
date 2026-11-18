using SIGH.Application.Common.Authorization;

namespace SIGH.Api.Middlewares;

/// <summary>
/// Rotas <see cref="CompanyRouteKind.Write"/> exigem uma empresa ESPECÍFICA ativa: no modo "Todas" a resposta é
/// 400 (COMPANY_REQUIRED) antes de chegar ao controller. A empresa pedida também é validada aqui (404 se o usuário
/// não tiver perfil vigente nela). Roda depois da autenticação e da autorização.
/// </summary>
public sealed class CompanyRouteMiddleware
{
    private readonly RequestDelegate _next;

    public CompanyRouteMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICompanyContext companies)
    {
        var route = context.GetEndpoint()?.Metadata.GetMetadata<CompanyRouteAttribute>();
        if (route is { Kind: CompanyRouteKind.Write } && context.User.Identity?.IsAuthenticated == true)
        {
            var selection = await companies.GetSelectionAsync(context.RequestAborted);
            if (selection.IsAll || selection.CompanyId is null)
                throw new CompanyRequiredException();
        }

        await _next(context);
    }
}
