import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { employeesApi, type ImportPreview } from '../../employees/api/employeesApi';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http } from 'msw';
import { server } from '../../../test/server';
import { API, EMPTY_OVERVIEW, makeUser, meReturns, ok, problem, refreshSucceeds, renderApp } from '../../../test/auth';
import { PERMISSIONS } from '../../../auth/permissions';
import type { UserCompany } from '../../../auth/authApi';

const READ = [PERMISSIONS.DISCIPLINARY_RECORDS_VIEW, PERMISSIONS.DISCIPLINARY_RECORDS_VIEW_ALL, PERMISSIONS.DISCIPLINARY_REPORTS_VIEW_DASHBOARD, PERMISSIONS.EMPLOYEES_VIEW];

const company = (code: string, name: string, permissions: string[]): UserCompany => ({
  id: `${code}-id`,
  code,
  name,
  managementUnitPrefix: code,
  roles: [],
  permissions,
  managementUnitIds: [],
});

const usga = company('USGA', 'Usina Serra Grande S.A.', [...READ, PERMISSIONS.DISCIPLINARY_RECORDS_REGISTER]);
const trap = company('TRAP', 'Trapiche', [...READ, PERMISSIONS.DISCIPLINARY_RECORDS_APPROVE]);

/** Cabeçalho X-SIGH-Company de cada requisição ao painel. */
const seen: (string | null)[] = [];

const commonHandlers = () => [
  refreshSucceeds(),
  http.get(`${API}/disciplinary-records/overview`, ({ request }) => {
    seen.push(request.headers.get('X-SIGH-Company'));
    return ok(EMPTY_OVERVIEW);
  }),
  http.get(`${API}/disciplinary-records/pending-count`, () => ok(0)),
  http.get(`${API}/notifications/unread/count`, () => ok(0)),
];

beforeEach(() => {
  seen.length = 0;
  window.localStorage.clear();
});

afterEach(() => vi.restoreAllMocks());

describe('empresa ativa', () => {
  it('a single-company user has no switcher, sees the company banner and sends the company header', async () => {
    server.use(...commonHandlers(), meReturns(makeUser({ id: 'u-1', permissions: usga.permissions, companies: [usga] })));
    renderApp('/painel');

    expect(await screen.findByTestId('company-banner')).toHaveTextContent('Empresa: Usina Serra Grande S.A.');
    expect(screen.queryByTestId('company-switcher')).not.toBeInTheDocument();
    await waitFor(() => expect(seen).toContain('USGA'));
    expect(screen.getByRole('link', { name: /Registrar medida/ })).toBeInTheDocument();
  });

  it('a user of two companies switches between them and "Todas" is read-only (no write menu)', async () => {
    const user = userEvent.setup();
    server.use(...commonHandlers(), meReturns(makeUser({ id: 'u-2', permissions: [], companies: [usga, trap] })));
    renderApp('/painel');

    // Padrão: a empresa do primeiro perfil vigente.
    expect(await screen.findByTestId('company-banner')).toHaveTextContent('Usina Serra Grande S.A.');
    expect(screen.getByRole('link', { name: /Registrar medida/ })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Aprovações/ })).not.toBeInTheDocument();

    // Trapiche: as permissões são as da Trapiche (aprova, não registra).
    await user.click(screen.getByRole('combobox', { name: 'Empresa' }));
    await user.click(await screen.findByRole('option', { name: 'Trapiche' }));
    expect(await screen.findByTestId('company-banner')).toHaveTextContent('Empresa: Trapiche');
    expect(await screen.findByRole('link', { name: /Aprovações/ })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Registrar medida/ })).not.toBeInTheDocument();
    await waitFor(() => expect(seen).toContain('TRAP'));
    expect(window.localStorage.getItem('sigh.company.u-2')).toBe('TRAP');

    // Todas: só leitura, nenhum item de escrita.
    await user.click(screen.getByRole('combobox', { name: 'Empresa' }));
    await user.click(await screen.findByRole('option', { name: 'Todas' }));
    expect(await screen.findByTestId('company-banner')).toHaveTextContent('somente consulta');
    expect(screen.queryByRole('link', { name: /Registrar medida/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Aprovações/ })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Medidas/ })).toBeInTheDocument();
    await waitFor(() => expect(seen).toContain('TODAS'));
  });

  it('remembers the choice per user, and falls back to the first company when it is no longer valid', async () => {
    window.localStorage.setItem('sigh.company.u-3', 'TRAP');
    server.use(...commonHandlers(), meReturns(makeUser({ id: 'u-3', permissions: [], companies: [usga, trap] })));
    const { unmount } = renderApp('/painel');
    expect(await screen.findByTestId('company-banner')).toHaveTextContent('Trapiche');
    unmount();

    // Perdeu a Trapiche: volta para a USGA (e "Todas" com uma empresa só não vale).
    window.localStorage.setItem('sigh.company.u-3', 'TODAS');
    server.use(meReturns(makeUser({ id: 'u-3', permissions: [], companies: [usga] })));
    renderApp('/painel');
    expect(await screen.findByTestId('company-banner')).toHaveTextContent('Usina Serra Grande S.A.');
    expect(window.localStorage.getItem('sigh.company.u-3')).toBe('USGA');
  });
});

