import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { brand, darkTheme, lightTheme } from '../../src/theme';

// ADR 0003: one Fluent UI brand theme for the studio and the runtime's consoles, a copy in each app kept the same.

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..', '..', '..');

describe('the brand theme (ADR 0003)', () => {
  it('is the studio\'s theme, byte for byte', () => {
    const mine = fs.readFileSync(path.join(repo, 'runtime', 'ops-console', 'src', 'theme.ts'), 'utf8');
    expect(mine).toBe(fs.readFileSync(path.join(repo, 'studio', 'web', 'src', 'theme.ts'), 'utf8'));
  });

  it('is navy around #0f4c81, in light and in dark, with the Segoe UI font stack', () => {
    expect(brand[80]).toBe('#0f4c81');
    expect(lightTheme.colorBrandBackground).toBe('#0f4c81');
    expect(darkTheme.colorBrandBackground).toBe(brand[100]);
    expect(lightTheme.fontFamilyBase).toMatch(/^'Segoe UI', 'Noto Sans', system-ui/);
    expect(darkTheme.colorNeutralBackground1).not.toBe(lightTheme.colorNeutralBackground1);
  });
});
