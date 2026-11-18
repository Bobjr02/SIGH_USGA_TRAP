using SIGH.Domain.Exceptions;

namespace SIGH.Domain.Common;

/// <summary>Atribuição única da empresa dona de um registro: nunca troca de empresa depois de definida.</summary>
public static class CompanyAssignment
{
    public static Guid Assign(Guid current, Guid companyId)
    {
        if (companyId == Guid.Empty)
            throw new BusinessRuleValidationException("A empresa é obrigatória.");
        if (current != Guid.Empty && current != companyId)
            throw new BusinessRuleValidationException("O registro já pertence a outra empresa.");
        return companyId;
    }
}