describe('administração por empresa', () => {
  const admin = company('TRAP', 'Trapiche', [PERMISSIONS.USERS_VIEW, PERMISSIONS.USERS_CREATE, PERMISSIONS.USERS_MANAGE_ROLES]);

  it('creating a user with an e-mail of another company offers to link the existing account', async () => {
    const user = userEvent.setup();
    let linked: Record<string, unknown> | null = null;
    server.use(
      ...commonHandlers(),
      meReturns(makeUser({ id: 'adm', permissions: [], companies: [admin] })),
      http.get(`${API}/users/admin-status`, () => ok([])),
      http.get(`${API}/users`, () => ok({ items: [], pageNumber: 1, pageSize: 20, totalCount: 0, totalPages: 0 })),
      http.get(`${API}/roles`, () => ok([{ id: 'r-legal', name: 'Legal', label: 'Jurídico', description: '', permissions: [] }])),
      http.get(`${API}/management-units`, () => ok([])),
      http.post(`${API}/users`, () => problem(409, 'Já existe uma conta com este e-mail em outra empresa do grupo.', { code: 'USER_EXISTS_IN_ANOTHER_COMPANY', canLink: true })),
      http.post(`${API}/users/link`, async ({ request }) => {
        linked = (await request.json()) as Record<string, unknown>;
        return ok({ userId: 'u-juridico' });
      }),
      http.get(`${API}/users/u-juridico`, () => ok({ id: 'u-juridico', fullName: 'Jurídico', email: 'juridico@usga.com.br', status: 'Active', roles: [], permissions: [], createdAt: '2026-11-01T00:00:00Z' })),
      http.get(`${API}/users/u-juridico/access`, () => ok({ userId: 'u-juridico', roles: [], managementUnits: [], hasAccessInOtherCompany: true })),
    );
    renderApp('/admin/usuarios');

    await user.click(await screen.findByRole('button', { name: 'Novo usuário' }));
    const dialog = await screen.findByRole('dialog', { name: 'Novo usuário' });
    expect(within(dialog).getByTestId('create-user-company')).toHaveTextContent('Trapiche');
    await user.type(within(dialog).getByLabelText(/Nome completo/), 'JURIDICO TESTE');
    await user.type(within(dialog).getByLabelText(/E-mail/), 'juridico@usga.com.br');
    await user.type(within(dialog).getByLabelText(/CPF/), '52998224725');
    await user.click(within(dialog).getByLabelText('Jurídico'));
    await user.click(within(dialog).getByRole('button', { name: 'Criar usuário' }));

    expect(await within(dialog).findByTestId('link-offer')).toHaveTextContent('vincular este usuário à Trapiche');
    await user.click(within(dialog).getByRole('button', { name: 'Vincular este usuário à Trapiche' }));
    await waitFor(() => expect(linked).toEqual({ email: 'juridico@usga.com.br', roleIds: ['r-legal'], managementUnitIds: [] }));
    expect(await screen.findByTestId('other-company-access')).toBeInTheDocument();
  });
});

