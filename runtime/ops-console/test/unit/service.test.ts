import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { PAGES, RETIRE_NONE, diffSummary, feedKeyAction, hashQuery, keyMaySign, logQuery, rotationParameters, runbookHref, stateTone, stepError, when } from '../../src/service/format';
import { DEV_USERS } from '../../src/shell/Shell';

// The service console's own logic (R-09-39 to R-09-44): its pages, the runbook link of every error it shows, the log
// filter, a difference's summary, the routes the assistant's proposals link to, and its synthetic dev users.

const here = path.dirname(fileURLToPath(import.meta.url));

describe('the service console', () => {
  it('has the seven pages of Part 9, section 9.12, its signers and native modules, and its audit trail (R-05-76)', () => {
    expect(PAGES.map((p) => p.id)).toEqual(['health', 'content', 'logs', 'errors', 'actions', 'backup', 'trust', 'assistant', 'audit']);
  });

  it('shows the values in force, the pins with no payment and the native modules, each from its route (R-09-110, R-09-165, R-09-120)', () => {
    const src = fs.readFileSync(path.join(here, '..', '..', 'src', 'service', 'Service.tsx'), 'utf8');
    for (const t of ['data-testid="value-row"', 'data-testid="stuck-pin-row"', 'data-testid="signer-row"', 'data-testid="native-file-row"']) expect(src).toContain(t);
    expect(src).toMatch(/api\.act\('end-pin', /);
    expect(src).toMatch(/api\.act\('put-in-force', /);
    const api = fs.readFileSync(path.join(here, '..', '..', 'src', 'service', 'api.ts'), 'utf8');
    for (const r of ["'/content/stuck-pins'", '`/content/values', "'/trust'"]) expect(api).toContain(r);
  });


  it("says what each trusted key may sign, and asks a rotation for the new key's kinds and scope (R-09-249, Issue I-292)", () => {
    expect(keyMaySign({ everyKind: true, kinds: ['workflow-package', 'distribution', 'configuration'] })).toBe('every kind');
    expect(keyMaySign({ kinds: ['workflow-package'] })).toBe('workflow-package');
    expect(keyMaySign({ kinds: ['configuration'], scope: { feed: 'fx-rates' } })).toBe('configuration, of the fx-rates feed only');
    const pem = '-----BEGIN PUBLIC KEY-----\nMFkw\n-----END PUBLIC KEY-----';
    expect(rotationParameters('', ['configuration'], '')).toMatch(/PEM/);
    expect(rotationParameters(pem, [], '')).toMatch(/at least one kind/);
    expect(rotationParameters(pem, ['distribution', 'configuration'], 'fx-rates')).toMatch(/configuration releases only/);
    // Every kind is sent as none, so the key's entry names none; a narrower key names its kinds, in their order.
    expect(rotationParameters(pem, ['configuration', 'workflow-package', 'distribution'], '')).toEqual({ publicKey: pem });
    expect(rotationParameters(pem, ['distribution', 'workflow-package'], '')).toEqual({ publicKey: pem, kinds: ['workflow-package', 'distribution'] });
    expect(rotationParameters(pem, ['configuration'], ' fx-rates ')).toEqual({ publicKey: pem, kinds: ['configuration'], scope: { feed: 'fx-rates' } });
  });

  it("trusts the rates feed's own key without a PEM pasted, retiring the feed's key before it (R-09-249)", () => {
    expect(feedKeyAction({})).toEqual({ action: 'rotate-trusted-key', target: RETIRE_NONE, parameters: { feed: 'fx-rates' } });
    expect(feedKeyAction({ previous: 'k-old' })).toEqual({ action: 'rotate-trusted-key', target: 'k-old', parameters: { feed: 'fx-rates' } });
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

  it('names a step\'s error in the log, with whether it may be retried, when the dictionary has no entry for it (Issue I-350)', () => {
    // The log item's error, unmasked (its data is masked, and an error object's name with it).
    expect(stepError({ name: 'NativeHostUnreachable', retryable: true }, [])).toBe('NativeHostUnreachable, retryable');
    expect(stepError({ name: 'BusinessReject', retryable: false }, [])).toBe('BusinessReject, not retryable');
    expect(stepError({ name: 'E0042' }, [])).toBe('E0042');
    // An error the dictionary knows is shown by its runbook link instead.
    expect(stepError({ name: 'TransientError', retryable: true }, [{ code: 'TransientError' }])).toBe('');
    expect(stepError(null, [])).toBe('');
  });

  it('wraps a long probe name or value in its cell, so the page does not scroll sideways (video 5c)', () => {
    const src = fs.readFileSync(path.join(here, '..', '..', 'src', 'service', 'Service.tsx'), 'utf8');
    const css = fs.readFileSync(path.join(here, '..', '..', 'src', 'styles.css'), 'utf8');
    expect(css).toMatch(/table\.grid td\.wrap \{[^}]*white-space: normal/);
    expect(src).toContain('<TableCell className="mono wrap">{c.name}</TableCell>');
    expect(src).toMatch(/<TableCell className="mono wrap" title=\{JSON\.stringify\(v\.value\)\}>/);
  });

  it('gives each state its tone', () => {
    expect(stateTone('active')).toBe('pass');
    expect(stateTone('draining')).toBe('warn');
    expect(stateTone('pending-approval')).toBe('warn');
    expect(stateTone('refused')).toBe('fail');
    expect(stateTone('drained')).toBe('');
    expect(stateTone('in-force')).toBe('pass');
    expect(stateTone('in use')).toBe('pass');
    expect(stateTone('superseded')).toBe('');
    expect(stateTone('awaiting-consent')).toBe('warn');
    expect(stateTone('revoked')).toBe('fail');
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
