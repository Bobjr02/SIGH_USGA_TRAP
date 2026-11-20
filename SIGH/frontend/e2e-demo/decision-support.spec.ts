import { expect, test, type Page } from '@playwright/test';
import { login, profile, settle, THEME_LABEL, THEMES } from './support/demo';

// Quadro "Apoio à decisão" legível em qualquer largura do PAINEL (container queries, não a largura da janela).
// O quadro recebe números grandes (123 medidas, 45 dias) e a data da última medida; em cada largura do contêiner
// (240, 320, 480 e 720 px) confere: quantas colunas, nenhum texto passando da borda (scrollWidth <= clientWidth em
// cada cartão, em cada texto e no painel), nenhum text-overflow cortando, rótulos em no máximo 2 linhas e sem
// quebrar palavra, a data inteira (dd/mm/aaaa) e cartões da mesma linha com a mesma altura. Nos dois temas.

const BIG = {
  employeeId: 'demo',
  totalMeasures: 123,
  warnings: 70,
  verbalWarnings: 35,
  writtenWarnings: 35,
  suspensions: 53,
  suspensionDays: 45,
  suspensionsWithoutPeriod: 2,
  otherMeasures: 0,
  byType: [
    { label: 'Advertência verbal', count: 35 },
    { label: 'Advertência escrita', count: 35 },
    { label: 'Suspensão', count: 53 },
  ],
  byInfractionType: [
    { label: 'Negligência ou imprudência no desempenho de suas funções', count: 61 },
    { label: 'Faltas injustificadas de forma recorrente', count: 62 },
  ],
  lastMeasureAt: '2026-11-20T15:00:00Z',
  measuresLast6Months: 12,
  measuresLast12Months: 30,
  sources: ['Sigh', 'Historical'],
  historicalMeasures: 1,
  sighMeasures: 122,
  byOrigin: [{ label: 'Advertências', total: 70, historical: 1, sigh: 69 }],
};

/** Largura do contêiner → colunas da grade dos cartões. */
const WIDTHS: Array<[number, number]> = [
  [240, 1],
  [320, 2],
  [480, 3],
  [720, 3],
];

const openPanel = async (page: Page) => {
  await page.route('**/api/v1/employees/*/decision-support', (route) => route.fulfill({ json: { success: true, data: BIG } }));
  await page.goto('/employees');
  await settle(page);
  await page.locator('a[aria-label="Ver funcionário"]').first().click();
  await page.getByRole('tab', { name: 'Apoio à decisão' }).click();
  const panel = page.getByTestId('decision-support');
  await expect(panel.getByTestId('support-stats')).toBeVisible();
  return panel;
};

