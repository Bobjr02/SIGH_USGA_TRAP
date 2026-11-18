import React from 'react';
import { Alert, AlertTitle } from '@mui/material';
import { useAuth } from '../../hooks/useAuth';

/**
 * Para qual empresa o arquivo vai ("Importando para: Trapiche"). No modo "Todas" não há importação: pede uma empresa.
 * @returns true quando dá para importar (há empresa específica).
 */
export const useImportCompany = () => {
  const { activeCompanyInfo, isAllCompanies, companies } = useAuth();
  return { company: activeCompanyInfo, blocked: isAllCompanies, hasCompanies: companies.length > 0 };
};

export const ImportTargetCompany: React.FC = () => {
  const { company, blocked, hasCompanies } = useImportCompany();
  if (!hasCompanies) return null;
  if (blocked)
    return (
      <Alert severity="warning" data-testid="import-company-required">
        O modo &quot;Todas&quot; é só para consulta. Escolha no topo a empresa para a qual o arquivo vai ser importado.
      </Alert>
    );
  return (
    <Alert severity="info" icon={false} data-testid="import-target">
      Importando para: <strong>{company?.name}</strong>
    </Alert>
  );
};

/** Alerta grande no topo da pré-visualização: mais da metade das linhas é de gerência de outra empresa. */
export const OtherCompanyFileAlert: React.FC<{ show?: boolean; rows?: number; companyName?: string }> = ({ show, rows, companyName }) =>
  show ? (
    <Alert severity="error" role="alert" variant="filled" data-testid="other-company-alert" sx={{ fontSize: '1rem' }}>
      <AlertTitle sx={{ fontSize: '1.15rem', fontWeight: 800 }}>Este arquivo parece ser de outra empresa</AlertTitle>
      {rows ?? 0} linha(s) têm gerência de outra empresa e foram rejeitadas. Confira se o arquivo é mesmo de {companyName ?? 'esta empresa'} e
      a empresa selecionada no topo. A confirmação fica bloqueada.
    </Alert>
  ) : null;
