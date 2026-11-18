namespace SIGH.Application.Common.Authorization;

/// <summary>Ids fixos das empresas do seed (os dados de antes da multiempresa ficam na USGA).</summary>
public static class CompanyDefaults
{
    /// <summary>USGA: o Id da antiga "empresa padrão".</summary>
    public static readonly Guid UsgaId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>Trapiche.</summary>
    public static readonly Guid TrapicheId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public const string UsgaCode = "USGA";
    public const string TrapicheCode = "TRAP";
}
