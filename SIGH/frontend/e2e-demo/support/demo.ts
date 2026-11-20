import { expect, type APIRequestContext, type Page } from '@playwright/test';
import { syntheticCpf } from '../../e2e/support/xlsx';
import enumValues from '../../src/i18n/enum-values.json' with { type: 'json' };

export const ADMIN_EMAIL = process.env.E2E_ADMIN_EMAIL ?? 'admin@demo.local';

export interface DemoProfile {
  login: string;
  email: string;
  password: () => string;
  label: string;
}

export const demoPassword = () => process.env.SIGH_DEMO_PASSWORD!;

/**
 * Perfis de demonstração (DemoDataSeeder) + o primeiro administrador do seed, que na demonstração também fica com a
 * senha de demonstração (como no scripts/local-up).
 */
export const PROFILES: DemoProfile[] = [
  { login: 'admin', email: ADMIN_EMAIL, password: demoPassword, label: 'Administrador' },
  ...[
    'auxiliar', 'encarregado', 'juridico', 'gestor.industrial', 'gestor.agricola', 'preposto',
    // Por empresa (multiempresa): USGA e Trapiche.
    'admin.usga', 'auxiliar.usga', 'encarregado.usga', 'gestor.usga131', 'admin.grupo',
    'admin.trap', 'auxiliar.trap', 'encarregado.trap', 'gestor.trap131',
  ].map((login) => ({
    login,
    email: `${login}@demo.local`,
    password: demoPassword,
    label: login,
  })),
];

export const profile = (login: string) => PROFILES.find((p) => p.login === login)!;

export type DemoTheme = 'dark' | 'light';

/** Temas das suítes: os dois (escuro e claro); E2E_THEME=dark ou E2E_THEME=light restringe a um só. */
export const THEMES: DemoTheme[] = process.env.E2E_THEME === 'dark' || process.env.E2E_THEME === 'light' ? [process.env.E2E_THEME] : ['dark', 'light'];

export const THEME_LABEL: Record<DemoTheme, string> = { dark: 'escuro', light: 'claro' };

export const login = async (page: Page, who: DemoProfile, theme: DemoTheme = 'dark') => {
  await page.addInitScript((t) => window.localStorage.setItem('sigh.theme', t), theme);
  await page.goto('/login');
  await page.getByLabel('E-mail').fill(who.email);
  await page.getByLabel('Senha', { exact: true }).fill(who.password());
  await page.getByRole('button', { name: 'Entrar' }).click();
  await expect(page.getByTestId('current-user-name').or(page.getByTestId('dashboard')).first()).toBeVisible({ timeout: 30_000 });
};

/** Rótulo do evento "perfis ou permissões alterados" na Auditoria (Security.RolesOrPermissionsChanged). */
export const ROLES_CHANGED_LABEL = 'Perfis ou permissões alterados';

/**
 * Garante, na USGA e na Trapiche, um evento "perfis ou permissões alterados" recente: o administrador de cada empresa
 * cria uma pessoa FICTÍCIA com um perfil (o perfil atribuído gera o evento). Assim a Auditoria sempre mostra os
 * detalhes desse evento (entidade e ação aninhadas), sem depender da ordem dos testes. Usa um contexto de API
 * próprio: não mexe na sessão do navegador.
 */
export const ensureRoleChangedEvent = async (api: APIRequestContext) => {
  for (const admin of ['admin.usga', 'admin.trap']) {
    const login = await api.post('/api/v1/auth/login', { data: { email: `${admin}@demo.local`, password: demoPassword() } });
    expect(login.ok(), await login.text()).toBeTruthy();
    const headers = { Authorization: `Bearer ${(await login.json()).data.accessToken as string}` };
    const roles: Array<{ id: string; name: string }> = (await (await api.get('/api/v1/roles', { headers })).json()).data;
    const stamp = `${Date.now()}`;
    const created = await api.post('/api/v1/users', {
      headers,
      data: {
        fullName: 'PERFIL ALTERADO TESTE',
        email: `perfil.alterado.${admin}.${stamp}@demo.local`,
        cpf: syntheticCpf(stamp.slice(-9)),
        roleIds: [roles.find((r) => r.name === 'HrRegistrar')!.id],
      },
    });
    expect(created.status(), await created.text()).toBe(201);
  }
};

/** Troca a empresa ativa pelo seletor do topo (só aparece para quem tem duas ou mais empresas). */
export const selectCompany = async (page: Page, name: string) => {
  await page.getByRole('combobox', { name: 'Empresa' }).click();
  await page.getByRole('option', { name, exact: true }).click();
  await expect(page.getByTestId('company-banner')).toContainText(name === 'Todas' ? 'somente consulta' : name);
};

/** Espera a página assentar (sem esqueletos de carregamento). */
export const settle = async (page: Page) => {
  await page.waitForLoadState('networkidle');
  await expect(page.locator('[aria-busy="true"]')).toHaveCount(0, { timeout: 20_000 });
  await page.waitForTimeout(300);
};

/**
 * Termos proibidos na tela: os NOMES de todos os valores de enum (enum-values.json, gerado do backend)
 * e palavras comuns de interface em inglês. Exceções: valores cujo nome também é texto válido em
 * português ou é a marca ("Sigh" = SIGH, "Normal", "Legal").
 */
const PORTUGUESE_TOO = new Set(['Sigh', 'Normal', 'Legal']);
export const FORBIDDEN_TERMS: string[] = Array.from(
  new Set([
    ...Object.values(enumValues).flatMap((group) => Object.keys(group)),
    'Pending',
    'Approved',
    'Rejected',
    'Warning',
    'Suspension',
    'Completed',
    'Cancelled',
    'Draft',
    'Loading',
    'Submit',
    'Cancel',
    'Save',
    'Delete',
    'Search',
    'Error',
    'Not found',
    'Unauthorized',
    'Forbidden',
    'Dashboard',
    // Textos padrão dos componentes do MUI sem a localização pt-BR (botões de fechar, Autocomplete, paginação).
    'Close',
    'Rows per page',
    'of',
    'Open',
    'Clear',
    'Previous',
    'Next',
    'Go to page',
  ]),
).filter((t) => !PORTUGUESE_TOO.has(t));

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Termos encontrados no texto (palavra inteira, sem diferenciar maiúsculas). */
export const forbiddenIn = (text: string): string[] =>
  FORBIDDEN_TERMS.filter((term) => new RegExp(`(?<![\\p{L}\\p{N}_])${escape(term)}(?![\\p{L}\\p{N}_])`, 'iu').test(text));
