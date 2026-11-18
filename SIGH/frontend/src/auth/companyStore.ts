// Empresa ativa da sessão (código da empresa ou "TODAS"). Vai no cabeçalho X-SIGH-Company de toda requisição;
// o servidor SEMPRE valida (empresa sem perfil vigente → 404). A escolha fica lembrada no navegador, por usuário.

export const COMPANY_HEADER = 'X-SIGH-Company';
export const ALL_COMPANIES = 'TODAS';

const keyOf = (userId: string) => `sigh.company.${userId}`;

let current: string | null = null;

export const companyStore = {
  get: (): string | null => current,

  /** Define a empresa ativa (e lembra no navegador, se houver usuário). */
  set: (value: string | null, userId?: string) => {
    current = value;
    if (!userId) return;
    try {
      if (value) window.localStorage.setItem(keyOf(userId), value);
      else window.localStorage.removeItem(keyOf(userId));
    } catch {
      // sem armazenamento: vale só nesta aba
    }
  },

  /** Escolha lembrada deste usuário (pode ser de uma empresa que ele não tem mais: quem chama valida). */
  remembered: (userId: string): string | null => {
    try {
      return window.localStorage.getItem(keyOf(userId));
    } catch {
      return null;
    }
  },

  clear: () => {
    current = null;
  },
};
