import { describe, expect, it } from 'vitest';
import { dispatchAs as dispatch } from './support/as-user.js';
import { parseYaml } from '../src/core/yaml.js';
import { buildAndPublish } from '../src/governance/distributions.js';
import * as gov from '../src/governance/governance.js';
import { checkAll, feedRateIssues, portBindingProblems } from '../src/checks/checks.js';
import { freshEnv } from './helpers.js';
import { fake } from './support/content.js';

// A bank's port bindings as a studio artefact (R-11-47, Issue I-70): the Bank Configurator edits them in scope b, they
// are checked against the port contracts (L48), a Bank Approver approves them as a version, and they go out in the
// bank's configuration release, credentials by secretRef only.

const as = (env: any, user: string, method: string, path: string, body?: any) => dispatch(env, { method, path, query: {}, body: body ?? {}, user });

describe('port bindings in the studio (R-11-47)', () => {
  it('starts a draft from the bank folder\'s runtime documents and offers the ports the bank may bind', async () => {
    const env = freshEnv();
    const r = await as(env, 'example.maker', 'GET', '/api/tenants/example-bank/port-bindings');
    expect(r.status).toBe(200);
    expect(r.body.released).toBeNull();
    expect(r.body.runtime.map((x: any) => x.file)).toContain('banks/example-bank/core-banking.yaml');
    expect(r.body.start).toMatchObject({ kind: 'port-bindings', tenant: 'example-bank', release: '1.0.0' });
    expect(r.body.start.bindings.some((b: any) => b.port === 'p.port.core-banking.v1' && b.adapter === 'simulator')).toBe(true);
    expect(r.body.ports.map((p: any) => p.name)).toContain('p.port.core-banking.v1');
    expect(r.body.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    // Another bank's bindings are not this user's to read.
    expect((await as(env, 'example.maker', 'GET', '/api/tenants/second-bank/port-bindings')).status).toBe(403);
  });

  it('a bank on the rates feed types no rates, and no configuration release of a bank is a patch (L52, L53; R-11-101, R-11-102, I-276)', () => {
    const feedBinding = 'kind: port-bindings\ntenant: feed-bank\nbindings:\n  - port: p.port.fx-rates.v1\n    adapter: gateway-rest\n    validFrom: 2026-11-23T00:00:00Z\n    settings: { baseUrl: "https://rates.feed-bank.invalid/" }\n';
    // Each sets requireAsOf, as a bank on the feed does (L60, R-09-247).
    const req = '  - parameter: p.enrich.fx-rate.v1#requireAsOf\n    validFrom: "2026-11-24"\n    value: true\n';
    const rates = 'kind: configuration-set\ntenant: feed-bank\nrelease: 1.2.0\npacks: []\nvalues:\n  - parameter: p.enrich.fx-rate.v1#rates\n    validFrom: "2026-11-01"\n    value: [ { from: USD, to: AED, rate: "3.6725" } ]\n' + req;
    const plain = 'kind: configuration-set\ntenant: feed-bank\nrelease: 1.3.0\npacks: []\nvalues:\n' + req;
    const files = new Map<string, string>([
      ['banks/feed-bank/platform-adapters.yaml', feedBinding],
      ['banks/feed-bank/configuration-set.yaml', plain],
      ['banks/feed-bank/configuration-sets/1.2.0.yaml', rates],
      ['banks/feed-bank/configuration-sets/1.3.0.yaml', plain],
    ]);
    // The newest release sets no rates: nothing to say.
    expect(feedRateIssues(files, 'feed-bank')).toEqual([]);
    // A newer release that types the rates while the bank binds the feed is refused; so is a patch release.
    files.set('banks/feed-bank/configuration-sets/1.4.0.yaml', rates.replace('1.2.0', '1.4.0'));
    files.set('banks/feed-bank/configuration-sets/1.4.1.yaml', plain.replace('1.3.0', '1.4.1'));
    const found = feedRateIssues(files, 'feed-bank');
    expect(found.map((i) => `${i.code} ${i.file}`).sort()).toEqual(['L53 banks/feed-bank/configuration-sets/1.4.1.yaml']);
    files.delete('banks/feed-bank/configuration-sets/1.4.1.yaml');
    expect(feedRateIssues(files, 'feed-bank').map((i) => `${i.code} ${i.file}`)).toEqual(['L52 banks/feed-bank/configuration-sets/1.4.0.yaml']);
    // A bank that does not bind the feed may type its rates.
    files.delete('banks/feed-bank/platform-adapters.yaml');
    expect(feedRateIssues(files, 'feed-bank')).toEqual([]);
    // The real banks bind the feed and type no rates; the example bank types its rates and binds no feed.
    const env = freshEnv();
    expect(checkAll(env).filter((i) => i.code === 'L52' || i.code === 'L53')).toEqual([]);
  });

  it('a bank on the rates feed requires each rate\'s asOf (L60; R-09-247, I-278)', () => {
    const feedBinding = 'kind: port-bindings\ntenant: feed-bank\nbindings:\n  - port: p.port.fx-rates.v1\n    adapter: gateway-rest\n    validFrom: 2026-11-23T00:00:00Z\n    settings: { baseUrl: "https://rates.feed-bank.invalid/" }\n';
    const set = (release: string, value: string) => `kind: configuration-set\ntenant: feed-bank\nrelease: ${release}\npacks: []\nvalues:\n  - parameter: p.enrich.fx-rate.v1#requireAsOf\n    validFrom: "2026-11-24"\n    value: ${value}\n`;
    const files = new Map<string, string>([
      ['banks/feed-bank/platform-adapters.yaml', feedBinding],
      ['banks/feed-bank/configuration-set.yaml', set('1.3.0', 'true')],
      ['banks/feed-bank/configuration-sets/1.3.0.yaml', set('1.3.0', 'true')],
    ]);
    expect(feedRateIssues(files, 'feed-bank')).toEqual([]);
    // A newer release that lets a typed rate stand without its asOf is refused, and so is a working set that leaves it out.
    files.set('banks/feed-bank/configuration-sets/1.4.0.yaml', set('1.4.0', 'false'));
    files.set('banks/feed-bank/configuration-set.yaml', 'kind: configuration-set\ntenant: feed-bank\nrelease: 1.4.0\npacks: []\nvalues: []\n');
    expect(feedRateIssues(files, 'feed-bank').map((i) => `${i.code} ${i.file}`).sort()).toEqual(['L60 banks/feed-bank/configuration-set.yaml', 'L60 banks/feed-bank/configuration-sets/1.4.0.yaml']);
    // A bank that does not bind the feed need not.
    files.delete('banks/feed-bank/platform-adapters.yaml');
    expect(feedRateIssues(files, 'feed-bank')).toEqual([]);
    // The real banks bind the feed and set it.
    expect(checkAll(freshEnv()).filter((i) => i.code === 'L60')).toEqual([]);
  });

  it('checks a binding against the port contracts and refuses a literal credential (L48)', () => {
    const isPort = (n: string) => n === 'p.port.core-banking.v1';
    const ok = { bindings: [{ port: 'p.port.core-banking.v1', adapter: 'gateway-rest', validFrom: '2026-12-01', settings: { baseUrl: 'https://core.example.test' }, secretRef: 'env:TH_CORE_TOKEN' }] };
    expect(portBindingProblems(ok, isPort)).toEqual([]);
    const bad = { bindings: [
      { port: 'p.port.nothing.v1', adapter: 'gateway-rest', validFrom: '2026-12-01' },
      { port: 'p.port.core-banking.v1', adapter: 'gateway-rest', validFrom: '2026-12-01', settings: { apiToken: 'synthetic-literal' } },
      { port: 'p.port.core-banking.v1', adapter: 'gateway-rest', validFrom: '2026-12-01' },
      { port: 'p.port.core-banking.v1', validFrom: 'soon' },
    ] };
    const problems = portBindingProblems(bad, isPort).join('\n');
    expect(problems).toMatch(/p\.port\.nothing\.v1, which is no port/);
    expect(problems).toMatch(/apiToken holds a value/);
    expect(problems).toMatch(/bound twice from 2026-12-01/);
    expect(problems).toMatch(/names no adapter/);
    expect(problems).toMatch(/soon is not a date/);
  });

  it('a bank configurator saves a draft, a designer may not; the bank approver approves it into a configuration release', async () => {
    const env = freshEnv();
    env.store.settings = { ...env.store.settings, distributions: true };
    await buildAndPublish(env, 'example.checker', 'example-bank', { kind: 'distribution', bank: 'example-bank', engine: 'temporal' });
    const start = (await as(env, 'example.maker', 'GET', '/api/tenants/example-bank/port-bindings')).body.start;
    // Switch core banking to the gateway from a later date: a new binding, the earlier one kept (scenario 2).
    const doc = { ...start, bindings: [...start.bindings, { port: 'p.port.core-banking.v1', adapter: 'gateway-rest', validFrom: '2027-01-01T00:00:00Z', settings: { baseUrl: 'https://core-banking.example.invalid/' }, secretRef: 'env:TH_EXAMPLE_CORE_BANKING_TOKEN' }] };
    // Checked without saving: a binding of a port no pack holds is refused.
    const wrong = await as(env, 'example.maker', 'POST', '/api/tenants/example-bank/port-bindings/check', { doc: { ...doc, bindings: [{ port: 'p.port.ledger.v1', adapter: 'x', validFrom: '2027-01-01' }] } });
    expect(wrong.body.issues.map((i: any) => i.code)).toContain('L48');
    // The table: a designer of the platform authors no bank's bindings; the bank's configurator does.
    expect((await as(env, 'workflow.designer', 'POST', '/api/drafts', { doc })).status).toBe(403);
    const saved = await as(env, 'example.maker', 'POST', '/api/drafts', { doc });
    expect(saved.status, JSON.stringify(saved.body)).toBe(200);
    expect(saved.body.issues.filter((i: any) => i.severity === 'error')).toEqual([]);
    const key = saved.body.draft.key;
    // The maker may not approve; the bank approver does, and the approval builds the configuration release.
    expect((await as(env, 'example.maker', 'POST', '/api/publish', { key })).status).toBe(403);
    const r: any = await gov.approveChangeSet(env, 'example-bank', 'example.checker', [key]);
    await r.write;
    const cfg = r.bundles.find((b: any) => b.kind === 'configuration');
    expect(cfg.releaseId).toBe(`config/example-bank@${env.store.configurationSet('example-bank').release}+bindings.1.0.0`);
    const files = fake().files(env.store.content!.repository, 'main');
    expect(files['content-configurations/banks/example-bank/port-bindings/1.0.0.yaml']).toBeDefined();
    const shipped = parseYaml(files[`content-configurations/${cfg.folder}/bindings/port-bindings.yaml`]);
    expect(shipped).toMatchObject({ kind: 'port-bindings', tenant: 'example-bank', release: '1.0.0' });
    expect(shipped.bindings.find((b: any) => b.validFrom === '2027-01-01T00:00:00Z')).toMatchObject({ adapter: 'gateway-rest', secretRef: 'env:TH_EXAMPLE_CORE_BANKING_TOKEN' });
    expect(parseYaml(files[`content-configurations/${cfg.folder}/manifest.yaml`])).toMatchObject({ bindings: shipped.bindings.length, portBindings: '1.0.0' });
    // The released bindings are what the editor starts from next, at the next minor; the checks hold over the branch.
    const next = (await as(env, 'example.maker', 'GET', '/api/tenants/example-bank/port-bindings')).body;
    expect(next.released.release).toBe('1.0.0');
    expect(next.start.release).toBe('1.1.0');
    expect(checkAll(env).filter((i) => i.code === 'L48')).toEqual([]);
  }, 240000);
});
