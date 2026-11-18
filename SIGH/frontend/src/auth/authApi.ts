import { api } from '../services/api';

interface ApiResult<T> {
  success: boolean;
  data: T;
  message?: string;
}

export interface CurrentUser {
  id: string;
  fullName: string;
  email: string;
  status: string;
  mustChangePassword: boolean;
  /** União dos perfis de todas as empresas (só exibição). */
  roles: string[];
  /** União das permissões de todas as empresas (só para clientes antigos; a tela usa as da empresa ativa). */
  permissions: string[];
  /** Empresas em que o usuário tem perfil vigente, com os perfis e as permissões de cada uma. */
  companies?: UserCompany[];
}

export interface UserCompany {
  id: string;
  /** Código curto (USGA, TRAP): é o valor enviado no cabeçalho X-SIGH-Company. */
  code: string;
  name: string;
  managementUnitPrefix: string;
  roles: string[];
  permissions: string[];
  managementUnitIds: string[];
}

export interface LoginResponse {
  accessToken: string;
  expiresInMinutes: number;
  mustChangePassword: boolean;
}

export interface ChangePasswordResponse {
  success: boolean;
  message: string;
  accessToken: string;
  expiresInMinutes: number;
}

// withCredentials só nas rotas que gravam, leem ou apagam o cookie sigh_rt (Path=/api/v1/auth).
// Em mesma origem o navegador já envia o cookie; a flag importa se o front for servido em outra origem.
export const authApi = {
  /** rememberMe = false: o cookie do refresh token é de sessão (some ao fechar o navegador). */
  login: async (email: string, password: string, rememberMe = true): Promise<LoginResponse> => {
    const response = await api.post<ApiResult<LoginResponse>>(
      '/v1/auth/login',
      { email, password, rememberMe },
      { withCredentials: true, skipAuthRefresh: true },
    );
    return response.data.data;
  },

  me: async (): Promise<CurrentUser> => {
    const response = await api.get<ApiResult<CurrentUser>>('/v1/auth/me');
    return response.data.data;
  },

  changePassword: async (currentPassword: string, newPassword: string): Promise<ChangePasswordResponse> => {
    const response = await api.post<ApiResult<ChangePasswordResponse>>(
      '/v1/auth/change-password',
      { currentPassword, newPassword },
      { withCredentials: true },
    );
    return response.data.data;
  },

  /** Sempre devolve a mesma mensagem genérica, exista ou não a conta. */
  forgotPassword: async (email: string): Promise<string> => {
    const response = await api.post<ApiResult<{ success: boolean; message: string }>>(
      '/v1/auth/forgot-password',
      { email },
      { skipAuthRefresh: true },
    );
    return response.data.data?.message ?? response.data.message ?? '';
  },

  /** Redefine (ou define, no primeiro acesso) a senha com o token do link do e-mail. */
  resetPassword: async (token: string, newPassword: string): Promise<void> => {
    await api.post('/v1/auth/reset-password', { token, newPassword }, { skipAuthRefresh: true });
  },

  logout: async (): Promise<void> => {
    await api.post('/v1/auth/logout', null, { withCredentials: true, skipAuthRefresh: true });
  },
};
