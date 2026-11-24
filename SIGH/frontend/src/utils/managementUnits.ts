/**
 * Chave Organização (gerência do SAP): o prefixo da empresa sozinho (USGA, TRAP) ou o prefixo seguido de exatamente
 * 3 dígitos (USGA010, TRAP180). Ordem de exibição: por prefixo; dentro dele, a chave sem sufixo primeiro e depois os
 * números em ordem (USGA, USGA010, USGA131…; TRAP, TRAP180…TRAP300). Fora do formato vai depois; sem código, por último.
 * Mesma regra do backend (ManagementUnitCodes.Compare).
 */
const split = (code: string): [string, number] => {
  const letters = /^[A-Z]*/.exec(code)?.[0] ?? '';
  if (!letters) return [code, Number.MAX_SAFE_INTEGER];
  const suffix = code.slice(letters.length);
  if (suffix === '') return [letters, -1];
  return /^\d{3}$/.test(suffix) ? [letters, Number(suffix)] : [letters, Number.MAX_SAFE_INTEGER];
};

const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

/** A chave está num dos dois formatos para o prefixo? (TRAP12, TRAP1234, USGATRAP, "TRAP 230" e trap230 não.) */
export const isValidManagementUnitCode = (code: string | null | undefined, prefix: string): boolean => {
  if (!code || !prefix || !code.startsWith(prefix)) return false;
  const suffix = code.slice(prefix.length);
  return /^[A-Z]+$/.test(prefix) && (suffix === '' || /^\d{3}$/.test(suffix));
};

export const compareManagementUnitCodes = (x: string | null | undefined, y: string | null | undefined): number => {
  if (x === y) return 0;
  if (x == null) return 1;
  if (y == null) return -1;
  const [px, nx] = split(x);
  const [py, ny] = split(y);
  return ordinal(px, py) || nx - ny || ordinal(x, y);
};

/** Cópia ordenada pela Chave Organização (e pelo nome, no empate). */
export const sortByManagementUnitCode = <T extends { code?: string | null; name: string }>(units: readonly T[]): T[] =>
  [...units].sort((a, b) => compareManagementUnitCodes(a.code, b.code) || a.name.localeCompare(b.name, 'pt-BR'));
