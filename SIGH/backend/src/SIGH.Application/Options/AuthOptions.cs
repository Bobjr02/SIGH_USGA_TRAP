namespace SIGH.Application.Options;

/// <summary>Configuração de autenticação (seção "Auth").</summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Janela, em segundos, em que um refresh token já rotacionado ainda é aceito sem disparar a
    /// detecção de reuso. Cobre duas abas renovando ao mesmo tempo com o mesmo cookie.
    /// </summary>
    public int RefreshTokenReuseGraceSeconds { get; set; } = 30;

    /// <summary>Validade do link de redefinição de senha ("esqueci a senha").</summary>
    public int PasswordResetTokenMinutes { get; set; } = 30;

    /// <summary>Validade do link de definição de senha enviado quando a conta é criada (padrão 48 h).</summary>
    public int AccountSetupTokenHours { get; set; } = 48;

    /// <summary>Domínio aceito quando <see cref="AllowedEmailDomains"/> não é configurado.</summary>
    public const string DefaultEmailDomain = "usga.com.br";

    /// <summary>
    /// Domínios de e-mail aceitos em contas NOVAS (Auth:AllowedEmailDomains:0, :1...). Sem configuração, só
    /// <see cref="DefaultEmailDomain"/>. A demonstração e os testes acrescentam os domínios fictícios deles.
    /// </summary>
    public string[] AllowedEmailDomains { get; set; } = [];

    /// <summary>Domínios efetivos: os configurados (sem "@", minúsculos) ou o padrão.</summary>
    public IReadOnlyList<string> EffectiveEmailDomains()
    {
        var configured = (AllowedEmailDomains ?? [])
            .Select(d => d.Trim().TrimStart('@').ToLowerInvariant())
            .Where(d => d.Length > 0)
            .Distinct()
            .ToList();
        return configured.Count > 0 ? configured : [DefaultEmailDomain];
    }

    /// <summary>O e-mail termina exatamente em "@" + um dos domínios aceitos (subdomínio não vale)?</summary>
    public bool IsEmailDomainAllowed(string email)
    {
        var at = email.Trim().LastIndexOf('@');
        if (at <= 0 || at == email.Trim().Length - 1)
            return false;
        var domain = email.Trim()[(at + 1)..].ToLowerInvariant();
        return EffectiveEmailDomains().Contains(domain);
    }

    /// <summary>Mensagem para e-mail fora dos domínios aceitos.</summary>
    public string EmailDomainMessage() =>
        "Use um e-mail corporativo: " + string.Join(", ", EffectiveEmailDomains().Select(d => "@" + d)) + ".";

    /// <summary>Teto do cache do SecurityStamp: a perda de acesso nunca demora mais que isto.</summary>
    public const int MaxSecurityStampCacheSeconds = 30;

    /// <summary>
    /// Por quanto tempo (segundos) o stamp e o status do usuário ficam em cache na API antes de serem
    /// relidos do banco. Limitado a <see cref="MaxSecurityStampCacheSeconds"/>; 0 desliga o cache.
    /// </summary>
    public int SecurityStampCacheSeconds { get; set; } = MaxSecurityStampCacheSeconds;
}
