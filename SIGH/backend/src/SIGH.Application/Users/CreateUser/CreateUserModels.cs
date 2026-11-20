namespace SIGH.Application.Users.CreateUser;

/// <summary>
/// Não há campo de senha: a conta nasce com uma senha aleatória que ninguém conhece e a pessoa recebe por e-mail um
/// link de uso único (48 h) para definir a própria senha. O administrador nunca conhece a senha de ninguém.
/// O e-mail precisa ser de um domínio de Auth:AllowedEmailDomains (padrão @usga.com.br).
/// </summary>
public record CreateUserRequest(
    string FullName,
    string Email,
    string Cpf,
    List<Guid> RoleIds,
    // Gerências (obrigatório se um dos perfis for Gestor).
    List<Guid>? ManagementUnitIds = null
);

public record CreateUserResponse(
    Guid Id,
    string FullName,
    string Email,
    string Cpf,
    string Status,
    bool MustChangePassword
);
