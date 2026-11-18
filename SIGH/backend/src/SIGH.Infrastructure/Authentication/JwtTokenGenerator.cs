using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SIGH.Application.Common.Authorization;
using SIGH.Application.Interfaces;
using SIGH.Application.Options;
using SIGH.Domain.Entities;

namespace SIGH.Infrastructure.Authentication;

public class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly JwtOptions _jwtOptions;

    public JwtTokenGenerator(IOptions<JwtOptions> jwtOptions)
    {
        _jwtOptions = jwtOptions.Value;
    }

    public string GenerateToken(User user, IEnumerable<string> roles, IEnumerable<string> permissions, DateTimeOffset? accessChangesAt = null)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        // A chave é validada no startup (JwtOptions + ValidateOnStart); não há segredo de fallback.
        var key = Encoding.UTF8.GetBytes(_jwtOptions.SecretKey);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.FullName),
            new("status", user.Status.ToString()),
            new(SighClaimTypes.SecurityStamp, user.SecurityStamp)
        };

        if (accessChangesAt is { } changesAt)
        {
            claims.Add(new Claim(SighClaimTypes.AccessChangesAt, changesAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        }

        // CPF nunca vai no token (dado pessoal, e o JWT é apenas codificado, não cifrado).
        if (user.MustChangePassword)
        {
            claims.Add(new Claim(SighClaimTypes.PasswordChangeRequired, "true"));
        }

        // Perfis e permissões são POR EMPRESA e lidos do banco a cada requisição (ICompanyContext): nada disso vai
        // no token, que não pode ser a fonte de autorização (a empresa ativa muda sem novo token). Qualquer mudança
        // de perfil, em qualquer empresa, troca o SecurityStamp e derruba o token.

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_jwtOptions.ExpiryInMinutes > 0 ? _jwtOptions.ExpiryInMinutes : 60),
            Issuer = _jwtOptions.Issuer,
            Audience = _jwtOptions.Audience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