/** Medições feitas no navegador, com o painel na largura pedida. */
const measure = (page: Page, width: number) =>
  page.evaluate((w) => {
    const panel = document.querySelector<HTMLElement>('[data-testid="decision-support"]')!;
    panel.style.width = `${w}px`;
    panel.style.maxWidth = `${w}px`;
    void panel.offsetWidth; // força o layout

    const grid = panel.querySelector<HTMLElement>('[data-testid="support-stats"]')!;
    const cards = Array.from(panel.querySelectorAll<HTMLElement>('[data-testid^="support-stat"]')).filter((el) =>
      ['support-stat', 'support-stat-last'].includes(el.dataset.testid!),
    );
    const describe = (el: Element) => `${el.tagName.toLowerCase()} "${(el.textContent ?? '').trim().slice(0, 40)}"`;

    // Nenhum elemento do painel (blocos com largura) com conteúdo mais largo que a caixa.
    const overflowing = Array.from(panel.querySelectorAll<HTMLElement>('*'))
      .concat(panel)
      .filter((el) => el.clientWidth > 0 && el.scrollWidth > el.clientWidth + 0.5)
      .map((el) => `${describe(el)}: scrollWidth ${el.scrollWidth} > clientWidth ${el.clientWidth}`);

    // Nada sai da borda do próprio cartão (nem do painel).
    const panelBox = panel.getBoundingClientRect();
    const outside: string[] = [];
    for (const card of cards) {
      const box = card.getBoundingClientRect();
      if (box.right > panelBox.right + 0.5) outside.push(`cartão ${describe(card)} passa do painel`);
      for (const child of Array.from(card.querySelectorAll('*'))) {
        const r = child.getBoundingClientRect();
        if (r.width > 0 && (r.right > box.right + 0.5 || r.left < box.left - 0.5)) outside.push(`${describe(child)} passa do cartão`);
      }
    }

    const ellipsis = Array.from(panel.querySelectorAll<HTMLElement>('*'))
      .filter((el) => getComputedStyle(el).textOverflow === 'ellipsis')
      .map(describe);

    // Rótulos: no máximo 2 linhas; nenhuma palavra quebrada (cada palavra ocupa um único retângulo de linha).
    const labels = Array.from(panel.querySelectorAll<HTMLElement>('[data-testid="support-stat-label"]')).map((label) => {
      const lineHeight = parseFloat(getComputedStyle(label).lineHeight);
      return { text: label.textContent ?? '', lines: Math.round(label.getBoundingClientRect().height / lineHeight) };
    });
    const brokenWords: string[] = [];
    for (const el of Array.from(panel.querySelectorAll<HTMLElement>('[data-testid="support-stats"] p'))) {
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const text = node.textContent ?? '';
        for (const match of text.matchAll(/\S+/g)) {
          const range = document.createRange();
          range.setStart(node, match.index!);
          range.setEnd(node, match.index! + match[0].length);
          const lines = new Set(Array.from(range.getClientRects()).map((r) => Math.round(r.top)));
          if (lines.size > 1) brokenWords.push(match[0]);
        }
      }
    }

    // Altura dos cartões por linha da grade.
    const rows = new Map<number, number[]>();
    for (const card of cards) {
      const r = card.getBoundingClientRect();
      const top = Math.round(r.top);
      rows.set(top, [...(rows.get(top) ?? []), Math.round(r.height)]);
    }

    const last = panel.querySelector('[data-testid="support-stat-last"] [data-testid="support-stat-value"]') as HTMLElement;
    const lists = Array.from(panel.querySelectorAll('ul[aria-label="Medidas por tipo"], ul[aria-label="Medidas por motivo"]')).map((ul) =>
      Math.round(ul.getBoundingClientRect().top),
    );

    return {
      columns: getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length,
      cards: cards.length,
      overflowing,
      outside,
      ellipsis,
      labels,
      brokenWords,
      rowHeights: Array.from(rows.values()),
      lastText: last.textContent,
      lastFits: last.scrollWidth <= last.clientWidth,
      listsSideBySide: lists.length === 2 && lists[0] === lists[1],
    };
  }, width);

for (const theme of THEMES) {
  test.describe(`tema ${THEME_LABEL[theme]}`, () => {
    test('decision support panel: readable at 240, 320, 480 and 720 px of container width', async ({ page }) => {
      await login(page, profile('juridico'), theme);
      await openPanel(page);

      for (const [width, columns] of WIDTHS) {
        const m = await measure(page, width);
        const where = `painel com ${width} px (tema ${THEME_LABEL[theme]})`;
        expect(m.cards, where).toBe(6);
        expect(m.columns, `${where}: colunas`).toBe(columns);
        expect(m.overflowing, `${where}: texto passando da borda`).toEqual([]);
        expect(m.outside, `${where}: texto fora do cartão`).toEqual([]);
        expect(m.ellipsis, `${where}: text-overflow cortando`).toEqual([]);
        expect(m.brokenWords, `${where}: palavra quebrada no meio`).toEqual([]);
        for (const label of m.labels) expect(label.lines, `${where}: rótulo "${label.text}" em até 2 linhas`).toBeLessThanOrEqual(2);
        for (const heights of m.rowHeights) expect(new Set(heights).size, `${where}: cartões da mesma linha com a mesma altura (${heights})`).toBe(1);
        expect(m.lastText, `${where}: data inteira`).toBe('20/11/2026');
        expect(m.lastFits, `${where}: a data cabe no cartão`).toBeTruthy();
        expect(m.listsSideBySide, `${where}: "por tipo" e "por motivo" lado a lado só com 420 px ou mais`).toBe(width >= 420);
      }
    });
  });
}
