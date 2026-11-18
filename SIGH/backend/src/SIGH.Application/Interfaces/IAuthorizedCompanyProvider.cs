namespace SIGH.Application.Interfaces;

/// <summary>
/// Empresas da requisição para os endpoints antigos de relatório e de notificações (que recebem um CompanyId).
/// Implementado sobre o <see cref="SIGH.Application.Common.Authorization.ICompanyContext"/>: nunca aceita uma empresa
/// fora das empresas da requisição.
/// </summary>
public interface IAuthorizedCompanyProvider
{
    Task<IReadOnlyList<Guid>> GetAuthorizedCompanyIdsAsync(CancellationToken cancellationToken = default);
    Task<bool> IsCompanyAuthorizedAsync(Guid companyId, CancellationToken cancellationToken = default);
    Task<Guid> GetAuthorizedCompanyIdAsync(Guid? requestedCompanyId, CancellationToken cancellationToken = default);
}
