import { expect, test, type Page } from '@playwright/test';
import { ADMIN_ACCESS_SUBJECT, apiToken, authed, setupLinkFrom, waitForEmail } from './support/helpers';

// Fluxo completo contra API + SQL Server reais. O seed cria o primeiro administrador SEM senha conhecida e manda o
// link de definição por e-mail (Mailpit). Veja e2e/README.md.
const required = (name: string): string => {
  const value = process.env[name];
  if (!value) throw new Error(`Defina a variável de ambiente ${name} (veja e2e/README.md).`);
  return value;
};

const ADMIN_EMAIL = process.env.E2E_ADMIN_EMAIL ?? 'admin@sigh.test';
// A senha que o admin define pelo link; os demais specs (projeto "app") entram com ela.
const NEW_PASSWORD = required('E2E_NEW_PASSWORD');

const storageSnapshot = (page: Page) =>
  page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));

test('admin: first access through the e-mailed link, users list, no data screens, reload keeps the session, logout', async ({ page, context, request }) => {
  // Nenhuma resposta da API pode trazer o refresh token no corpo.
  const authBodies: string[] = [];
  page.on('response', async (response) => {
    if (response.url().includes('/api/v1/auth/')) {
      authBodies.push(await response.text().catch(() => ''));
    }
  });

  // 1. Ninguém conhece a senha do admin semeado: ele define a dele pelo link do e-mail (uso único, 48 h).
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  const mail = await waitForEmail(request, ADMIN_EMAIL, ADMIN_ACCESS_SUBJECT);
  expect(mail.text).toContain('48 horas');
  expect(mail.text).not.toMatch(/senha( inicial)?:\s*\S/i);
  await page.goto(setupLinkFrom(mail.text));
  await page.getByLabel('Nova senha', { exact: true }).fill(NEW_PASSWORD);
  await page.getByLabel('Confirmar nova senha').fill(NEW_PASSWORD);
  await page.getByRole('button', { name: 'Salvar senha' }).click();
  await expect(page.getByText(/senha (definida|redefinida) com sucesso/i)).toBeVisible();

  // 2. Login com a senha definida: sem troca obrigatória.
  await page.goto('/login');
  await page.getByLabel('E-mail').fill(ADMIN_EMAIL);
  await page.getByLabel('Senha', { exact: true }).fill(NEW_PASSWORD);
  await page.getByRole('button', { name: 'Entrar' }).click();
  await expect(page.getByTestId('current-user-name')).toBeVisible();
  await expect(page).not.toHaveURL(/\/change-password$/);

  // 3. Lista de usuários (a chamada à API tem que funcionar com o token novo).
  const listResponse = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/v1/users');
  await page.getByRole('navigation', { name: 'Menu principal' }).getByRole('link', { name: 'Usuários', exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/usuarios$/);
  expect((await listResponse).status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Usuários' })).toBeVisible();

  // 4. O Administrador só gerencia acessos: nada de medidas, funcionários, painel nem relatórios.
  const menu = page.getByRole('navigation', { name: 'Menu principal' });
  for (const hidden of ['Painel', 'Medidas', 'Funcionários', 'Relatórios']) await expect(menu.getByRole('link', { name: hidden, exact: true })).toHaveCount(0);
  await expect(menu.getByRole('link', { name: 'Auditoria', exact: true })).toBeVisible();
  const adminToken = await apiToken(request, ADMIN_EMAIL, NEW_PASSWORD);
  for (const url of ['/api/v1/employees', '/api/v1/disciplinary-records', '/api/v1/disciplinary-records/overview', '/api/v1/audit-logs']) {
    expect((await request.get(url, { headers: authed(adminToken) })).status(), url).toBe(403);
  }
  expect((await request.get('/api/v1/audit-logs/access', { headers: authed(adminToken) })).status()).toBe(200);
  await page.goto('/medidas');
  await expect(page.getByRole('heading', { name: /403/ })).toBeVisible();
  await page.goto('/admin/usuarios');
  await expect(page.getByRole('heading', { name: 'Usuários' })).toBeVisible();

  // O cookie do refresh token é HttpOnly, Secure, SameSite=Strict e restrito a /api/v1/auth.
  const rt = (await context.cookies()).find((c) => c.name === 'sigh_rt');
  expect(rt).toMatchObject({ httpOnly: true, secure: true, sameSite: 'Strict', path: '/api/v1/auth' });

  // Nada de token no armazenamento do navegador nem no corpo das respostas.
  const storage = await storageSnapshot(page);
  expect(storage).not.toContain(rt!.value);
  expect(storage).not.toMatch(/eyJ[\w-]+\.[\w-]+\./); // nenhum JWT
  for (const body of authBodies) {
    expect(body).not.toContain(rt!.value);
    expect(body.toLowerCase()).not.toContain('refreshtoken');
  }

  // 4. Recarregar mantém a sessão (refresh via cookie + /auth/me) sem passar pelo login
  const visitedLogin: string[] = [];
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame() && frame.url().includes('/login')) visitedLogin.push(frame.url());
  });
  const reloadListResponse = page.waitForResponse((r) => new URL(r.url()).pathname === '/api/v1/users');
  await page.reload();
  await expect(page).toHaveURL(/\/admin\/usuarios$/);
  expect((await reloadListResponse).status()).toBe(200);
  await expect(page.getByTestId('current-user-name')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Usuários' })).toBeVisible();
  expect(visitedLogin).toEqual([]);

  // 5. Logout: volta para o login, o cookie some e recarregar não restaura a sessão
  await page.getByRole('button', { name: 'Sair' }).click();
  await expect(page).toHaveURL(/\/login$/);
  expect((await context.cookies()).find((c) => c.name === 'sigh_rt')).toBeUndefined();
  await page.goto('/admin/usuarios');
  await expect(page).toHaveURL(/\/login$/);
});
