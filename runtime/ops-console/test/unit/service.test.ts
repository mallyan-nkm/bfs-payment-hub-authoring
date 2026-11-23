import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { PAGES, diffSummary, hashQuery, logQuery, runbookHref, stateTone, when } from '../../src/service/format';
import { DEV_USERS } from '../../src/shell/Shell';

// The service console's own logic (R-09-39 to R-09-44): its pages, the runbook link of every error it shows, the log
// filter, a difference's summary, the routes the assistant's proposals link to, and its synthetic dev users.

const here = path.dirname(fileURLToPath(import.meta.url));

describe('the service console', () => {
  it('has the seven pages of Part 9, section 9.12', () => {
    expect(PAGES.map((p) => p.id)).toEqual(['health', 'content', 'logs', 'errors', 'actions', 'backup', 'assistant']);
  });

  it('links an error to its runbook in the dictionary, whatever its code', () => {
    expect(runbookHref('AM02')).toBe('#/service/errors/AM02');
    expect(runbookHref('th:nonRetryable')).toBe('#/service/errors/th%3AnonRetryable');
    expect(runbookHref('signing.Tampered')).toBe('#/service/errors/signing.Tampered');
  });

  it('shows every error it renders through the runbook link (no code is shown bare)', () => {
    const src = fs.readFileSync(path.join(here, '..', '..', 'src', 'service', 'Service.tsx'), 'utf8');
    const errorBadges = src.match(/<a [^>]*data-testid="error-link"[^>]*>/g) ?? [];
    expect(errorBadges.length).toBeGreaterThanOrEqual(4);
    for (const m of errorBadges) expect(m).toMatch(/runbookHref/);
  });

  it('gives each state its tone', () => {
    expect(stateTone('active')).toBe('pass');
    expect(stateTone('draining')).toBe('warn');
    expect(stateTone('pending-approval')).toBe('warn');
    expect(stateTone('refused')).toBe('fail');
    expect(stateTone('drained')).toBe('');
  });

  it('sends only the filters that are filled', () => {
    expect(logQuery({ code: ' AM02 ', leg: '', scheme: undefined, tenant: 'example-bank' })).toEqual({ code: 'AM02', tenant: 'example-bank' });
  });

  it('summarises a difference against what is active', () => {
    expect(diffSummary({ flows: [{ change: 'changed' }], artefacts: [{ change: 'added' }, { change: 'changed' }], parameters: [] })).toBe('1 flow(s), 2 file(s), 0 parameter(s) differ: 1 added, 2 changed, 0 removed.');
    expect(diffSummary({ flows: [], artefacts: [], parameters: [] })).toBe('No difference against what is active.');
  });

  it('reads a proposal\'s route to the actions page', () => {
    expect(hashQuery('#/service/actions?action=release-hold&target=b-1')).toEqual({ action: 'release-hold', target: 'b-1' });
    expect(when('2026-11-22T08:00:00.123+00:00')).toBe('2026-11-22 08:00:00Z');
  });

  it('signs in synthetic service engineers only, in dev mode', () => {
    const svc = DEV_USERS.filter((u) => u.roles.some((r) => r.endsWith(':ServiceEngineer') || r.endsWith(':BankApprover')));
    expect(svc.map((u) => u.sub)).toEqual(['reg.approver', 'svc.engineer']);
    for (const u of svc) expect(u.name).toMatch(/^Example /);
  });
});
