import * as fs from 'node:fs';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { expandWorkflow, checkExpanded } from '../src/core/expand.js';
import { parseYaml } from '../src/core/yaml.js';
import { freshEnv } from './helpers.js';

describe('expansion of composite steps (C04-S6, R-05-35)', () => {
  const env = freshEnv();
  const resolve = (n: string) => env.store.doc(n);
  const dir = path.join(env.specRoot, 'expanded');
  const files = fs.readdirSync(dir).filter((f) => f.endsWith('.yaml'));
  it('finds the four reference expansions', () => expect(files.length).toBe(4));
  for (const f of files) {
    it(`gives the same document as tools/expand.py for ${f}`, () => {
      const want = parseYaml(fs.readFileSync(path.join(dir, f), 'utf8'));
      const got = expandWorkflow(env.store.doc(f.replace(/\.yaml$/, '')), resolve);
      expect(got).toEqual(want);
      expect(checkExpanded(got, resolve)).toEqual([]);
    });
  }
  it('is a pure function', () => {
    const a = expandWorkflow(env.store.doc('uae.aani.wf.credit-out.v1'), resolve);
    const b = expandWorkflow(env.store.doc('uae.aani.wf.credit-out.v1'), resolve);
    expect(JSON.stringify(a)).toBe(JSON.stringify(b));
  });
  it('leaves a workflow with no composite as it is', () => {
    const d = env.store.doc('p.wf.corporate.v1');
    expect(expandWorkflow(d, resolve)).toBe(d);
  });
});
