using Microsoft.EntityFrameworkCore;
using SIGH.Domain.Disciplinary.Entities;
using SIGH.Domain.Disciplinary.Enums;
using SIGH.Domain.Employees.Entities;

namespace SIGH.Application.Common.Authorization;

/// <summary>
/// Escopo de dados resolvido para o usuário da requisição, POR EMPRESA:
/// - <see cref="FullCompanyIds"/>: empresas em que ele vê tudo (Disciplinary.Records.ViewAll vigente nelas);
/// - <see cref="ManagementUnitIds"/>: gerências vigentes dele nas demais empresas da requisição (o Gestor).
/// Só entram as empresas da requisição (a empresa ativa, ou todas no modo "Todas") em que o usuário tem as
/// permissões exigidas pela rota. <see cref="Unrestricted"/> é só para código sem usuário (testes de unidade).
/// </summary>
public sealed record AccessScopeFilter(bool Unrestricted, IReadOnlyCollection<Guid> FullCompanyIds, IReadOnlyCollection<Guid> ManagementUnitIds)
{
    public static readonly AccessScopeFilter Everything = new(true, Array.Empty<Guid>(), Array.Empty<Guid>());
    public static readonly AccessScopeFilter Nothing = new(false, Array.Empty<Guid>(), Array.Empty<Guid>());

    /// <summary>O funcionário (empresa + gerência ATUAL) está no escopo?</summary>
    public bool Allows(Guid companyId, Guid managementUnitId) =>
        Unrestricted || FullCompanyIds.Contains(companyId) || ManagementUnitIds.Contains(managementUnitId);

    /// <summary>Alguma parte da empresa está no escopo (catálogos da empresa: motivos, gerências, lotes)?</summary>
    public bool TouchesCompany(Guid companyId, IReadOnlyCollection<Guid> companiesOfUnits) =>
        Unrestricted || FullCompanyIds.Contains(companyId) || companiesOfUnits.Contains(companyId);
}

/// <summary>
/// Escopo por empresa e por gerência, aplicado no SERVIDOR em toda leitura de dados de funcionários e de medidas.
/// Fora do escopo: listas não trazem o registro e o detalhe responde 404 (nunca 403).
/// </summary>
public interface IAccessScope
{
    /// <summary>Há um usuário autenticado (requisição HTTP)? Sem ele (workers, seed) os repositórios não restringem.</summary>
    bool AppliesToRequest => true;

    /// <summary>Filtro pronto (resolvido uma vez por requisição).</summary>
    Task<AccessScopeFilter> GetFilterAsync(CancellationToken cancellationToken = default);

    /// <summary>Empresas visíveis nesta requisição (com escopo total ou por gerência).</summary>
    Task<IReadOnlyList<Guid>> GetCompanyIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Filtros LINQ do escopo, traduzidos para SQL pelo EF (paginação continua no banco).</summary>
public static class AccessScopeQueryExtensions
{
    public static IQueryable<Employee> InScope(this IQueryable<Employee> employees, AccessScopeFilter scope)
    {
        if (scope.Unrestricted)
            return employees;
        var full = scope.FullCompanyIds.ToList();
        var ids = scope.ManagementUnitIds.ToList();
        return employees.Where(e => full.Contains(e.CompanyId) || ids.Contains(e.ManagementUnitId));
    }

    /// <summary>
    /// Processo/medida visível se o funcionário ACUSADO pertence HOJE a uma gerência do escopo:
    /// o histórico acompanha o funcionário quando ele muda de gerência.
    /// </summary>
    public static IQueryable<DisciplinaryCase> InScope(
        this IQueryable<DisciplinaryCase> cases,
        IQueryable<DisciplinaryCaseEmployee> caseEmployees,
        IQueryable<Employee> employees,
        AccessScopeFilter scope)
    {
        if (scope.Unrestricted)
            return cases;
        var full = scope.FullCompanyIds.ToList();
        var ids = scope.ManagementUnitIds.ToList();
        return cases.Where(c => full.Contains(c.CompanyId)
                                || caseEmployees.Any(ce => ce.DisciplinaryCaseId == c.Id
                                                           && ce.Role == CaseEmployeeRole.Accused
                                                           && employees.Any(e => e.Id == ce.EmployeeId && e.CompanyId == c.CompanyId
                                                                                 && ids.Contains(e.ManagementUnitId))));
    }

    /// <summary>Ids dos processos visíveis (para filtrar tabelas que só têm o DisciplinaryCaseId).</summary>
    public static IQueryable<Guid> VisibleCaseIds(
        IQueryable<DisciplinaryCase> cases,
        IQueryable<DisciplinaryCaseEmployee> caseEmployees,
        IQueryable<Employee> employees,
        AccessScopeFilter scope) =>
        cases.InScope(caseEmployees, employees, scope).Select(c => c.Id);
}
