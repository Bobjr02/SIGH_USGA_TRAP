namespace SIGH.Application.Authentication.CurrentUser;

/// <summary>
/// Dados do usuário autenticado (GET /api/v1/auth/me). Perfis e permissões são lidos do banco a cada chamada,
/// POR EMPRESA (<see cref="Companies"/>): a tela usa as permissões da empresa ativa. <see cref="Roles"/> e
/// <see cref="Permissions"/> são a união de todas as empresas, só para exibição (nunca para decidir acesso).
/// </summary>
public record CurrentUserResponse(
    Guid Id,
    string FullName,
    string Email,
    string Status,
    bool MustChangePassword,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<CurrentUserCompanyDto>? Companies = null
);

/// <summary>Uma empresa em que o usuário tem perfil vigente: perfis, permissões e gerências NELA.</summary>
public record CurrentUserCompanyDto(
    Guid Id,
    string Code,
    string Name,
    string ManagementUnitPrefix,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Guid> ManagementUnitIds
);
