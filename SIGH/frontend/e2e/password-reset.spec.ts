import { expect, test } from '@playwright/test';
import { ACCOUNT_CREATED_SUBJECT, apiToken, authed, loginUi, setupLinkFrom, waitForEmail } from './support/helpers';

// "Esqueci a senha" de ponta a ponta: o e-mail sai pela outbox, chega ao Mailpit e o link redefine a senha.
test('forgot password: the e-mail reaches Mailpit and its link resets the password', async ({ page, request }) => {
  const stamp = Date.now();
  const email = `e2e.reset.${stamp}@sigh.local`;
  const newPassword = `Nova#${stamp}Bb`;

  // Usuário próprio do teste (o admin dos outros specs não é afetado).
  const token = await apiToken(request);
  const cpf = String(stamp).slice(-11).padStart(11, '7');
  const createUser = await request.post('/api/v1/users', {
    headers: authed(token),
    // Sem campo de senha: um "initialPassword" de cliente antigo é ignorado.
    data: { fullName: 'Pessoa do E2E', email, cpf, initialPassword: `Ini#${stamp}Aa`, roleIds: [] },
  });
  expect(createUser.status(), await createUser.text()).toBe(201);

  // A conta criada recebe o e-mail de primeiro acesso, com o link de 48 h e sem senha.
  const welcome = await waitForEmail(request, email, ACCOUNT_CREATED_SUBJECT);
  expect(welcome.text).toContain('48 horas');
  expect(welcome.text).not.toContain(`Ini#${stamp}Aa`);
  const setupLink = setupLinkFrom(welcome.text);
  expect((await request.post('/api/v1/auth/login', { data: { email, password: `Ini#${stamp}Aa` } })).status(), 'ninguém conhece a senha').toBe(400);

  // Esqueci minha senha, pela tela.
  await page.goto('/login');
  await page.getByRole('link', { name: 'Esqueceu sua senha?' }).click();
  await expect(page.getByRole('heading', { name: 'Esqueci minha senha' })).toBeVisible();
  await page.getByLabel('E-mail').fill(email);
  await page.getByRole('button', { name: 'Enviar link' }).click();
  await expect(page.getByText(/se o e-mail informado estiver cadastrado/i)).toBeVisible();

  const reset = await waitForEmail(request, email, 'Redefinição de senha');
  const link = /(https?:\/\/\S+\/reset-password\?token=[^\s"<]+)/.exec(reset.text)?.[1];
  expect(link, reset.text).toBeTruthy();
  expect(reset.html).toContain('Redefinir senha');

  // O link (App:PublicBaseUrl) abre a página de redefinição.
  await page.goto(link!);
  await expect(page.getByRole('heading', { name: 'Redefinir senha' })).toBeVisible();
  await page.getByLabel('Nova senha', { exact: true }).fill(newPassword);
  await page.getByLabel('Confirmar nova senha').fill(newPassword);
  await page.getByRole('button', { name: 'Salvar senha' }).click();
  await expect(page.getByText(/senha redefinida com sucesso/i)).toBeVisible();

  // O link é de uso único.
  await page.goto(link!);
  await page.getByLabel('Nova senha', { exact: true }).fill(`Outra#${stamp}Cc`);
  await page.getByLabel('Confirmar nova senha').fill(`Outra#${stamp}Cc`);
  await page.getByRole('button', { name: 'Salvar senha' }).click();
  await expect(page.getByText(/inválido ou expirado/i)).toBeVisible();

  // O link de primeiro acesso (anterior) também deixou de valer; a nova senha entra (e não pede troca obrigatória).
  await page.goto(setupLink);
  await page.getByLabel('Nova senha', { exact: true }).fill(`Velho#${stamp}Dd`);
  await page.getByLabel('Confirmar nova senha').fill(`Velho#${stamp}Dd`);
  await page.getByRole('button', { name: 'Salvar senha' }).click();
  await expect(page.getByText(/inválido ou expirado/i)).toBeVisible();
  await loginUi(page, email, newPassword);
  await expect(page).not.toHaveURL(/change-password/);
});
