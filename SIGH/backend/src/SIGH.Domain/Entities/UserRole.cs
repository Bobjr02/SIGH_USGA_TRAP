namespace SIGH.Domain.Entities;

public class UserRole : AuditableEntity, IHasValidity
{
    public Guid UserId { get; set; }
    public virtual User User { get; set; } = null!;

    /// <summary>
    /// Empresa em que o perfil vale. Um mesmo usuário pode ter perfis diferentes em empresas diferentes;
    /// cada atribuição tem a própria validade.
    /// </summary>
    public Guid CompanyId { get; set; }

    public Guid RoleId { get; set; }
    public virtual Role Role { get; set; } = null!;

    /// <summary>Início da vigência (inclusivo). Nulo: desde sempre.</summary>
    public DateTimeOffset? ValidFrom { get; set; }

    /// <summary>Fim da vigência (exclusivo). Nulo: sem prazo. Uso típico: substituto nas férias.</summary>
    public DateTimeOffset? ValidUntil { get; set; }

    public UserRole() : base() { }

    public UserRole(Guid id) : base(id) { }
}