describe('importação por empresa', () => {
  it('shows the target company and the big alert when the file looks like another company', async () => {
    const user = userEvent.setup();
    const importer = company('USGA', 'Usina Serra Grande S.A.', [PERMISSIONS.EMPLOYEES_IMPORT, PERMISSIONS.EMPLOYEES_VIEW]);
    server.use(
      ...commonHandlers(),
      meReturns(makeUser({ id: 'imp', permissions: [], companies: [importer] })),
      http.get(`${API}/employees/import/batches`, () => ok({ items: [], pageNumber: 1, pageSize: 20, totalCount: 0 })),
    );
    vi.spyOn(employeesApi, 'previewImport').mockResolvedValue(({
          batchId: 'b-1', fileName: 'trapiche.xlsx', fileSha256: 'x', expiresAt: '2026-11-18T12:00:00Z',
          summary: { totalRows: 2, created: 0, updated: 0, terminated: 0, terminatedByAbsence: 0, reactivated: 0, unchanged: 0, errors: 2, warnings: 0 },
          created: [], updated: [], toTerminate: [], reactivated: [], unchanged: [],
          errors: [
            { row: 2, employeeNumber: '70090001', severity: 'Error', message: 'Chave Organização TRAP220: gerência de outra empresa — confira se o arquivo é desta empresa.' },
            { row: 3, employeeNumber: '70090002', severity: 'Error', message: 'Chave Organização TRAP220: gerência de outra empresa — confira se o arquivo é desta empresa.' },
          ],
          warnings: [], activeBefore: 0, absenceTerminationPercent: 0, maxTerminationPercent: 10,
          requiresMassTerminationConfirmation: false, companyCode: 'USGA', companyName: 'Usina Serra Grande S.A.', otherCompanyRows: 2, looksLikeOtherCompany: true,
        }) as ImportPreview);
    renderApp('/employees/import');

    expect(await screen.findByTestId('import-target')).toHaveTextContent('Importando para: Usina Serra Grande S.A.');
    const file = new File([new Uint8Array([0x50, 0x4b, 0x03, 0x04])], 'trapiche.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' });
    await user.upload(screen.getByLabelText('Planilha do SAP'), file);
    await user.click(screen.getByRole('button', { name: 'Pré-visualizar' }));

    expect(await screen.findByTestId('other-company-alert')).toHaveTextContent('Este arquivo parece ser de outra empresa');
    expect(screen.getByRole('button', { name: 'Confirmar importação' })).toBeDisabled();
  });
});

