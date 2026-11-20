import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { DecisionSupportPanel } from '../pages/EmployeeDetailPage';
import type { DecisionSupport } from '../api/employeesApi';

const base: DecisionSupport = {
  employeeId: 'e1',
  totalMeasures: 123,
  warnings: 70,
  verbalWarnings: 0,
  writtenWarnings: 1,
  suspensions: 3,
  suspensionDays: 45,
  suspensionsWithoutPeriod: 0,
  otherMeasures: 0,
  byType: [{ label: 'Suspensão', count: 3 }],
  byInfractionType: [{ label: 'Falta', count: 3 }],
  lastMeasureAt: '2026-11-20T12:00:00Z',
  measuresLast6Months: 12,
  measuresLast12Months: 30,
  sources: ['Sigh'],
};

describe('DecisionSupportPanel', () => {
  it('uses the right plural and joins the warnings with " · "', () => {
    render(<DecisionSupportPanel data={{ ...base, suspensionsWithoutPeriod: 1 }} />);
    const panel = within(screen.getByRole('region', { name: 'Apoio à decisão' }));
    const hints = panel.getAllByTestId('support-stat-hint').map((h) => h.textContent);
    expect(hints).toEqual(['0 verbais · 1 escrita', '45 dias no total+1 suspensão sem dias informados']);
    expect(screen.getByRole('region', { name: 'Apoio à decisão' }).textContent).not.toMatch(/\((s|is|ões)\)/);
  });

  it('shows "1 dia no total" and the full date of the last measure', () => {
    render(<DecisionSupportPanel data={{ ...base, suspensionDays: 1, verbalWarnings: 1, writtenWarnings: 0 }} />);
    expect(screen.getAllByTestId('support-stat-hint').map((h) => h.textContent)).toEqual(['1 verbal · 0 escritas', '1 dia no total']);
    expect(within(screen.getByTestId('support-stat-last')).getByTestId('support-stat-value')).toHaveTextContent(/^20\/11\/2026$/);
  });

  it('shows "Nenhuma" when there is no last measure', () => {
    render(<DecisionSupportPanel data={{ ...base, lastMeasureAt: null }} />);
    expect(within(screen.getByTestId('support-stat-last')).getByTestId('support-stat-value')).toHaveTextContent('Nenhuma');
  });

  it('shows the six cards in the required order (row 1: total, warnings, suspensions; row 2: last, 6 and 12 months)', () => {
    render(<DecisionSupportPanel data={base} />);
    expect(screen.getAllByTestId('support-stat-label').map((l) => l.textContent)).toEqual([
      'Medidas aplicadas',
      'Advertências',
      'Suspensões',
      'Última medida',
      'Últimos 6 meses',
      'Últimos 12 meses',
    ]);
  });

  it('origin breakdown: singular for 1 ("1 advertência — 1 histórica")', () => {
    render(
      <DecisionSupportPanel
        data={{ ...base, historicalMeasures: 3, byOrigin: [{ label: 'Advertências', total: 1, historical: 1, sigh: 0 }, { label: 'Suspensões', total: 2, historical: 2, sigh: 0 }] }}
      />,
    );
    const rows = screen.getAllByTestId('origin-breakdown').map((r) => r.textContent);
    expect(rows).toEqual(['1 advertência — 1 histórica, 0 no SIGH', '2 suspensões — 2 históricas, 0 no SIGH']);
  });
});
