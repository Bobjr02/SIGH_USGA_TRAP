import { describe, expect, it } from 'vitest';
import { countOf, plural } from './plural';

describe('plural (português, sem "(s)" nem "(is)")', () => {
  it.each([
    [0, 'verbal', 'verbais', '0 verbais'],
    [1, 'verbal', 'verbais', '1 verbal'],
    [2, 'verbal', 'verbais', '2 verbais'],
    [0, 'escrita', 'escritas', '0 escritas'],
    [1, 'escrita', 'escritas', '1 escrita'],
    [2, 'escrita', 'escritas', '2 escritas'],
    [0, 'dia', 'dias', '0 dias'],
    [1, 'dia', 'dias', '1 dia'],
    [2, 'dia', 'dias', '2 dias'],
    [0, 'suspensão', 'suspensões', '0 suspensões'],
    [1, 'suspensão', 'suspensões', '1 suspensão'],
    [2, 'suspensão', 'suspensões', '2 suspensões'],
    [0, 'registro', 'registros', '0 registros'],
    [1, 'registro', 'registros', '1 registro'],
    [2, 'registro', 'registros', '2 registros'],
  ])('%i %s → "%s"', (count, singular, pluralForm, expected) => {
    expect(countOf(count, singular, pluralForm)).toBe(expected);
  });

  it('formats the number in pt-BR and keeps the plural for large counts', () => {
    expect(countOf(1248, 'linha', 'linhas')).toBe('1.248 linhas');
  });

  it('plural() returns only the word', () => {
    expect(plural(1, 'Ocorrência', 'Ocorrências')).toBe('Ocorrência');
    expect(plural(3, 'Ocorrência', 'Ocorrências')).toBe('Ocorrências');
    expect(plural(0, 'Ocorrência', 'Ocorrências')).toBe('Ocorrências');
  });

  it('never produces "(s)", "(is)" or "(ões)"', () => {
    for (const n of [0, 1, 2]) expect(countOf(n, 'verbal', 'verbais')).not.toMatch(/\((s|is|ões)\)/);
  });
});
