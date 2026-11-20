import { formatNumber } from './formatters';

/**
 * Plural em português, sem "(s)" nem "(is)": 1 → singular; 0 e 2 ou mais → plural ("0 verbais", "1 verbal",
 * "2 verbais"). A forma plural é sempre informada (não é deduzida: verbal → verbais, suspensão → suspensões).
 */
export const plural = (count: number, singular: string, pluralForm: string): string => (count === 1 ? singular : pluralForm);

/** Número formatado (1.248) + a palavra no singular ou no plural: "1 dia", "2 dias", "1.248 registros". */
export const countOf = (count: number, singular: string, pluralForm: string): string =>
  `${formatNumber(count)} ${plural(count, singular, pluralForm)}`;
