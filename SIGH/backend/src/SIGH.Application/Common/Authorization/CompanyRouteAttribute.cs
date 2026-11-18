namespace SIGH.Application.Common.Authorization;

/// <summary>Classificação de uma rota quanto à empresa (obrigatória em TODA ação; conferida por teste).</summary>
public enum CompanyRouteKind
{
    /// <summary>Não lê nem grava dados de empresa (login, saúde, dados do próprio usuário).</summary>
    NotApplicable = 0,

    /// <summary>Lê dados de empresa: restrita às empresas da requisição; o modo "Todas" é aceito.</summary>
    Read = 1,

    /// <summary>Grava dados de empresa: exige uma empresa ESPECÍFICA ativa e a permissão nela (o modo "Todas" é recusado).</summary>
    Write = 2,

    /// <summary>Altera só um dado do próprio usuário (marcar o próprio aviso como lido), sempre dentro das empresas dele.</summary>
    Personal = 3
}

/// <summary>
/// Diz como a rota trata a empresa. <see cref="CompanyRouteKind.Write"/> é imposta no servidor pelo
/// CompanyRouteMiddleware: no modo "Todas" a resposta é 400 com o código COMPANY_REQUIRED.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class CompanyRouteAttribute : Attribute
{
    public CompanyRouteAttribute(CompanyRouteKind kind, string? reason = null)
    {
        Kind = kind;
        Reason = reason;
    }

    public CompanyRouteKind Kind { get; }

    /// <summary>Motivo (obrigatório para NotApplicable e Personal).</summary>
    public string? Reason { get; }
}
