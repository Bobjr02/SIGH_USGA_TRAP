using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SIGH.Application.Common.Authorization;
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
    /// Gerências (Chave Organização do SAP) de Seed:ManagementUnits (código e nome). Só cria as que
    /// faltam: nomes já existentes nunca são sobrescritos, para continuarem editáveis.
    /// </summary>
    private async Task EnsureManagementUnitsAsync(CancellationToken cancellationToken)
    {
        var configured = _configuration.GetSection("Seed:ManagementUnits").GetChildren()
            .Select(s => (Code: s["Code"]?.Trim(), Name: s["Name"]?.Trim()))
            .Where(x => !string.IsNullOrEmpty(x.Code) && !string.IsNullOrEmpty(x.Name))
            .ToList();
        if (configured.Count == 0)
            return;

        var existingCodes = await _context.ManagementUnits
            .IgnoreQueryFilters()
            .Where(m => m.CompanyId == DefaultCompanyId && m.Code != null)
            .Select(m => m.Code!)
            .ToListAsync(cancellationToken);

        var created = 0;
        foreach (var (code, name) in configured)
        {
            if (existingCodes.Contains(code!, StringComparer.OrdinalIgnoreCase))
                continue;
            await _context.ManagementUnits.AddAsync(ManagementUnit.Create(DefaultCompanyId, name!, code), cancellationToken);
            created++;
        }

        if (created > 0)
            _logger.LogInformation("Seed: {Count} gerências criadas.", created);
    }
}
