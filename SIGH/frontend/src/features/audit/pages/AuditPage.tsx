import React, { useEffect, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Autocomplete, Button, MenuItem, TextField } from '@mui/material';
import { FilterX, ScrollText, Search } from 'lucide-react';
import { PageHeader } from '../../../components/ui/PageHeader';
import { PaginationBar } from '../../../components/ui/DataTable';
import { EmptyState, ErrorState } from '../../../components/ui/States';
import { formatNumber } from '../../../utils/formatters';
import { useAuth } from '../../../hooks/useAuth';
import { PERMISSIONS } from '../../../auth/permissions';
import { auditApi, auditKeys, type AuditLogFilters, type AuditMode, type AuditUserOption } from '../api/auditApi';
import { AuditEntryCard } from '../components/AuditEntryCard';
import { ACTION_LABELS, ENTITY_LABELS } from '../components/auditFormat';

/** Registros da auditoria de acesso (espelho de AccessAuditPolicy no backend). */
export const ACCESS_AUDIT_ENTITIES = ['User', 'UserRole', 'UserManagementUnit', 'ManagementUnit', 'Role', 'Permission', 'RolePermission', 'InfractionType', 'AccessControl'];
const isAccessAction = (action: string) =>
  ['Created', 'Updated', 'Deleted', 'SoftDeleted'].includes(action) || (action.startsWith('Security.') && action !== 'Security.EvidenceDownloaded');

/** Trilha completa (Audit.View) ou só a de acesso (Audit.ViewAccess, do Administrador). */
export const useAuditMode = (): AuditMode | null => {
  const { hasPermission } = useAuth();
  if (hasPermission(PERMISSIONS.AUDIT_VIEW)) return 'full';
  if (hasPermission(PERMISSIONS.AUDIT_VIEW_ACCESS)) return 'access';
  return null;
};

const PAGE_SIZE = 25;

const toIso = (local: string, endOfMinute = false) => (local ? new Date(local + (endOfMinute ? ':59' : '')).toISOString() : undefined);

/** Seletor de usuário com busca por nome (no lugar do "Id do usuário"). */
const UserPicker: React.FC<{ value: AuditUserOption | null; onChange: (u: AuditUserOption | null) => void; mode: AuditMode }> = ({ value, onChange, mode }) => {
  const [input, setInput] = useState('');
  const [debounced, setDebounced] = useState('');
  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(input.trim()), 250);
    return () => window.clearTimeout(t);
  }, [input]);
  const users = useQuery({ queryKey: auditKeys.users(debounced, mode), queryFn: () => auditApi.users(debounced, mode), staleTime: 60_000 });

  return (
    <Autocomplete
      size="small"
      options={users.data ?? []}
      value={value}
      onChange={(_, v) => onChange(v)}
      inputValue={input}
      onInputChange={(_, v) => setInput(v)}
      filterOptions={(x) => x}
      getOptionLabel={(o) => o.fullName}
      isOptionEqualToValue={(a, b) => a.id === b.id}
      loading={users.isFetching}
      loadingText="Buscando…"
      noOptionsText="Nenhum usuário encontrado"
      clearText="Limpar"
      openText="Abrir"
      closeText="Fechar"
      renderOption={(props, o) => {
        const { key, ...rest } = props as React.HTMLAttributes<HTMLLIElement> & { key: string };
        return (
          <li key={key} {...rest}>
            <span>
              <span className="block text-sm">{o.fullName}</span>
              <span className="block text-xs text-muted">{o.email}</span>
            </span>
          </li>
        );
      }}
      renderInput={(params) => <TextField {...params} label="Usuário" placeholder="Buscar por nome" />}
    />
  );
};

/**
 * Consulta da trilha de auditoria, com filtros legíveis. Com Audit.View (Jurídico), a trilha completa; com só
 * Audit.ViewAccess (Administrador), a auditoria de ACESSO: contas, perfis, gerências, motivos e eventos de segurança,
 * sem busca por medida ou matrícula e sem nenhum dado de funcionário ou de medida.
 */
