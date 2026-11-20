import { api } from '../../../services/api';

export interface AuditLogDto {
  id: string;
  createdAt: string;
  action: string;
  entity: string;
  entityId?: string | null;
  userId?: string | null;
  userName?: string | null;
  ipAddress?: string | null;
  correlationId?: string | null;
  /** JSON com os valores anteriores dos campos alterados. */
  oldValues?: string | null;
  /** JSON com os valores novos (ou os detalhes do evento de segurança). */
  newValues?: string | null;
  /** Empresa do registro (mostrada no modo "Todas"). */
  companyName?: string | null;
}

export interface AuditPage {
  items: AuditLogDto[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface AuditLogFilters {
  entityName?: string;
  entityId?: string;
  userId?: string;
  action?: string;
  /** Número da medida (MED-…) ou matrícula. */
  reference?: string;
  from?: string;
  to?: string;
  pageNumber?: number;
  pageSize?: number;
}

interface Result<T> {
  success: boolean;
  data?: T;
  message?: string;
}

const clean = (filters: AuditLogFilters) =>
  Object.fromEntries(Object.entries(filters).filter(([, v]) => v !== undefined && v !== null && v !== ''));

export interface AuditUserOption {
  id: string;
  fullName: string;
  email: string;
}

/**
 * "full": trilha completa (Audit.View, Jurídico). "access": só eventos de acesso e segurança (Audit.ViewAccess,
 * Administrador), sem dados de funcionário nem de medida.
 */
export type AuditMode = 'full' | 'access';

export const auditKeys = {
  users: (search: string, mode: AuditMode = 'full') => ['audit-logs', 'users', mode, search] as const,
  all: ['audit-logs'] as const,
  search: (filters: AuditLogFilters, mode: AuditMode = 'full') => ['audit-logs', 'search', mode, clean(filters)] as const,
  caseTrail: (caseId: string, page: number) => ['audit-logs', 'case', caseId, page] as const,
};

export const auditApi = {
  async search(filters: AuditLogFilters, mode: AuditMode = 'full'): Promise<AuditPage> {
    const { reference, ...rest } = filters;
    const response = await api.get<Result<AuditPage>>(mode === 'access' ? '/v1/audit-logs/access' : '/v1/audit-logs', {
      params: clean(mode === 'access' ? rest : { ...rest, reference }),
    });
    if (!response.data.success || !response.data.data) throw new Error(response.data.message || 'Erro ao consultar a auditoria.');
    return response.data.data;
  },

  async users(search: string, mode: AuditMode = 'full'): Promise<AuditUserOption[]> {
    const response = await api.get<Result<AuditUserOption[]>>(mode === 'access' ? '/v1/audit-logs/access/users' : '/v1/audit-logs/users', {
      params: { search: search || undefined },
    });
    if (!response.data.success || !response.data.data) throw new Error(response.data.message || 'Erro ao buscar usuários.');
    return response.data.data;
  },

  async caseTrail(caseId: string, pageNumber = 1, pageSize = 100): Promise<AuditPage> {
    const response = await api.get<Result<AuditPage>>(`/v1/disciplinary-cases/${caseId}/audit-trail`, { params: { pageNumber, pageSize } });
    if (!response.data.success || !response.data.data) throw new Error(response.data.message || 'Erro ao carregar o histórico.');
    return response.data.data;
  },
};
