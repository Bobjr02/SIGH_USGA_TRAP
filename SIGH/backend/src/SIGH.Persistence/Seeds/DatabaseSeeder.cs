using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Employees.Common;
using SIGH.Domain.Employees;
using SIGH.Application.Interfaces;
using SIGH.Persistence.Context;

namespace SIGH.Persistence.Seeds;

/// <summary>
/// Seed idempotente dos dados mínimos para o sistema funcionar num banco novo:
/// catálogo de permissões, perfis de sistema (<see cref="SystemRoles"/>), empresas e o primeiro administrador de cada
/// empresa (sem senha: link de definição por e-mail, via <see cref="SIGH.Application.Users.Bootstrap.ICompanyAdminProvisioner"/>).
/// Pode ser executado a cada inicialização: só insere o que estiver faltando, e as permissões de cada
/// perfil de sistema são sincronizadas com a lista fixa (o que sobrar é retirado logicamente).
/// </summary>
public sealed class DatabaseSeeder
{
    public const string AdministratorRoleName = SystemRoles.Administrator;

    /// <summary>
    /// USGA: a antiga "empresa padrão" (os dados de antes da multiempresa ficam nela).
    /// </summary>
    public static readonly Guid DefaultCompanyId = CompanyDefaults.UsgaId;

    /// <summary>Trapiche (criada pela migration MultiCompany e pelo seed).</summary>
    public static readonly Guid TrapicheCompanyId = CompanyDefaults.TrapicheId;

    private const string FallbackCompanyName = "Usina Serra Grande S.A.";
    private const string TrapicheDisplayName = "Trapiche";
    // Nome antigo do seed: renomeado automaticamente para o nome configurado.
    private const string LegacyCompanyName = "Empresa Padrão";

    private readonly SighDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseSeeder> _logger;
    private readonly SIGH.Application.Users.Bootstrap.ICompanyAdminProvisioner? _companyAdmins;

