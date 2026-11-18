namespace SIGH.Application.Interfaces;

/// <summary>Dados da requisição HTTP atual usados na auditoria. Fora de uma requisição, tudo é null.</summary>
public interface IRequestContext
{
    string? IpAddress { get; }
    string? UserAgent { get; }
    string? CorrelationId { get; }

    /// <summary>Valor bruto do cabeçalho X-SIGH-Company (empresa ativa pedida pelo cliente; SEMPRE validado).</summary>
    string? CompanyHeader => null;
}
