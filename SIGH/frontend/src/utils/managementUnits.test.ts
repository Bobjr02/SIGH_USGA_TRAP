import { describe, expect, it } from 'vitest';
import { compareManagementUnitCodes, isValidManagementUnitCode, sortByManagementUnitCode } from './managementUnits';

const OFFICIAL = [
  'USGA', 'USGA010', 'USGA131', 'USGA132', 'USGA133', 'USGA134', 'USGA135', 'USGA136', 'USGA137', 'USGA138', 'USGA141', 'USGA161',
  'TRAP', 'TRAP180', 'TRAP220', 'TRAP230', 'TRAP240', 'TRAP250', 'TRAP260', 'TRAP300',
];

describe('Chave Organização', () => {
  it.each(OFFICIAL)('%s está no formato', (code) => {
    expect(isValidManagementUnitCode(code, code.slice(0, 4))).toBe(true);
  });

  it.each(['TRAP12', 'TRAP1234', 'USGATRAP', 'TRAP 230', '', 'trap230', 'usga'])('"%s" está fora do formato', (code) => {
    expect(isValidManagementUnitCode(code, 'TRAP')).toBe(false);
    expect(isValidManagementUnitCode(code, 'USGA')).toBe(false);
  });

  it('ordena a chave sem sufixo primeiro e depois os números', () => {
    const shuffled = [...OFFICIAL].reverse();
    const usga = shuffled.filter((c) => c.startsWith('USGA')).sort(compareManagementUnitCodes);
    const trap = shuffled.filter((c) => c.startsWith('TRAP')).sort(compareManagementUnitCodes);
    expect(usga).toEqual(OFFICIAL.slice(0, 12));
    expect(trap).toEqual(OFFICIAL.slice(12));
  });

  it('sortByManagementUnitCode deixa sem código por último', () => {
    const sorted = sortByManagementUnitCode([
      { code: 'TRAP300', name: 'b' },
      { code: null, name: 'sem código' },
      { code: 'TRAP', name: 'a' },
      { code: 'TRAP180', name: 'c' },
    ]);
    expect(sorted.map((u) => u.code)).toEqual(['TRAP', 'TRAP180', 'TRAP300', null]);
  });
});
