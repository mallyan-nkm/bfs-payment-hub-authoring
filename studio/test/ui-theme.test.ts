import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// ADR 0003: the studio and the operations console share one Fluent UI brand theme. Each app keeps its own copy of the file,
// which must stay the same byte for byte.

const here = path.dirname(fileURLToPath(import.meta.url));
const studio = path.resolve(here, '..', 'web', 'src', 'theme.ts');
const console_ = path.resolve(here, '..', '..', 'runtime', 'ops-console', 'src', 'theme.ts');

describe('the shared Fluent UI theme (ADR 0003)', () => {
  it('is the same file in the studio and in the operations console', () => {
    expect(fs.readFileSync(console_, 'utf8')).toBe(fs.readFileSync(studio, 'utf8'));
  });

  it('builds a navy brand ramp around #0f4c81 with the teal accent and the Segoe UI font stack', () => {
    const src = fs.readFileSync(studio, 'utf8');
    expect(src).toMatch(/80: '#0f4c81'/);
    expect(src).toMatch(/'#5fd3c5'/);
    expect(src).toMatch(/'#0f2d4f'/);
    expect(src).toMatch(/createLightTheme\(brand\)/);
    expect(src).toMatch(/createDarkTheme\(brand\)/);
    expect(src).toContain("'Segoe UI', 'Noto Sans', system-ui");
    for (let shade = 10; shade <= 160; shade += 10) expect(src).toMatch(new RegExp(`\\n  ${shade}: '#[0-9a-f]{6}',`));
  });
});