    public DatabaseSeeder(
        SighDbContext context,
        IPasswordHasher passwordHasher,
        IConfiguration configuration,
        ILogger<DatabaseSeeder> logger,
        SIGH.Application.Users.Bootstrap.ICompanyAdminProvisioner? companyAdmins = null)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
        _logger = logger;
        _companyAdmins = companyAdmins;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var permissions = await EnsurePermissionsAsync(cancellationToken);
        await EnsureSystemRolesAsync(permissions, cancellationToken);
        await EnsureDefaultCompanyAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await EnsureConfiguredCompaniesAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        await EnsureManagementUnitsAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        // Primeiro administrador de cada empresa (Seed:CompanyAdmins e as chaves antigas Seed:AdminEmail e
        // Seed:Companies:<código>:AdminEmail): só para empresa SEM Administrador vigente, com link de definição de
        // senha por e-mail. Bancos que já têm administrador não mudam.
        if (_companyAdmins is not null)
            await _companyAdmins.ProvisionMissingAsync(
                SIGH.Application.Users.Bootstrap.CompanyAdminProvisioner.ReadConfiguration(_configuration, _logger), cancellationToken);
        else if (_configuration["Seed:AdminEmail"] is { Length: > 0 })
            _logger.LogWarning("Seed: sem o serviço de primeiro administrador; nenhum administrador foi criado.");
    }

    /// <summary>
    /// Empresas de Seed:Companies:&lt;código&gt; (Name, ManagementUnitPrefix; o primeiro administrador vem de
    /// Seed:CompanyAdmins ou de Seed:Companies:&lt;código&gt;:AdminEmail). USGA usa o Id antigo da
    /// empresa padrão; TRAP, o Id fixo da Trapiche. Só cria o que falta: o nome de exibição continua editável no banco
    /// (nenhum nome jurídico é inventado). Não há tela para criar empresas nesta versão.
    /// </summary>
    private async Task<List<(Company Company, IConfigurationSection Section)>> EnsureConfiguredCompaniesAsync(CancellationToken cancellationToken)
    {
        var result = new List<(Company, IConfigurationSection)>();
        foreach (var section in _configuration.GetSection("Seed:Companies").GetChildren())
        {
            var code = section.Key.Trim().ToUpperInvariant();
            if (code.Length == 0)
                continue;

            var id = code switch
            {
                CompanyDefaults.UsgaCode => DefaultCompanyId,
                CompanyDefaults.TrapicheCode => TrapicheCompanyId,
                _ => (Guid?)null
            };

            var company = await _context.Companies.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Code == code || (id != null && c.Id == id), cancellationToken);
            if (company is null)
            {
                company = id is { } fixedId ? new Company(fixedId) : new Company();
                company.UpdateName(section["Name"] is { Length: > 0 } name ? name : code == CompanyDefaults.TrapicheCode ? TrapicheDisplayName : code);
                company.SetCode(code);
                company.SetManagementUnitPrefix(section["ManagementUnitPrefix"] is { Length: > 0 } prefix ? prefix : code);
                await _context.Companies.AddAsync(company, cancellationToken);
                _logger.LogInformation("Seed: empresa {Code} criada.", code);
            }
            else
            {
                if (string.IsNullOrEmpty(company.Code))
                    company.SetCode(code);
                if (string.IsNullOrEmpty(company.ManagementUnitPrefix))
                    company.SetManagementUnitPrefix(section["ManagementUnitPrefix"] is { Length: > 0 } prefix ? prefix : code);
            }

            result.Add((company, section));
        }
        return result;
    }

    private async Task<IReadOnlyList<Permission>> EnsurePermissionsAsync(CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: uma permissão excluída logicamente é restaurada em vez de duplicada.
        var existing = await _context.Permissions
            .IgnoreQueryFilters()
            .ToDictionaryAsync(p => p.Code, StringComparer.Ordinal, cancellationToken);

        var result = new List<Permission>();
        var inserted = 0;

        foreach (var definition in PermissionCodes.All)
        {
            if (existing.TryGetValue(definition.Code, out var permission))
            {
                if (permission.IsDeleted)
                {
                    permission.IsDeleted = false;
                    permission.DeletedAt = null;
                    permission.DeletedBy = null;
                }
            }
            else
            {
                permission = new Permission
                {
                    Code = definition.Code,
                    Name = definition.Name,
                    Description = definition.Description
                };
                await _context.Permissions.AddAsync(permission, cancellationToken);
                inserted++;
            }

            result.Add(permission);
        }

        // Permissão que saiu do código (ex.: Disciplinary.Reports.ViewCases, dos relatórios antigos removidos): exclusão
        // lógica. Os perfis do sistema já a perdem em SyncRolePermissionsAsync.
        var codes = PermissionCodes.All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);
        var obsolete = existing.Values.Where(p => !p.IsDeleted && !codes.Contains(p.Code)).ToList();
        foreach (var permission in obsolete)
        {
            permission.IsDeleted = true;
            permission.DeletedAt = DateTimeOffset.UtcNow;
        }
        if (obsolete.Count > 0)
            _logger.LogInformation("Seed: {Count} permissões que saíram do código foram desativadas.", obsolete.Count);

        if (inserted > 0)
            _logger.LogInformation("Seed: {Count} permissões inseridas.", inserted);

        return result;
    }

    private async Task<Dictionary<string, Role>> EnsureSystemRolesAsync(IReadOnlyList<Permission> permissions, CancellationToken cancellationToken)
    {
        var permissionByCode = permissions.ToDictionary(p => p.Code, StringComparer.Ordinal);
        var result = new Dictionary<string, Role>(StringComparer.Ordinal);

        foreach (var definition in SystemRoles.All)
        {
            var role = await _context.Roles
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Name == definition.Name, cancellationToken);

            if (role is null)
            {
                role = new Role { Name = definition.Name, Description = definition.Description };
                await _context.Roles.AddAsync(role, cancellationToken);
                _logger.LogInformation("Seed: perfil {Role} criado.", definition.Name);
            }
            else
            {
                if (role.IsDeleted)
                {
                    role.IsDeleted = false;
                    role.DeletedAt = null;
                    role.DeletedBy = null;
                }
                if (role.Description != definition.Description)
                    role.Description = definition.Description;
            }

            await SyncRolePermissionsAsync(role, definition, permissionByCode, cancellationToken);
            result[definition.Name] = role;
        }

        return result;
    }

    /// <summary>
    /// Deixa o perfil com EXATAMENTE as permissões da definição: concede as que faltam e retira
    /// (exclusão lógica, auditada) as que sobram — é assim que o Administrador de bancos antigos
    /// perde as permissões de medida e do processo em etapas.
    /// </summary>
    private async Task SyncRolePermissionsAsync(
        Role role,
        SystemRoleDefinition definition,
        IReadOnlyDictionary<string, Permission> permissionByCode,
        CancellationToken cancellationToken)
    {
        var wanted = definition.Permissions.Select(code => permissionByCode[code].Id).ToHashSet();

        var links = await _context.RolePermissions
            .IgnoreQueryFilters()
            .Where(rp => rp.RoleId == role.Id)
            .ToListAsync(cancellationToken);

        var granted = 0;
        var revoked = 0;
        foreach (var link in links)
        {
            if (wanted.Contains(link.PermissionId))
            {
                if (link.IsDeleted)
                {
                    link.IsDeleted = false;
                    link.DeletedAt = null;
                    link.DeletedBy = null;
                    granted++;
                }
            }
            else if (!link.IsDeleted)
            {
                link.IsDeleted = true;
                link.DeletedAt = DateTimeOffset.UtcNow;
                revoked++;
            }
        }

        var existing = links.Select(l => l.PermissionId).ToHashSet();
        foreach (var permissionId in wanted.Where(id => !existing.Contains(id)))
        {
            await _context.RolePermissions.AddAsync(new RolePermission { RoleId = role.Id, PermissionId = permissionId }, cancellationToken);
            granted++;
        }

        if (granted > 0 || revoked > 0)
            _logger.LogInformation("Seed: perfil {Role}: {Granted} permissões concedidas, {Revoked} retiradas.", definition.Name, granted, revoked);
    }

    private async Task EnsureDefaultCompanyAsync(CancellationToken cancellationToken)
    {
        var name = _configuration["Seed:DefaultCompanyName"];
        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogWarning(
                "Seed: 'Seed:DefaultCompanyName' não configurado. A empresa padrão usa o nome '{Name}'.",
                FallbackCompanyName);
            name = FallbackCompanyName;
        }

        var existing = await _context.Companies
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == DefaultCompanyId, cancellationToken);

        if (existing is not null)
        {
            if (string.IsNullOrEmpty(existing.Code))
                existing.SetCode(CompanyDefaults.UsgaCode);
            if (string.IsNullOrEmpty(existing.ManagementUnitPrefix))
                existing.SetManagementUnitPrefix(CompanyDefaults.UsgaCode);
            // Só troca o nome legado do seed; um nome alterado por alguém é preservado.
            if (existing.Name == LegacyCompanyName && name != LegacyCompanyName)
            {
                existing.UpdateName(name);
                _logger.LogInformation("Seed: empresa padrão renomeada para {Name}.", name);
            }
            return;
        }

        var company = new Company(DefaultCompanyId);
        company.UpdateName(name);
        company.SetCode(CompanyDefaults.UsgaCode);
        company.SetManagementUnitPrefix(CompanyDefaults.UsgaCode);
        await _context.Companies.AddAsync(company, cancellationToken);
        _logger.LogInformation("Seed: empresa padrão {CompanyId} criada.", DefaultCompanyId);
    }

    /// <summary>
    /// Gerências (Chave Organização do SAP): a lista oficial do cliente (<see cref="OfficialManagementUnits"/>), criada ou
    /// atualizada por código, cada uma na empresa do seu prefixo (Company.ManagementUnitPrefix), ativa e com o nome oficial.
    /// O nome oficial substitui o do seed antigo e o nome provisório (o próprio código, das gerências criadas pela
    /// importação); um nome editado por alguém na tela é mantido. Chave que está no banco e não está na lista: desativada
    /// se não tiver funcionários, medidas nem gestores vinculados; se tiver, continua ativa e o log registra um aviso com
    /// as quantidades. Nada é apagado.
    /// </summary>
    private async Task EnsureManagementUnitsAsync(CancellationToken cancellationToken)
    {
        var companies = await _context.Companies.IgnoreQueryFilters()
            .Where(c => c.ManagementUnitPrefix != "")
            .ToListAsync(cancellationToken);
        var units = await _context.ManagementUnits.IgnoreQueryFilters().ToListAsync(cancellationToken);

        var created = 0;
        var updated = 0;
        var official = new HashSet<Guid>();
        foreach (var item in OfficialManagementUnits.All)
        {
            var company = companies.FirstOrDefault(c => ManagementUnitCodes.IsValid(item.Code, c.ManagementUnitPrefix));
            if (company is null)
            {
                _logger.LogWarning("Seed: nenhuma empresa tem o prefixo da chave {Code}; a gerência não foi criada.", item.Code);
                continue;
            }

            var unit = units.FirstOrDefault(u => u.CompanyId == company.Id && string.Equals(u.Code, item.Code, StringComparison.OrdinalIgnoreCase));
            if (unit is null)
            {
                unit = ManagementUnit.Create(company.Id, item.Name, item.Code);
                await _context.ManagementUnits.AddAsync(unit, cancellationToken);
                units.Add(unit);
                created++;
            }
            else
            {
                var changed = false;
                if (unit.IsDeleted)
                {
                    unit.IsDeleted = false;
                    unit.DeletedAt = null;
                    unit.DeletedBy = null;
                    changed = true;
                }
                if (!unit.IsActive)
                {
                    unit.Activate();
                    changed = true;
                }
                if (unit.Code != item.Code)
                {
                    unit.UpdateCode(item.Code);
                    changed = true;
                }
                if (unit.Name != item.Name)
                {
                    if (IsProvisionalName(unit.Name, item.Code))
                    {
                        unit.UpdateName(item.Name);
                        changed = true;
                    }
                    else
                        _logger.LogInformation("Seed: gerência {Code} mantém o nome editado \"{Name}\" (nome oficial: \"{Official}\").", item.Code, unit.Name, item.Name);
                }
                if (changed)
                    updated++;
            }
            official.Add(unit.Id);
        }

        if (created > 0)
            _logger.LogInformation("Seed: {Count} gerências da lista oficial criadas.", created);
        if (updated > 0)
            _logger.LogInformation("Seed: {Count} gerências da lista oficial atualizadas.", updated);

        await DeactivateUnofficialUnitsAsync(units.Where(u => u.Code != null && !u.IsDeleted && u.IsActive && !official.Contains(u.Id)).ToList(), cancellationToken);
    }

    /// <summary>O nome atual não foi escolhido por uma pessoa: o código (gerência criada pela importação) ou o nome do seed antigo.</summary>
    private static bool IsProvisionalName(string name, string code) =>
        string.Equals(name, code, StringComparison.OrdinalIgnoreCase)
        || OfficialManagementUnits.LegacyNames.TryGetValue(name, out var legacyCode) && legacyCode == code;

    private async Task DeactivateUnofficialUnitsAsync(List<ManagementUnit> unofficial, CancellationToken cancellationToken)
    {
        if (unofficial.Count == 0)
            return;

        var ids = unofficial.Select(u => u.Id).ToList();
        var employees = await _context.Employees.IgnoreQueryFilters().Where(e => ids.Contains(e.ManagementUnitId))
            .GroupBy(e => e.ManagementUnitId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var measures = await _context.DisciplinaryCaseEmployees.IgnoreQueryFilters()
            .Where(x => x.ManagementUnitIdAtTime != null && ids.Contains(x.ManagementUnitIdAtTime.Value))
            .GroupBy(x => x.ManagementUnitIdAtTime!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var managers = await _context.UserManagementUnits.Where(x => ids.Contains(x.ManagementUnitId))
            .GroupBy(x => x.ManagementUnitId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var deactivated = 0;
        foreach (var unit in unofficial)
        {
            var e = employees.GetValueOrDefault(unit.Id);
            var m = measures.GetValueOrDefault(unit.Id);
            var g = managers.GetValueOrDefault(unit.Id);
            if (e == 0 && m == 0 && g == 0)
            {
                unit.Deactivate();
                deactivated++;
                _logger.LogInformation("Seed: gerência {Code} ({Name}) fora da lista oficial e sem vínculos: desativada.", unit.Code ?? "(sem código)", unit.Name);
                continue;
            }

            _logger.LogWarning(
                "Seed: gerência {Code} ({Name}) fora da lista oficial continua ativa porque tem vínculos: {Employees} funcionários, {Measures} medidas, {Managers} gestores. Transfira os vínculos para uma chave oficial.",
                unit.Code ?? "(sem código)", unit.Name, e, m, g);
        }

        if (deactivated > 0)
            _logger.LogInformation("Seed: {Count} gerências fora da lista oficial desativadas.", deactivated);
    }
}