describe('modo "Todas": empresa de cada item e nada escolhido implicitamente', () => {
  const both = (permissions: string[]) => [company('USGA', 'Usina Serra Grande S.A.', permissions), company('TRAP', 'Trapiche', permissions)];
  const emptyBatches = { items: [], pageNumber: 1, pageSize: 20, totalCount: 0 };

  it('the users list shows the Empresa column only in "Todas"', async () => {
    window.localStorage.setItem('sigh.company.adm2', 'TODAS');
    server.use(
      ...commonHandlers(),
      meReturns(makeUser({ id: 'adm2', permissions: [], companies: both([PERMISSIONS.USERS_VIEW]) })),
      http.get(`${API}/users/admin-status`, () => ok([])),
      http.get(`${API}/users`, () =>
        ok({
          items: [
            { id: 'u-a', fullName: 'Pessoa das duas', email: 'a@x', cpf: '', status: 'Active', mustChangePassword: false, roles: ['Legal'], createdAt: '2026-11-01T00:00:00Z', companies: ['Trapiche', 'Usina Serra Grande S.A.'] },
          ],
          pageNumber: 1, pageSize: 20, totalCount: 1, totalPages: 1,
        }),
      ),
    );
    renderApp('/admin/usuarios');
    expect(await screen.findByRole('columnheader', { name: 'Empresa' })).toBeInTheDocument();
    expect(await screen.findByTestId('user-company')).toHaveTextContent('Trapiche, Usina Serra Grande S.A.');
  });

  it('the employee import opens read-only in "Todas": asks for a company and shows the company of each batch', async () => {
    window.localStorage.setItem('sigh.company.imp2', 'TODAS');
    server.use(
      ...commonHandlers(),
      meReturns(makeUser({ id: 'imp2', permissions: [], companies: both([PERMISSIONS.EMPLOYEES_IMPORT, PERMISSIONS.EMPLOYEES_VIEW]) })),
      http.get(`${API}/employees/import/batches`, () =>
        ok({
          ...emptyBatches,
          totalCount: 1,
          items: [{ id: 'b-1', fileName: 'sap.xlsx', fileSha256: 'x', status: 'Applied', previewedAt: '2026-11-01T00:00:00Z', appliedAt: '2026-11-01T00:00:00Z', summary: { created: 1, updated: 0, terminated: 0, terminatedByAbsence: 0, reactivated: 0, errors: 0 }, massTerminationConfirmed: false, companyName: 'Trapiche' }],
        }),
      ),
    );
    renderApp('/employees/import');
    expect(await screen.findByTestId('import-company-required')).toHaveTextContent('Selecione uma empresa');
    expect(await screen.findByTestId('batch-company')).toHaveTextContent('Trapiche');
    expect(screen.getByRole('button', { name: 'Pré-visualizar' })).toBeDisabled();
    expect(screen.getByRole('link', { name: /Importar funcionários/ })).toBeInTheDocument();
  });

  it('the history import in "Todas" never asks the server for the status and asks for a company', async () => {
    window.localStorage.setItem('sigh.company.imp3', 'TODAS');
    let statusCalls = 0;
    server.use(
      ...commonHandlers(),
      meReturns(makeUser({ id: 'imp3', permissions: [], companies: both([PERMISSIONS.DISCIPLINARY_HISTORY_IMPORT]) })),
      http.get(`${API}/disciplinary-history/import/status`, () => {
        statusCalls++;
        return problem(400, 'Selecione uma empresa no topo da tela.', { code: 'COMPANY_REQUIRED' });
      }),
      http.get(`${API}/disciplinary-history/import/batches`, () =>
        ok({
          ...emptyBatches,
          totalCount: 1,
          items: [{ id: 'h-1', fileName: 'penalidades.xlsx', fileSha256: 'x', status: 'Applied', previewedAt: '2026-11-01T00:00:00Z', summary: { totalRows: 1, toImport: 1, existing: 0, toUpdate: 0, rejected: 0, warnings: 0, newInfractionTypes: 0, absent: 0 }, companyName: 'Usina Serra Grande S.A.' }],
        }),
      ),
    );
    renderApp('/admin/history-import');
    expect(await screen.findByTestId('import-company-required')).toHaveTextContent('Selecione uma empresa');
    expect(await screen.findByTestId('batch-company')).toHaveTextContent('Usina Serra Grande S.A.');
    expect(screen.queryByTestId('history-closed')).not.toBeInTheDocument();
    expect(statusCalls).toBe(0);
  });

  it('notifications in "Todas" say the company of each item and never pick a company by companyId', async () => {
    window.localStorage.setItem('sigh.company.n1', 'TODAS');
    const items = [
      { id: 'n-u', companyId: 'USGA-id', companyName: 'Usina Serra Grande S.A.', recipientUserId: 'n1', title: 'Aviso USGA', message: 'm', type: 'Info', priority: 'Medium', createdAt: '2026-11-01T00:00:00Z', isRead: false, isExpired: false },
      { id: 'n-t', companyId: 'TRAP-id', companyName: 'Trapiche', recipientUserId: 'n1', title: 'Aviso TRAP', message: 'm', type: 'Info', priority: 'Medium', createdAt: '2026-11-01T00:00:00Z', isRead: false, isExpired: false },
    ];
    const companiesAsked: (string | null)[] = [];
    server.use(
      ...commonHandlers(),
      meReturns(makeUser({ id: 'n1', permissions: [], companies: both([PERMISSIONS.NOTIFICATIONS_VIEW, PERMISSIONS.NOTIFICATIONS_READ]) })),
      http.get(`${API}/notifications`, ({ request }) => {
        companiesAsked.push(new URL(request.url).searchParams.get('companyId'));
        return ok({ items, pageNumber: 1, pageSize: 10, totalCount: 2, totalPages: 1 });
      }),
    );
    renderApp('/notifications');
    const chips = await screen.findAllByTestId('notification-company');
    expect(chips.map((c) => c.textContent)).toEqual(['Usina Serra Grande S.A.', 'Trapiche']);
    // A tela não escolhe empresa pelo companyId: quem decide é o cabeçalho (todas as empresas da requisição).
    expect(companiesAsked.every((c) => c === null)).toBe(true);
  });
});
