namespace SIGH.Domain.Entities;

public class AuditLog : AuditableEntity
{
    /// <summary>
    /// Empresa do registro auditado (a do próprio registro, se ele tiver CompanyId; senão a empresa ativa da
    /// requisição). Nulo: evento sem empresa (ex.: login), visível só a quem tem Audit.View em todas as empresas.
    /// </summary>
    public Guid? CompanyId { get; set; }

    public Guid? UserId { get; set; }
    public virtual User? User { get; set; }

    public string Action { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? CorrelationId { get; set; }

    public AuditLog() : base() { }

    public AuditLog(Guid id) : base(id) { }
}
