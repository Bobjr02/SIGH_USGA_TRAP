using Microsoft.EntityFrameworkCore;
using SIGH.Domain.Employees.Entities;
using SIGH.Domain.Employees.Helpers;
using SIGH.Domain.Repositories;
using SIGH.Persistence.Context;

namespace SIGH.Persistence.Repositories;

public class EmployeeRepository : IEmployeeRepository
{
    private readonly SighDbContext _context;
    private readonly SIGH.Application.Common.Authorization.IAccessScope? _scope;

    public EmployeeRepository(SighDbContext context, SIGH.Application.Common.Authorization.IAccessScope? scope = null)
    {
        _context = context;
        _scope = scope;
    }

    /// <summary>Funcionário ao alcance da requisição (empresa e gerência); fora dele, "não existe". Sem usuário, sem restrição.</summary>
    public async Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        IQueryable<Employee> employees = _context.Employees;
        if (_scope is not null && _scope.AppliesToRequest)
            employees = SIGH.Application.Common.Authorization.AccessScopeQueryExtensions.InScope(employees, await _scope.GetFilterAsync(cancellationToken));
        return await employees.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    /// <summary>A regra de CPF vale DENTRO de cada empresa: só os funcionários das empresas da requisição contam.</summary>
    public async Task<bool> ExistsByCpfAsync(string cpf, Guid? excludingEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var normalizedCpf = CpfValidator.Normalize(cpf);
        List<Guid>? companies = _scope is not null && _scope.AppliesToRequest ? (await _scope.GetCompanyIdsAsync(cancellationToken)).ToList() : null;
        return await _context.Employees
            .AsNoTracking()
            .AnyAsync(e => e.Cpf == normalizedCpf && (!excludingEmployeeId.HasValue || e.Id != excludingEmployeeId.Value)
                           && (companies == null || companies.Contains(e.CompanyId)), cancellationToken);
    }

    public async Task<bool> ExistsByEmployeeNumberAsync(Guid companyId, string employeeNumber, Guid? excludingEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var trimmedNumber = employeeNumber.Trim();
        return await _context.Employees
            .AsNoTracking()
            .AnyAsync(e => e.CompanyId == companyId && e.EmployeeNumber == trimmedNumber && (!excludingEmployeeId.HasValue || e.Id != excludingEmployeeId.Value), cancellationToken);
    }

    public async Task<bool> ExistsByCorporateEmailAsync(string corporateEmail, Guid? excludingEmployeeId = null, CancellationToken cancellationToken = default)
    {
        var trimmedEmail = corporateEmail.Trim();
        return await _context.Employees
            .AsNoTracking()
            .AnyAsync(e => e.CorporateEmail == trimmedEmail && (!excludingEmployeeId.HasValue || e.Id != excludingEmployeeId.Value), cancellationToken);
    }

    public async Task<bool> IsUserLinkedAsync(Guid userId, Guid? excludingEmployeeId = null, CancellationToken cancellationToken = default)
    {
        return await _context.Employees
            .AsNoTracking()
            .AnyAsync(e => e.UserId == userId && (!excludingEmployeeId.HasValue || e.Id != excludingEmployeeId.Value), cancellationToken);
    }

    public async Task AddAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        await _context.Employees.AddAsync(employee, cancellationToken);
    }
}
