import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { parseYaml } from '../src/core/yaml.js';
import { checkDocument } from '../src/checks/checks.js';
import { nextConfigurationSetRelease } from '../web/src/release.js';
import { freshEnv } from './helpers.js';

// The Tenant view's "Bump the release" (R-11-102, lint L53): a bank's configuration is released as a major or a minor,
// so the button proposes the next minor, and the configuration set it gives can be saved.
describe('bumping a configuration set release', () => {
  it('proposes the next minor, never a patch', () => {
    expect(nextConfigurationSetRelease('1.10.0')).toBe('1.11.0');
    expect(nextConfigurationSetRelease('1.10.1')).toBe('1.11.0');
    expect(nextConfigurationSetRelease('2.0.0')).toBe('2.1.0');
    expect(nextConfigurationSetRelease(undefined)).toBe('1.1.0');
  });

  it('gives a release that lint L53 accepts, where the old patch bump was refused', () => {
    const env = freshEnv();
    const doc: any = parseYaml(readFileSync(new URL('../../content-configurations/banks/example-bank/configuration-set.yaml', import.meta.url), 'utf8'));
    const l53 = (release: string) => checkDocument(env, { ...doc, release }, 'example-bank').filter((i) => i.code === 'L53');
    expect(l53('1.10.1').length).toBe(1);
    expect(l53(nextConfigurationSetRelease('1.10.0'))).toEqual([]);
  });
});
