import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { authApi, type CurrentUser, type UserCompany } from '../auth/authApi';
import { ALL_COMPANIES, companyStore } from '../auth/companyStore';
import { WRITE_PERMISSIONS } from '../auth/permissions';
import { tokenStore } from '../auth/tokenStore';
import { crossTab } from '../auth/crossTab';
import { refreshAccessToken, setAuthEventHandlers } from '../services/api';

export type AuthStatus = 'loading' | 'authenticated' | 'unauthenticated';

interface AuthContextType {
  status: AuthStatus;
  user: CurrentUser | null;
  isAuthenticated: boolean;
  /** Permissões que valem AGORA: as da empresa ativa (no modo "Todas", as de leitura de todas as empresas). */
  permissions: string[];
  hasPermission: (permission: string) => boolean;
  /** Empresas em que o usuário tem perfil vigente. */
  companies: UserCompany[];
  /** Código da empresa ativa, ou "TODAS" (só leitura). */
  activeCompany: string | null;
  /** A empresa ativa (nulo no modo "Todas"). */
  activeCompanyInfo: UserCompany | null;
  /** Modo "Todas": listas e relatórios das empresas do usuário, sem nenhuma escrita. */
  isAllCompanies: boolean;
  /** Mais de uma empresa: aparece o seletor no topo. */
  canSwitchCompany: boolean;
  setActiveCompany: (code: string) => void;
  hasAnyPermission: (permissions: string[]) => boolean;
  /** Autentica e carrega o usuário de GET /auth/me. */
  login: (email: string, password: string, rememberMe?: boolean) => Promise<CurrentUser>;
  /** Troca a senha, passa a usar o novo access token e recarrega o usuário. */
  changePassword: (currentPassword: string, newPassword: string) => Promise<CurrentUser>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<AuthStatus>('loading');
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [activeCompany, setActiveCompanyState] = useState<string | null>(null);

  const endSession = useCallback(() => {
    tokenStore.clear();
    companyStore.clear();
    queryClient.clear();
    setUser(null);
    setActiveCompanyState(null);
    setStatus('unauthenticated');
  }, [queryClient]);

  /**
   * Empresa ativa depois de carregar o usuário: a lembrada no navegador, se ainda valer (perfil vigente nela;
   * "Todas" só com duas ou mais empresas); senão, a do primeiro perfil vigente. Vale antes de qualquer outra requisição.
   */
  const applyCompany = useCallback((me: CurrentUser) => {
    const companies = me.companies ?? [];
    const remembered = companyStore.remembered(me.id);
    const valid =
      (remembered === ALL_COMPANIES && companies.length > 1) || companies.some((c) => c.code === remembered);
    const selected = companies.length === 0 ? null : valid ? remembered : companies[0].code;
    companyStore.set(selected, me.id);
    setActiveCompanyState(selected);
  }, []);

  const loadUser = useCallback(async () => {
    const me = await authApi.me();
    applyCompany(me);
    setUser(me);
    setStatus('authenticated');
    return me;
  }, [applyCompany]);

  // Restauração de sessão: o access token não sobrevive ao reload; o cookie HttpOnly sim.
  useEffect(() => {
    let cancelled = false;

    refreshAccessToken()
      .then(() => (cancelled ? null : authApi.me()))
      .then((me) => {
        if (cancelled || !me) return;
        applyCompany(me);
        setUser(me);
        setStatus('authenticated');
      })
      .catch(() => {
        if (cancelled) return;
        tokenStore.clear();
        setUser(null);
        setStatus('unauthenticated');
      });

    return () => {
      cancelled = true;
    };
  }, [applyCompany]);

  // O interceptor do axios avisa quando o refresh falha ou a API exige troca de senha.
  // As rotas reagem ao estado: ProtectedRoute leva para /login ou /change-password.
  useEffect(
    () =>
      setAuthEventHandlers({
        onSessionExpired: endSession,
        onPasswordChangeRequired: () =>
          setUser((current) => (current ? { ...current, mustChangePassword: true } : current)),
        onAccessChanged: () => {
          // Perfis ou gerências mudaram: recarrega o usuário (menu e botões) e os dados em cache.
          void loadUser()
            .then(() => queryClient.invalidateQueries())
            .catch(() => undefined);
        },
      }),
    [endSession, loadUser, queryClient],
  );

  const login = useCallback(
    async (email: string, password: string, rememberMe = true) => {
      const result = await authApi.login(email, password, rememberMe);
      tokenStore.set(result.accessToken);
      try {
        return await loadUser();
      } catch (error) {
        endSession();
        throw error;
      }
    },
    [endSession, loadUser],
  );

  const changePassword = useCallback(
    async (currentPassword: string, newPassword: string) => {
      const result = await authApi.changePassword(currentPassword, newPassword);
      tokenStore.set(result.accessToken);
      return loadUser();
    },
    [loadUser],
  );

  const logout = useCallback(async () => {
    try {
      await authApi.logout();
    } catch {
      // Mesmo que a API falhe (token expirado, rede), a sessão local termina.
    } finally {
      endSession();
      // As outras abas usam o mesmo cookie, que acabou de ser revogado: elas também saem.
      crossTab.publish({ type: 'logout' });
    }
  }, [endSession]);

  const setActiveCompany = useCallback(
    (code: string) => {
      if (!user) return;
      companyStore.set(code, user.id);
      setActiveCompanyState(code);
      // Outra empresa: nada do que estava em cache vale (listas, contadores, detalhes).
      void queryClient.resetQueries();
    },
    [queryClient, user],
  );

  const value = useMemo<AuthContextType>(() => {
    const companies = user?.companies ?? [];
    const isAllCompanies = activeCompany === ALL_COMPANIES;
    const activeCompanyInfo = companies.find((c) => c.code === activeCompany) ?? null;
    // Sem a lista de empresas (cliente antigo da API), vale a lista única de permissões.
    const permissions = !user?.companies
      ? (user?.permissions ?? [])
      : isAllCompanies
        ? [...new Set(companies.flatMap((c) => c.permissions))].filter((p) => !WRITE_PERMISSIONS.has(p))
        : (activeCompanyInfo?.permissions ?? []);
    const hasPermission = (permission: string) => permissions.includes(permission);
    return {
      status,
      user,
      isAuthenticated: status === 'authenticated' && user !== null,
      permissions,
      hasPermission,
      hasAnyPermission: (perms: string[]) => perms.some(hasPermission),
      companies,
      activeCompany,
      activeCompanyInfo,
      isAllCompanies,
      canSwitchCompany: companies.length > 1,
      setActiveCompany,
      login,
      changePassword,
      logout,
    };
  }, [status, user, activeCompany, setActiveCompany, login, changePassword, logout]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};

/** Modo "Todas" (coluna Empresa) para componentes que também aparecem fora do AuthProvider (testes isolados). */
export const useIsAllCompanies = (): boolean => useContext(AuthContext)?.isAllCompanies ?? false;

export const useAuthContext = (): AuthContextType => {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuthContext deve ser usado dentro de um AuthProvider');
  }
  return context;
};
