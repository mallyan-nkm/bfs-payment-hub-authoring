import { describe, expect, it } from 'vitest';
import { exportPackage, exportZip, packageHash } from '../src/export/exporter.js';
import { parseYaml } from '../src/core/yaml.js';
import { freshEnv } from './helpers.js';

describe('export (C05, C20, C22)', () => {
  const env = freshEnv();
  it('pins every artefact a workflow uses, exactly (R-01-14, L23)', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.commercial.v1'], target: 'temporal' });
    expect(pkg.issues).toEqual([]);
    const wf = parseYaml(pkg.files['workflows/p.wf.commercial.v1@1.0.0.yaml']);
    const names = wf.artefacts.map((a: any) => a.name);
    expect(names).toContain('p.seg.check-and-screen.v1');
    expect(names).toContain('p.rule.amount-positive.v1');
    for (const a of wf.artefacts) expect(a.hash).toBe(env.store.get(a.name, a.release)!.hash);
  });
  it('registers tasks as name@release on the queue of their scope (R-09-04, R-09-05)', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.commercial.v1'], target: 'dtfx' });
    const t = pkg.manifest.tasks.find((x: any) => x.task === 'uae.aani.exchange.send-credit.v1@1.0.0');
    expect(t.queue).toBe('th-uae-aani');
    expect(pkg.manifest.tasks.find((x: any) => x.kind === 'handler').endpoint).toBe('interop');
  });
  it('gives the same bytes for the same input (R-09-15)', () => {
    const a = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'flowable', tenant: 'example-bank' });
    const b = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'flowable', tenant: 'example-bank' });
    expect(exportZip(a).equals(exportZip(b))).toBe(true);
    expect(packageHash(a)).toBe(packageHash(b));
  });
  it('generates BPMN with external worker tasks, timer boundaries and an event-based await (R-09-12)', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.commercial.v1'], target: 'flowable' });
    const x = pkg.files['bpmn/uae.aani.wf.credit-out.v1@1.0.0.bpmn20.xml'];
    expect(x).toMatch(/flowable:type="external-worker" flowable:topic="th-uae-aani:uae.aani.exchange.send-credit.v1@1.0.0"/);
    expect(x).toMatch(/<boundaryEvent id="deadline" attachedToRef="main"/);
    expect(x).toMatch(/<eventBasedGateway/);
    expect((x.match(/<process /g) ?? []).length).toBe(1);
    const corp = pkg.files['bpmn/p.wf.commercial.v1@1.0.0.bpmn20.xml'];
    expect(corp).toMatch(/__finally/);
  });
  it('carries a tenant configuration set and the hooks it binds (R-06-18)', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'temporal', tenant: 'example-bank' });
    expect(Object.keys(pkg.files)).toContain('configuration/example-bank.yaml');
    expect(Object.keys(pkg.files).some((f) => f.startsWith('hooks/b.hook.pre-release-approval.v1'))).toBe(true);
    expect(pkg.manifest.containsSimulators).toBe(false);
  });
});
