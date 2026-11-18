namespace SIGH.Application.Interfaces;

/// <summary>
/// Empresas da requisição para os endpoints que aceitam um <c>companyId</c> opcional (notificações). Implementado sobre
/// o <see cref="SIGH.Application.Common.Authorization.ICompanyContext"/>: nunca aceita uma empresa fora das empresas da
/// requisição e nunca escolhe uma empresa por conta própria.
/// </summary>
public interface IAuthorizedCompanyProvider
{
    Task<IReadOnlyList<Guid>> GetAuthorizedCompanyIdsAsync(CancellationToken cancellationToken = default);
    Task<bool> IsCompanyAuthorizedAsync(Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empresas que a requisição consulta. Com <paramref name="requestedCompanyId"/>: só ela, se estiver entre as
    /// empresas da requisição; fora delas, <see cref="SIGH.Application.Common.Authorization.CompanyNotAccessibleException"/>
    /// (404 — nunca 401, que a tela entende como sessão expirada). Sem empresa pedida: TODAS as empresas da requisição
    /// (a empresa ativa, ou todas no modo "Todas").
    /// </summary>
    Task<IReadOnlyList<Guid>> ResolveCompanyIdsAsync(Guid? requestedCompanyId, CancellationToken cancellationToken = default);
}
