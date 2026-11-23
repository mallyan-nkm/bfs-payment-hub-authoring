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
    const pkg = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'dtfx' });
    const t = pkg.manifest.tasks.find((x: any) => x.task === 'uae.wps.exchange.send-salary-file.v1@1.0.0');
    expect(t.queue).toBe('th-uae-wps');
  });
  it('serves a handler on the interop endpoint (R-09-05)', () => {
    // The factory has no handler now (docs/ISSUES.md I-38), so the export uses a minimal synthetic one.
    const e = freshEnv();
    const handler = {
      kind: 'workflow', wfKind: 'handler', name: 'uae.wps.handler.synthetic.v1', release: '1.0.0', dsl: '1.0.0', title: 'Synthetic handler',
      exit: { outcomes: ['answered', 'unknown'] }, do: [{ id: 'find', call: 'p.enrich.payment-record.v1', handle: { 'not-found': { end: 'unknown' } } }, { end: 'answered' }],
      provenance: { origin: 'factory', status: 'provisional' },
    };
    e.store.publish('owner', e.store.saveDraft('owner', 'maker', handler).key, 'maker', 'checker');
    const pkg = exportPackage(e, { workflows: [handler.name], target: 'dtfx' });
    expect(pkg.manifest.tasks.find((x: any) => x.kind === 'handler')).toMatchObject({ task: 'uae.wps.handler.synthetic.v1@1.0.0', endpoint: 'interop' });
  });
  it('gives the same bytes for the same input (R-09-15)', () => {
    const a = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'flowable', tenant: 'example-bank' });
    const b = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'flowable', tenant: 'example-bank' });
    expect(exportZip(a).equals(exportZip(b))).toBe(true);
    expect(packageHash(a)).toBe(packageHash(b));
  });
  it('generates BPMN with external worker tasks, timer boundaries and an event-based await (R-09-12)', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.commercial.v1', 'p.wf.corporate.v1'], target: 'flowable' });
    const x = pkg.files['bpmn/uae.wps.wf.salary-file-out.v1@1.4.0.bpmn20.xml'];
    expect(x).toMatch(/flowable:type="external-worker" flowable:topic="th-uae-wps:uae.wps.exchange.send-salary-file.v1@1.0.0"/);
    expect(x).toMatch(/<eventBasedGateway/);
    expect((x.match(/<process /g) ?? []).length).toBe(1);
    const com = pkg.files['bpmn/p.wf.commercial.v1@1.0.0.bpmn20.xml'];
    expect(com).toMatch(/<boundaryEvent id="deadline" attachedToRef="main"/);
    expect(com).toMatch(/__finally/);
  });
  it('carries a tenant configuration set and the hooks it binds (R-06-18)', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'temporal', tenant: 'example-bank' });
    expect(Object.keys(pkg.files)).toContain('configuration/example-bank.yaml');
    expect(Object.keys(pkg.files).some((f) => f.startsWith('hooks/b.hook.pre-release-approval.v1'))).toBe(true);
    expect(pkg.manifest.containsSimulators).toBe(false);
  });
  it('ships the expanded form, pins the composite and its subflow, and makes a block a sub-process (R-09-17, R-05-41)', () => {
    const pkg = exportPackage(env, { workflows: ['p.wf.corporate.v1'], target: 'flowable' });
    const leg = parseYaml(pkg.files['workflows/uae.wps.wf.salary-file-out.v1@1.4.0.yaml']);
    expect(leg.form).toBe('expanded');
    expect(leg.expandedFrom.release).toBe('1.4.0');
    const pins = leg.artefacts.map((a: any) => a.name);
    expect(pins).toEqual(expect.arrayContaining(['uae.wps.exchange.submit-salary-file.v1', 'uae.wps.sub.submit-salary-file.v1', 'uae.wps.exchange.send-salary-file.v1', 'uae.wps.map.ack-in.v1', 'p.protect.encrypt-file.v1']));
    const x = pkg.files['bpmn/uae.wps.wf.salary-file-out.v1@1.4.0.bpmn20.xml'];
    expect(x).toMatch(/<subProcess id="block_\d+" name="submit">/);
    expect(x).toMatch(/errorRef="ret_pending"/);
  });
  it('marks a duration parameter, an await family and a block start for the Flowable binding (R-05-43 to R-05-45)', () => {
    const pkg = exportPackage(env, { workflows: ['uae.wps.wf.agent-payment-in.v2'], target: 'flowable' });
    const x = pkg.files['bpmn/uae.wps.wf.agent-payment-in.v2@2.2.0.bpmn20.xml'];
    expect(x).not.toMatch(/object Object/);
    // The wait and the timeout are written with the default, and marked with the parameter the binding resolves.
    expect(x).toMatch(/<intermediateCatchEvent id="return-credits\.upload-hold" name="wait:PT0S"><extensionElements><th:timer parameter="uae\.wps\.exchange\.return-credits\.v1#uploadHold" default="PT0S"\/><\/extensionElements><timerEventDefinition><timeDuration>PT0S<\/timeDuration>/);
    expect(x).toMatch(/<th:timer [^>]*parameter="uae\.wps\.exchange\.return-credits\.v1#replyWait" default="P30D" calendarParameter="uae\.wps\.exchange\.return-credits\.v1#replyWaitCalendar"\/><\/extensionElements><timerEventDefinition><timeDuration>P30D<\/timeDuration>/);
    expect(x).toMatch(/<th:point kind="text" text="return-credits\.upload-hold:PT0S" record="detail" timer="return-credits\.upload-hold" prefix="return-credits\.upload-hold"\/>/);
    expect(x).toMatch(/<th:await step="return-credits\.await-ack" message="uae\.wps\.ack" correlateBy="header\.fileId" family="rfa"\/>/);
    // The REST binding learns when each composite step began at a point of its own.
    expect(x).toMatch(/<th:point kind="blockStart" step="return-credits"\/>/);
  });
  it('marks the acknowledgement wait a bank sets for the Flowable binding (R-05-47)', () => {
    const pkg = exportPackage(env, { workflows: ['uae.wps.wf.salary-file-out.v1'], target: 'flowable' });
    const x = pkg.files['bpmn/uae.wps.wf.salary-file-out.v1@1.4.0.bpmn20.xml'];
    // The timer is written with the policy's default, and names the parameters the binding resolves.
    expect(x).toMatch(/<th:timer [^>]*parameter="uae\.wps\.exchange\.submit-salary-file\.v1#ackWait" default="P1D" calendarParameter="uae\.wps\.exchange\.submit-salary-file\.v1#ackWaitCalendar"\/>/);
  });
});
