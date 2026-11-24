import { randomUUID } from 'node:crypto';
import { expect, test, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import { ADMIN_ACCESS_SUBJECT, apiToken, authed, createUserApi, definePasswordFromEmail, isoDay, loginUi } from './support/helpers';
import { sapFile, syntheticCpf } from './support/xlsx';

// Multiempresa (USGA × Trapiche), de ponta a ponta:
// - o auxiliar.trap registra uma medida da Trapiche;
// - o encarregado.usga não a vê (lista sem ela; detalhe "não encontrada");
// - o encarregado.trap aprova;
// - o admin.trap vincula o jurídico (conta da USGA) à Trapiche sem criar outra conta;
// - o jurídico vê as duas empresas em "Todas" e não consegue escrever nesse modo;
// - o admin.trap não vê usuários da USGA nem dados de funcionários (de empresa nenhuma).
// O primeiro administrador da Trapiche vem do seed (Seed:CompanyAdmins, veja scripts/e2e-stack.sh): sem senha, com o
// link de definição por e-mail.
// Funcionários FICTÍCIOS gerados aqui (chaves TRAP220/TRAP230, nomes terminando em TESTE).

const TRAP_ADMIN_EMAIL = process.env.E2E_TRAP_ADMIN_EMAIL ?? 'admin.trap@e2e.local';

const stamp = Date.now();
const TRAP_EMPLOYEE = '71090001';
const newPage = async (browser: Browser) => (await browser.newContext()).newPage();
const company = (code: string) => ({ 'X-SIGH-Company': code });

const choose = async (page: Page, label: string | RegExp, option: string | RegExp) => {
  await page.getByRole('combobox', { name: label }).click();
  await page.getByRole('option', { name: option }).click();
};

/** Primeiro acesso do administrador semeado da Trapiche: define a senha pelo link do e-mail, uma vez por banco. */
const TRAP_ADMIN_PASSWORD = `Trap#${process.env.E2E_DB_NAME ?? 'e2e'}Aa1`;
const trapAdminSession = async (request: APIRequestContext) => {
  const login = await request.post('/api/v1/auth/login', { data: { email: TRAP_ADMIN_EMAIL, password: TRAP_ADMIN_PASSWORD } });
  if (!login.ok()) await definePasswordFromEmail(request, TRAP_ADMIN_EMAIL, `${ADMIN_ACCESS_SUBJECT} - Trapiche`, TRAP_ADMIN_PASSWORD);
  return { token: await apiToken(request, TRAP_ADMIN_EMAIL, TRAP_ADMIN_PASSWORD), password: TRAP_ADMIN_PASSWORD };
};

const importEmployees = async (request: APIRequestContext, token: string, file: Buffer) => {
  const multipart = { file: { name: 'funcionarios_trapiche_ficticio.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: file } };
  const preview = await request.post('/api/v1/employees/import/preview', { headers: authed(token), multipart });
  expect(preview.ok(), await preview.text()).toBeTruthy();
  const batch = (await preview.json()).data.batchId as string;
  const confirm = await request.post(`/api/v1/employees/import/${batch}/confirm`, {
    headers: authed(token),
    multipart: { ...multipart, confirmMassTermination: 'false' },
  });
  expect(confirm.ok(), await confirm.text()).toBeTruthy();
};

test('multiempresa: Trapiche isolada da USGA, aprovação por empresa, jurídico nas duas e vínculo de conta existente', async ({ browser, request }) => {
  test.setTimeout(300_000);
  const usgaAdmin = await apiToken(request);
  const trapAdmin = await trapAdminSession(request);

  // Dados FICTÍCIOS da Trapiche nas chaves oficiais TRAP220 e TRAP230 (criadas pelo seed, como as outras 18 da lista).
  await importEmployees(request, trapAdmin.token, sapFile([
    { number: TRAP_EMPLOYEE, name: 'JOAO DA TRAPICHE TESTE', cpf: syntheticCpf('710900001'), mu: 'TRAP220' },
    { number: '71090002', name: 'MARIA DA TRAPICHE TESTE', cpf: syntheticCpf('710900002'), mu: 'TRAP230', group: 'Campo' },
  ]));
  const infraction = await request.post('/api/v1/infraction-types', {
    headers: authed(trapAdmin.token),
    data: { code: `TRAP${stamp % 100000}`, name: 'Atraso na Trapiche (E2E)', defaultSeverity: 2 },
  });
  expect(infraction.ok(), await infraction.text()).toBeTruthy();

  // Usuários: cada administrador cria os da própria empresa.
  const auxiliarTrap = await createUserApi(request, trapAdmin.token, { fullName: 'AUXILIAR TRAPICHE TESTE', email: `auxiliar.trap.${stamp}@sigh.local`, cpf: syntheticCpf('711000001'), roles: ['HrRegistrar'] });
  const encarregadoTrap = await createUserApi(request, trapAdmin.token, { fullName: 'ENCARREGADO TRAPICHE TESTE', email: `encarregado.trap.${stamp}@sigh.local`, cpf: syntheticCpf('711000002'), roles: ['HrApprover'] });
  const encarregadoUsga = await createUserApi(request, usgaAdmin, { fullName: 'ENCARREGADO USGA TESTE', email: `encarregado.usga.${stamp}@sigh.local`, cpf: syntheticCpf('711000003'), roles: ['HrApprover'] });
  const juridico = await createUserApi(request, usgaAdmin, { fullName: 'JURIDICO DO GRUPO TESTE', email: `juridico.${stamp}@sigh.local`, cpf: syntheticCpf('711000004'), roles: ['Legal'] });

  // 1. O auxiliar da Trapiche registra uma medida da Trapiche (pela tela).
  const auxiliar = await newPage(browser);
  await loginUi(auxiliar, auxiliarTrap.email, auxiliarTrap.password);
  await expect(auxiliar.getByTestId('company-banner')).toContainText('Empresa: Trapiche');
  await expect(auxiliar.getByTestId('company-switcher')).toHaveCount(0);
  await auxiliar.getByRole('button', { name: 'Registrar medida' }).first().click();
  const form = auxiliar.getByRole('form', { name: 'Registrar medida' });
  await form.getByRole('combobox', { name: 'Funcionário', exact: true }).fill(TRAP_EMPLOYEE);
  await auxiliar.getByRole('option', { name: new RegExp(TRAP_EMPLOYEE) }).click();
  await choose(auxiliar, /motivo/i, 'Atraso na Trapiche (E2E)');
  await choose(auxiliar, /tipo de medida/i, 'Advertência escrita');
  await auxiliar.getByLabel('Ocorrência 1').fill(isoDay(-2));
  await auxiliar.getByLabel(/data da medida/i).fill(isoDay(0));
  await auxiliar.getByLabel(/descrição/i).fill('Chegou atrasado ao turno da moenda.');
  await form.getByRole('button', { name: 'Registrar medida' }).click();
  await expect(auxiliar.getByTestId('record-status')).toHaveText('Aguardando aprovação');
  const recordId = auxiliar.url().split('/').pop()!;

  // 2. O encarregado da USGA não a vê: nem na lista, nem pelo endereço.
  const usgaApprover = await newPage(browser);
  await loginUi(usgaApprover, encarregadoUsga.email, encarregadoUsga.password);
  await expect(usgaApprover.getByTestId('company-banner')).toContainText('Empresa:');
  await usgaApprover.goto('/medidas');
  await expect(usgaApprover.getByTestId('record-row').filter({ hasText: TRAP_EMPLOYEE })).toHaveCount(0);
  await usgaApprover.goto(`/medidas/${recordId}`);
  await expect(usgaApprover.getByTestId('record-not-found').getByText('Medida não encontrada.', { exact: true })).toBeVisible();
  expect((await request.get(`/api/v1/disciplinary-records/${recordId}`, { headers: authed(encarregadoUsga.token) })).status()).toBe(404);
  expect((await request.post(`/api/v1/disciplinary-records/${recordId}/approve`, { headers: authed(encarregadoUsga.token) })).status()).toBe(404);

  // 3. O encarregado da Trapiche aprova.
  const trapApprover = await newPage(browser);
  await loginUi(trapApprover, encarregadoTrap.email, encarregadoTrap.password);
  await trapApprover.goto(`/medidas/${recordId}`);
  await trapApprover.getByRole('button', { name: 'Aprovar' }).click();
  await expect(trapApprover.getByText('Medida aprovada.')).toBeVisible();
  await expect(trapApprover.getByTestId('record-status')).toHaveText('Aprovada');

  // 4. O admin da Trapiche vincula o jurídico (conta da USGA) à Trapiche, sem criar outra conta.
  const usersBefore = (await (await request.get('/api/v1/users?pageSize=1', { headers: authed(usgaAdmin) })).json()).data.totalCount as number;
  const admin = await newPage(browser);
  await loginUi(admin, TRAP_ADMIN_EMAIL, trapAdmin.password);
  await admin.goto('/admin/usuarios');
  await expect(admin.getByText(juridico.email)).toHaveCount(0);
  await admin.getByRole('button', { name: 'Novo usuário' }).click();
  const dialog = admin.getByRole('dialog', { name: 'Novo usuário' });
  await expect(dialog.getByTestId('create-user-company')).toContainText('Trapiche');
  await dialog.getByLabel('Nome completo').fill('JURIDICO DO GRUPO TESTE');
  await dialog.getByLabel('E-mail').fill(juridico.email);
  await dialog.getByLabel('CPF').fill(syntheticCpf('711000004'));
  await dialog.getByRole('checkbox', { name: 'Jurídico' }).check();
  await dialog.getByRole('button', { name: 'Criar usuário' }).click();
  await expect(dialog.getByTestId('link-offer')).toBeVisible();
  await dialog.getByRole('button', { name: 'Vincular este usuário à Trapiche' }).click();
  await expect(admin).toHaveURL(new RegExp(`/admin/usuarios/${juridico.id}$`));
  await admin.getByRole('tab', { name: 'Perfis e gerências' }).click();
  await expect(admin.getByTestId('roles-company')).toContainText('Perfis na empresa Trapiche');
  await expect(admin.getByTestId('role-row')).toHaveCount(1);
  await expect(admin.getByTestId('other-company-access')).toBeVisible();
  const usersAfter = (await (await request.get('/api/v1/users?pageSize=1', { headers: authed(usgaAdmin) })).json()).data.totalCount as number;
  expect(usersAfter, 'nenhuma conta nova foi criada').toBe(usersBefore);

  // 5. O admin da Trapiche não vê usuários da USGA e não consulta funcionários (só gerencia acessos); o auxiliar da
  // Trapiche vê só os funcionários dela.
  await admin.goto('/admin/usuarios');
  await expect(admin.getByText(encarregadoUsga.email)).toHaveCount(0);
  await expect(admin.getByText(encarregadoTrap.email)).toBeVisible();
  expect((await request.get('/api/v1/employees?pageSize=100', { headers: authed(trapAdmin.token) })).status()).toBe(403);
  const trapEmployees = await (await request.get('/api/v1/employees?pageSize=100', { headers: authed(auxiliarTrap.token) })).json();
  expect((trapEmployees.data.items as Array<{ managementUnitCode: string }>).every((e) => e.managementUnitCode.startsWith('TRAP'))).toBeTruthy();
  expect((await (await request.get('/api/v1/employees?searchTerm=60090004', { headers: authed(auxiliarTrap.token) })).json()).data.items).toHaveLength(0);

  // 6. O jurídico vê as duas empresas em "Todas" e não consegue escrever nesse modo.
  const usgaRegistrar = await createUserApi(request, usgaAdmin, { fullName: 'AUXILIAR USGA TESTE', email: `auxiliar.usga.${stamp}@sigh.local`, cpf: syntheticCpf('711000005'), roles: ['HrRegistrar'] });
  // Um funcionário da USGA que não é o do records.spec (60090004), para não interferir na fila dele.
  const usgaEmployees: Array<{ id: string; employeeNumber: string; status: number | string }> =
    (await (await request.get('/api/v1/employees?pageSize=100', { headers: authed(usgaRegistrar.token) })).json()).data.items;
  const davi = usgaEmployees.find((e) => e.employeeNumber !== '60090004' && (e.status === 2 || e.status === 'Active'))!;
  expect(davi, 'um funcionário ativo da USGA').toBeTruthy();
  const usgaInfraction = await request.post('/api/v1/infraction-types', { headers: authed(usgaAdmin), data: { code: `USG${stamp % 100000}`, name: 'Atraso na USGA (E2E)', defaultSeverity: 2 } });
  const usgaRecord = await request.post('/api/v1/disciplinary-records', {
    headers: authed(usgaRegistrar.token),
    data: {
      employeeId: davi.id, infractionTypeId: (await usgaInfraction.json()).data.id, measureType: 3,
      occurrenceDates: [isoDay(-1)], measureDate: isoDay(0), description: 'Medida da USGA para o modo Todas.', confirmPossibleDuplicate: true,
    },
  });
  expect(usgaRecord.status(), await usgaRecord.text()).toBe(201);

  const legal = await newPage(browser);
  await loginUi(legal, juridico.email, juridico.password);
  await choose(legal, 'Empresa', 'Todas');
  await expect(legal.getByTestId('company-banner')).toContainText('somente consulta');
  await legal.goto('/medidas');
  await expect(legal.getByTestId('record-company').filter({ hasText: 'Trapiche' }).first()).toBeVisible();
  await expect(legal.getByTestId('record-row').filter({ hasText: davi.employeeNumber }).first()).toBeVisible();
  await legal.goto(`/medidas/${recordId}`);
  await expect(legal.getByTestId('record-status')).toHaveText('Aprovada');
  await expect(legal.getByRole('button', { name: /Aprovar|Rejeitar|Anexar/ })).toHaveCount(0);
  const legalToken = await apiToken(request, juridico.email, juridico.password);
  const blocked = await request.post(`/api/v1/disciplinary-records/${recordId}/cancel`, {
    headers: { ...authed(legalToken), ...company('TODAS') },
    data: { reason: 'Tentativa no modo Todas.' },
  });
  expect(blocked.status(), 'o jurídico não tem permissão de escrita em nenhuma empresa').toBe(403);
  // Quem TEM a permissão de escrita também é recusado no modo "Todas": é preciso escolher a empresa.
  const allMode = await request.post(`/api/v1/disciplinary-records/${recordId}/approve`, {
    headers: { ...authed(encarregadoTrap.token), ...company('TODAS') },
  });
  expect(allMode.status()).toBe(400);
  expect((await allMode.json()).code).toBe('COMPANY_REQUIRED');

  // Empresa não autorizada é 404 (nunca 401, que a tela entende como sessão expirada) e a sessão continua valendo.
  const otherCompany = await request.get(`/api/v1/notifications?companyId=${randomUUID()}`, { headers: authed(encarregadoTrap.token) });
  expect(otherCompany.status()).toBe(404);
  expect((await otherCompany.json()).code).toBe('COMPANY_NOT_FOUND');
  expect((await request.get('/api/v1/auth/me', { headers: authed(encarregadoTrap.token) })).status()).toBe(200);
  expect((await request.get('/api/v1/notifications', { headers: authed(encarregadoTrap.token) })).status()).toBe(200);
  // Sem cabeçalho, quem tem duas empresas precisa escolher: o servidor não usa "a primeira".
  const noHeader = await request.get('/api/v1/disciplinary-records', { headers: authed(legalToken) });
  expect(noHeader.status()).toBe(400);
  expect((await noHeader.json()).code).toBe('COMPANY_REQUIRED');
  await expect(legal.getByTestId('company-banner')).toContainText('somente consulta');
});
