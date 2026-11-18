import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { MenuItem, TextField } from '@mui/material';
import { Building2 } from 'lucide-react';
import { useAuth } from '../hooks/useAuth';
import { ALL_COMPANIES } from '../auth/companyStore';

const GUID = /\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(\/.*)?$/i;

/**
 * Seletor "Empresa: USGA | Trapiche | Todas", no topo, só para quem tem perfil em duas ou mais empresas.
 * "Todas" é só leitura. Num detalhe (registro de uma empresa) a troca volta para a lista.
 */
export const CompanySwitcher: React.FC = () => {
  const { companies, activeCompany, canSwitchCompany, setActiveCompany } = useAuth();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  if (!canSwitchCompany) return null;

  const change = (code: string) => {
    if (code === activeCompany) return;
    setActiveCompany(code);
    if (GUID.test(pathname)) navigate(pathname.replace(GUID, ''), { replace: true });
  };

  return (
    <TextField
      select
      size="small"
      label="Empresa"
      value={activeCompany ?? ''}
      onChange={(e) => change(e.target.value)}
      // Rótulo focado na cor do texto (o azul primário no fundo escuro não tem contraste AA).
      sx={{ minWidth: 168, '& .MuiInputLabel-root.Mui-focused': { color: 'text.primary' } }}
      slotProps={{ htmlInput: { 'data-testid': 'company-switcher', 'aria-label': 'Empresa' } }}
    >
      {companies.map((c) => (
        <MenuItem key={c.code} value={c.code}>
          {c.name}
        </MenuItem>
      ))}
      <MenuItem value={ALL_COMPANIES}>Todas</MenuItem>
    </TextField>
  );
};

/** Faixa discreta com a empresa ativa, para todos (no modo "Todas", avisa que é só consulta). */
export const CompanyBanner: React.FC = () => {
  const { companies, activeCompanyInfo, isAllCompanies } = useAuth();
  if (companies.length === 0) return null;
  return (
    <div
      role="note"
      data-testid="company-banner"
      className="flex items-center gap-2 border-b border-line bg-page px-4 py-1 text-xs text-muted sm:px-8"
    >
      <Building2 size={14} aria-hidden />
      {isAllCompanies ? (
        <span>
          Empresa: <strong className="text-ink">Todas</strong> — somente consulta. Para registrar, aprovar, importar ou administrar, escolha
          uma empresa no topo.
        </span>
      ) : (
        <span>
          Empresa: <strong className="text-ink">{activeCompanyInfo?.name}</strong>
        </span>
      )}
    </div>
  );
};