export const AuditPage: React.FC = () => {
  const mode = useAuditMode() ?? 'access';
  const accessOnly = mode === 'access';
  const entities = Object.entries(ENTITY_LABELS).filter(([value]) => !accessOnly || ACCESS_AUDIT_ENTITIES.includes(value));
  const actions = Object.entries(ACTION_LABELS).filter(([value]) => !accessOnly || isAccessAction(value));
  const empty = { entityName: '', reference: '', user: null as AuditUserOption | null, action: '', from: '', to: '' };
  const [draft, setDraft] = useState(empty);
  const [filters, setFilters] = useState<AuditLogFilters>({ pageNumber: 1, pageSize: PAGE_SIZE });

  const { data, isLoading, isError, error, isFetching, refetch } = useQuery({
    queryKey: auditKeys.search(filters, mode),
    queryFn: () => auditApi.search(filters, mode),
    placeholderData: keepPreviousData,
  });

  const apply = (event: React.FormEvent) => {
    event.preventDefault();
    setFilters({
      entityName: draft.entityName || undefined,
      reference: draft.reference.trim() || undefined,
      userId: draft.user?.id,
      action: draft.action || undefined,
      from: toIso(draft.from),
      to: toIso(draft.to, true),
      pageNumber: 1,
      pageSize: PAGE_SIZE,
    });
  };

  const set = (field: 'entityName' | 'reference' | 'action' | 'from' | 'to') => (e: React.ChangeEvent<HTMLInputElement>) =>
    setDraft((d) => ({ ...d, [field]: e.target.value }));

  return (
    <div>
      <PageHeader
        icon={<ScrollText size={22} />}
        title={accessOnly ? 'Auditoria de acesso' : 'Auditoria'}
        subtitle={
          accessOnly
            ? 'Contas, perfis, gerências, motivos e eventos de segurança (sem dados de funcionários nem de medidas)'
            : 'Trilha de alterações e eventos de segurança do sistema'
        }
      />

      <form
        onSubmit={apply}
        aria-label="Filtros da auditoria"
        className="mb-4 grid grid-cols-1 gap-3 rounded-lg border border-line bg-surface p-4 shadow-card sm:grid-cols-2 lg:grid-cols-3"
      >
        {!accessOnly && (
          <TextField
            size="small"
            label="Medida ou matrícula"
            placeholder="MED-2026-… ou 50090006"
            value={draft.reference}
            onChange={set('reference')}
            helperText="Número da medida (MED-…) ou matrícula do funcionário"
          />
        )}
        <UserPicker mode={mode} value={draft.user} onChange={(user) => setDraft((d) => ({ ...d, user }))} />
        <TextField select size="small" label="Registro" value={draft.entityName} onChange={set('entityName')}>
          <MenuItem value="">Todos</MenuItem>
          {entities
            .sort((a, b) => a[1].localeCompare(b[1], 'pt-BR'))
            .map(([value, text]) => (
              <MenuItem key={value} value={value}>
                {text}
              </MenuItem>
            ))}
        </TextField>
        <TextField select size="small" label="Ação" value={draft.action} onChange={set('action')}>
          <MenuItem value="">Todas</MenuItem>
          {actions.map(([value, text]) => (
            <MenuItem key={value} value={value}>
              {text}
            </MenuItem>
          ))}
        </TextField>
        <TextField size="small" type="datetime-local" label="De" value={draft.from} onChange={set('from')} slotProps={{ inputLabel: { shrink: true } }} />
        <TextField size="small" type="datetime-local" label="Até" value={draft.to} onChange={set('to')} slotProps={{ inputLabel: { shrink: true } }} />
        <div className="flex flex-wrap justify-end gap-2 sm:col-span-2 lg:col-span-3">
          <Button
            variant="outlined"
            startIcon={<FilterX size={16} />}
            onClick={() => {
              setDraft(empty);
              setFilters({ pageNumber: 1, pageSize: PAGE_SIZE });
            }}
          >
            Limpar filtros
          </Button>
          <Button type="submit" variant="contained" startIcon={<Search size={16} />} disabled={isFetching}>
            Filtrar
          </Button>
        </div>
      </form>

      {isError && <ErrorState message={(error as Error).message} onRetry={() => refetch()} />}
      {isLoading && (
        <div className="flex flex-col gap-3" aria-busy="true" aria-label="Carregando auditoria">
          {[0, 1, 2].map((i) => (
            <div key={i} className="h-20 animate-pulse rounded-lg bg-surface-2" />
          ))}
        </div>
      )}

      {data && (
        <div className="flex flex-col gap-3">
          <p className="text-sm text-muted">{formatNumber(data.totalCount)} registro(s)</p>
          {data.items.length === 0 ? (
            <div className="rounded-lg border border-line bg-surface">
              <EmptyState message="Nenhum registro para os filtros escolhidos." icon={<ScrollText size={32} />} />
            </div>
          ) : (
            data.items.map((log) => <AuditEntryCard key={log.id} log={log} />)
          )}
          {data.totalCount > 0 && (
            <div className="overflow-hidden rounded-lg border border-line bg-surface">
              <PaginationBar
                pageNumber={data.pageNumber}
                pageSize={data.pageSize}
                totalCount={data.totalCount}
                onPageChange={(page) => setFilters((f) => ({ ...f, pageNumber: page }))}
                noun="registros"
              />
            </div>
          )}
        </div>
      )}
    </div>
  );
};

export default AuditPage;
