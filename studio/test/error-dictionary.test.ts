import { describe, expect, it } from 'vitest';
import { exportPackage } from '../src/export/exporter.js';
import { parseYaml } from '../src/core/yaml.js';
import { freshEnv } from './helpers.js';

// The error dictionary ships with every package (R-05-52): the dictionaries of each scope the package carries and the
// platform's, so the service console links every error of a payment to its runbook from the package it is pinned to.

function scopeOf(name: string) {
  const p = name.split('.');
  return p[0] === 'p' || p[0] === 'b' ? p[0] : p.slice(0, 2).join('.');
}

describe('the error dictionary in a package (R-05-51, R-05-52)', () => {
  const env = freshEnv();

  it('ships the dictionaries of p, the cluster and the scheme with the corporate workflow, listed in the manifest', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'temporal' });
    const errors = Object.keys(pkg.files).filter((f) => f.startsWith('errors/')).sort();
    expect(errors).toEqual(['errors/p.errors.platform.v1@1.8.0.yaml', 'errors/uae.p.errors.cluster.v1@1.0.0.yaml', 'errors/uae.wps.errors.wps.v1@1.1.0.yaml']);
    for (const f of errors) expect(pkg.manifest.files.map((x: any) => x.path)).toContain(f);
    expect(parseYaml(pkg.files['errors/p.errors.platform.v1@1.8.0.yaml']).kind).toBe('error-dictionary');
  });

  it('has an entry, with a runbook, for every error an activity in the package declares', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'dtfx' });
    const dict = new Map<string, any>();
    for (const [f, text] of Object.entries(pkg.files)) {
      if (!f.startsWith('errors/')) continue;
      const d = parseYaml(text as string);
      for (const e of d.entries) dict.set(`${scopeOf(d.name)}/${e.code}`, e);
    }
    const missing: string[] = [];
    for (const [f, text] of Object.entries(pkg.files)) {
      if (!f.startsWith('activities/')) continue;
      const a = parseYaml(text as string);
      const sc = scopeOf(a.name);
      const scopes = sc === 'p' ? ['p'] : sc.endsWith('.p') ? [sc, 'p'] : [sc, `${sc.split('.')[0]}.p`, 'p'];
      for (const e of a.errors ?? []) {
        const entry = scopes.map((s) => dict.get(`${s}/${e.name}`)).find(Boolean);
        if (!entry) missing.push(`${a.name}: ${e.name}`);
        else expect(entry.runbook.checks.length * entry.runbook.safeActions.length).toBeGreaterThan(0);
      }
    }
    expect(missing).toEqual([]);
  });

  it('ships only the platform dictionary with a platform-only workflow', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.commercial.v1'], target: 'temporal' });
    expect(Object.keys(pkg.files).filter((f) => f.startsWith('errors/'))).toContain('errors/p.errors.platform.v1@1.8.0.yaml');
    expect(Object.keys(pkg.files).filter((f) => f.startsWith('errors/uae.wps'))).toEqual([]);
  });
});
