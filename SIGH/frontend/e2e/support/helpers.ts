import { expect, type APIRequestContext, type Page } from '@playwright/test';

export const ADMIN_EMAIL = process.env.E2E_ADMIN_EMAIL ?? 'admin@sigh.test';
/**
 * Senha que o admin semeado define pelo link do e-mail (auth-flow.spec). Ninguém conhece a senha antes: o seed cria a
 * conta sem senha utilizável e manda o link de definição.
 */
export const adminPassword = () => {
  const value = process.env.E2E_NEW_PASSWORD;
  if (!value) throw new Error('E2E_NEW_PASSWORD não definida.');
  return value;
};

const MAILPIT = process.env.E2E_MAILPIT_URL ?? 'http://localhost:8025';

export const loginUi = async (page: Page, email = ADMIN_EMAIL, password = adminPassword()) => {
  await page.goto('/login');
  await page.getByLabel('E-mail').fill(email);
  await page.getByLabel('Senha', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Entrar' }).click();
  await expect(page.getByTestId('current-user-name')).toBeVisible();
};

/** Access token por API (sessão própria, separada do navegador). */
export const apiToken = async (request: APIRequestContext, email = ADMIN_EMAIL, password = adminPassword()) => {
  const response = await request.post('/api/v1/auth/login', { data: { email, password } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return (await response.json()).data.accessToken as string;
};

export const authed = (token: string) => ({ Authorization: `Bearer ${token}` });

export interface MailpitMessage {
  ID: string;
  Subject: string;
  To: { Address: string }[];
}

/** Espera chegar ao Mailpit um e-mail para o destinatário e devolve o texto. */
export const waitForEmail = async (request: APIRequestContext, to: string, subjectIncludes: string, timeoutMs = 30_000) => {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const search = await request.get(`${MAILPIT}/api/v1/search`, { params: { query: `to:"${to}"` } });
    if (search.ok()) {
      const messages: MailpitMessage[] = (await search.json()).messages ?? [];
      const hit = messages.find((m) => m.Subject.includes(subjectIncludes));
      if (hit) {
        const message = await (await request.get(`${MAILPIT}/api/v1/message/${hit.ID}`)).json();
        return { subject: hit.Subject, text: message.Text as string, html: message.HTML as string };
      }
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`Nenhum e-mail "${subjectIncludes}" para ${to} chegou ao Mailpit em ${timeoutMs} ms.`);
};

/** PDF pequeno, mas válido. */
export const pdfBytes = () =>
  Buffer.from(
    '%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n' +
      '3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n',
    'latin1',
  );

/** Cabeçalho de executável Windows (MZ...PE). */
export const exeBytes = () => Buffer.from([0x4d, 0x5a, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0xb8, 0x00, 0x50, 0x45]);

export interface E2EUser {
  email: string;
  password: string;
  token: string;
  id: string;
}

/** Ids dos perfis de sistema (por nome estável: Administrator, HrRegistrar, HrApprover, Legal, Manager). */
export const roleIds = async (request: APIRequestContext, adminToken: string, names: string[]) => {
  const roles: Array<{ id: string; name: string }> = (await (await request.get('/api/v1/roles', { headers: authed(adminToken) })).json()).data;
  return names.map((n) => {
    const role = roles.find((r) => r.name === n);
    if (!role) throw new Error(`Perfil ${n} não encontrado.`);
    return role.id;
  });
};

export const unitIds = async (request: APIRequestContext, adminToken: string, codes: string[]) => {
  const units: Array<{ id: string; code: string }> = (await (await request.get('/api/v1/management-units', { headers: authed(adminToken) })).json()).data;
  return codes.map((c) => units.find((u) => u.code === c)!.id);
};

/** Assunto do e-mail de conta criada (link de definição de senha, 48 h). */
export const ACCOUNT_CREATED_SUBJECT = 'Sua conta no SIGH foi criada';
/** Assunto do e-mail do primeiro administrador da empresa e da recuperação pelo TI. */
export const ADMIN_ACCESS_SUBJECT = 'Acesso de administrador no SIGH';

/** Link de definição/redefinição de senha do texto do e-mail. */
export const setupLinkFrom = (text: string) => {
  const link = /(https?:\/\/\S+\/reset-password\?token=[^\s"<]+)/.exec(text)?.[1];
  expect(link, text).toBeTruthy();
  return link!;
};

/** A própria pessoa define a senha pelo link do e-mail (POST /auth/reset-password com o token do link). */
export const definePasswordFromEmail = async (request: APIRequestContext, email: string, subject: string, password: string) => {
  const mail = await waitForEmail(request, email, subject);
  const token = decodeURIComponent(new URL(setupLinkFrom(mail.text)).searchParams.get('token')!);
  const reset = await request.post('/api/v1/auth/reset-password', { data: { token, newPassword: password } });
  expect(reset.ok(), await reset.text()).toBeTruthy();
};

/**
 * Cria um usuário pela API do admin (perfis e gerências reais; sem senha: o administrador nunca a conhece) e faz o
 * que a pessoa faria: define a senha pelo link do e-mail. Devolve a senha e um access token.
 */
export const createUserApi = async (
  request: APIRequestContext,
  adminToken: string,
  { fullName, email, cpf, roles, units = [] }: { fullName: string; email: string; cpf: string; roles: string[]; units?: string[] },
): Promise<E2EUser> => {
  const created = await request.post('/api/v1/users', {
    headers: authed(adminToken),
    data: { fullName, email, cpf, roleIds: await roleIds(request, adminToken, roles), managementUnitIds: await unitIds(request, adminToken, units) },
  });
  expect(created.status(), await created.text()).toBe(201);
  const id = (await created.json()).data.id as string;

  const password = `Def#${Date.now()}Aa9z`;
  await definePasswordFromEmail(request, email, ACCOUNT_CREATED_SUBJECT, password);
  return { email, password, token: await apiToken(request, email, password), id };
};

/**
 * Jurídico da USGA criado pelo admin: consulta funcionários, medidas e a trilha completa de auditoria, que o
 * Administrador não vê mais (ele só gerencia acessos).
 */
export const createReader = (request: APIRequestContext, adminToken: string, tag: string, cpf: string) =>
  createUserApi(request, adminToken, { fullName: `JURIDICO ${tag.toUpperCase()} E2E`, email: `juridico.${tag}.${Date.now()}@sigh.local`, cpf, roles: ['Legal'] });

/** Data local "yyyy-MM-dd", com deslocamento em dias. */
export const isoDay = (offsetDays = 0) => {
  const d = new Date(Date.now() + offsetDays * 86_400_000);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
};

/** Sai do navegador (limpa o cookie de refresh) para entrar com outro usuário. */
export const logoutUi = async (page: Page) => {
  await page.getByRole('button', { name: 'Sair' }).click();
  await expect(page).toHaveURL(/\/login$/);
};
