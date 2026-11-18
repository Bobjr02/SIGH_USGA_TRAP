namespace SIGH.Application.Common.Authorization;

/// <summary>
/// Empresa ativa (específica) da requisição, depois de validada pelo <see cref="ICompanyContext"/>. Sem dependências:
/// a auditoria (DbContext) lê daqui sem criar ciclo de injeção. Nulo: sem empresa (login) ou modo "Todas".
/// </summary>
public sealed class ActiveCompanyHolder
{
    public Guid? CompanyId { get; set; }
}
