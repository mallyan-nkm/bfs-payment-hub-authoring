import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { authorizeUrl, base64url, pkceChallenge, pkceVerifier } from '../../src/shell/auth';
import { columnsFor, filtersFor, grouped, show, tone } from '../../src/ops/format';
import type { View } from '../../src/ops/api';

// The console's own logic: PKCE (RFC 7636), and rendering from a view's declaration alone.

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..', '..', '..');

describe('the sign-in (R-06-22)', () => {
  it('makes the S256 challenge of RFC 7636, appendix B', async () => {
    expect(await pkceChallenge('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk')).toBe('E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');
    expect(base64url(new Uint8Array([251, 255, 0]))).toBe('-_8A');
    expect(pkceVerifier((a) => a.fill(1))).toHaveLength(43);
  });

  it('asks for the authorisation code with PKCE', () => {
    const u = new URL(authorizeUrl('https://idp.example.invalid/auth', { clientId: 'th-ops-console', redirectUri: 'https://ops.example.invalid/', scope: 'openid profile', state: 's', challenge: 'c' }));
    expect(Object.fromEntries(u.searchParams)).toEqual({ response_type: 'code', client_id: 'th-ops-console', redirect_uri: 'https://ops.example.invalid/', scope: 'openid profile', state: 's', code_challenge: 'c', code_challenge_method: 'S256' });
  });
});

describe('rendering from the declaration (R-05-48)', () => {
  const view: View = {
    kind: 'operations-view', name: 'zz.demo.view.operations.v1', release: '1.0.0', title: 'Demo', scheme: 'zz.demo',
    subject: {
      groups: [{ id: 'a', title: 'A' }],
      fields: [{ id: 'one', label: 'One', group: 'a', source: 'bulk.messageId' }, { id: 'two', label: 'Two', source: 'subject.amount', format: 'amount' }],
      columns: ['one'], filters: [{ field: 'one', match: 'contains' }],
    },
    legs: [{ leg: 'zz.demo.wf.out.v1', fields: [{ id: 'ack', label: 'Ack', source: 'step:send.outcome', format: 'status' }], columns: ['ack'], filters: [{ field: 'ack' }] }],
  };

  it('takes a new scheme\'s columns, filters and groups from its view, with no code for it', () => {
    expect(columnsFor([view], 'zz.demo').map((c) => `${c.from}:${c.key}`)).toEqual(['subject:one', 'zz.demo.wf.out.v1:ack']);
    expect(filtersFor([view], 'zz.demo').map((f) => `${f.key}/${f.match}`)).toEqual(['zz.demo.view.operations.v1:subject:one/contains', 'zz.demo.view.operations.v1:zz.demo.wf.out.v1:ack/exact']);
    expect(grouped(view.subject!.fields, view.subject!.groups).map((g) => `${g.title}:${g.fields.map((f) => f.id)}`)).toEqual(['A:one', 'Other:two']);
  });

  it('formats by the declared format only', () => {
    expect(show(1500, 'amount')).toBe('1,500.00');
    expect(show(null)).toBe('—');
    expect(show('2026-11-17T06:00:00.000Z', 'datetime')).toBe('2026-11-17 06:00:00Z');
    expect(tone('nak')).toBe('fail');
    expect(tone('ack')).toBe('pass');
  });

  it('names no scheme in its code', () => {
    const src = path.join(here, '..', '..', 'src');
    const files = fs.readdirSync(src, { recursive: true }).map(String).filter((f) => /\.(ts|tsx)$/.test(f));
    for (const f of files) expect(fs.readFileSync(path.join(src, f), 'utf8'), f).not.toMatch(/uae\.wps|\bWPS\b|\bSIF\b|salary/i);
  });

  it('reads the factory views as published', () => {
    const wps = fs.readFileSync(path.join(repo, 'content-configurations', 'artefacts', 'uae.wps', 'uae.wps.view.operations.v1', '1.0.0.yaml'), 'utf8');
    expect(wps).toContain('kind: operations-view');
  });
});
