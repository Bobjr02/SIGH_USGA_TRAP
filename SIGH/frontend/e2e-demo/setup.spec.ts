import { expect, test } from '@playwright/test';
import { ADMIN_ACCESS_SUBJECT, setupLinkFrom, waitForEmail } from '../e2e/support/helpers';
import { ADMIN_EMAIL } from './support/demo';

// Primeiro acesso do administrador do seed no banco descartável: ninguém conhece a senha; ele a define pelo link do
// e-mail (Mailpit) e entra com ela.
test('admin: first access through the e-mailed setup link', async ({ page, request }) => {
  const mail = await waitForEmail(request, ADMIN_EMAIL, ADMIN_ACCESS_SUBJECT);
  await page.goto(setupLinkFrom(mail.text));
  await page.getByLabel('Nova senha', { exact: true }).fill(process.env.E2E_NEW_PASSWORD!);
  await page.getByLabel('Confirmar nova senha').fill(process.env.E2E_NEW_PASSWORD!);
  await page.getByRole('button', { name: 'Salvar senha' }).click();
  await expect(page.getByText(/senha definida com sucesso/i)).toBeVisible();

  await page.goto('/login');
  await page.getByLabel('E-mail').fill(ADMIN_EMAIL);
  await page.getByLabel('Senha', { exact: true }).fill(process.env.E2E_NEW_PASSWORD!);
  await page.getByRole('button', { name: 'Entrar' }).click();
  await expect(page.getByTestId('current-user-name')).toBeVisible();
});
