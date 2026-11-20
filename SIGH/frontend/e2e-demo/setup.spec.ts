import { expect, test } from '@playwright/test';
import { ADMIN_ACCESS_SUBJECT, waitForEmail } from '../e2e/support/helpers';
import { ADMIN_EMAIL, demoPassword } from './support/demo';

// Mesmo caminho do scripts/local-up: o seed cria o primeiro administrador sem senha conhecida (o e-mail com o link
// sai, sem senha) e a carga de demonstração lhe dá a senha de demonstração pelo link, internamente.
test('admin: the seed administrator gets the demo password and signs in (same path as local-up)', async ({ page, request }) => {
  const mail = await waitForEmail(request, ADMIN_EMAIL, ADMIN_ACCESS_SUBJECT);
  expect(mail.text).not.toContain(demoPassword());

  await page.goto('/login');
  await page.getByLabel('E-mail').fill(ADMIN_EMAIL);
  await page.getByLabel('Senha', { exact: true }).fill(demoPassword());
  await page.getByRole('button', { name: 'Entrar' }).click();
  await expect(page.getByTestId('current-user-name')).toBeVisible();
  await expect(page).not.toHaveURL(/change-password/);
});
