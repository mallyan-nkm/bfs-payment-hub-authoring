import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { auditQuery, auditRow, chainText, newestFirst, shortHash } from '../../src/shell/audit';
import { messageChoices, uploadAnswer } from '../../src/ops/upload';

// The upload page (R-05-73, R-05-74, Issue I-201) and the consoles' audit pages (R-05-75, R-05-76, Issue I-202): what an answer
// says, the messages offered, the audit filter, how a fact is shown and what the page says of the chain. And the laptop's
// register maker (I-203): a synthetic user whose group the operations role map makes a RegisterMaker.

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..', '..', '..');

describe('the upload page', () => {
  it('says whether the file was started, kept or refused, with the runbook of the code and the payment it started', () => {
    const started = uploadAnswer({ outcome: 'started', status: 'accepted', file: 'f-1', payment: 'pay-1', distribution: 'dist/example-bank@2026.11.23-1' }, 200);
    expect(started).toMatchObject({ outcome: 'started', payment: 'pay-1', text: 'Kept as f-1 and its payment started on dist/example-bank@2026.11.23-1.' });
    expect(uploadAnswer({ status: 'accepted', file: 'f-2', detail: 'kept; no front door is configured' }, 200)).toMatchObject({ outcome: 'kept', text: 'Kept as f-2: kept; no front door is configured' });
    const refused = uploadAnswer({ outcome: 'refused', status: 'refused', file: 'f-3', code: 'file.Duplicate', error: 'The same file was received before', runbook: 'http://service.test/#/service/errors/file.Duplicate' }, 409);
    expect(refused).toMatchObject({ outcome: 'refused', code: 'file.Duplicate', runbook: 'http://service.test/#/service/errors/file.Duplicate', text: 'Refused (kept as f-3): The same file was received before' });
    // A refusal the console gives itself, with no outcome: refused by its status, linked to the runbook in the same app.
    expect(uploadAnswer({ error: 'No distribution is active', code: 'frontdoor.NoActiveDistribution' }, 409)).toMatchObject({ outcome: 'refused', runbook: '#/service/errors/frontdoor.NoActiveDistribution' });
  });

  it('offers the messages that start a payment first, then those delivered to a running one', () => {
    const t = { tenant: 'example-bank', environment: 'test', distribution: 'd', messages: [{ id: 'z.ack', title: 'Ack', scheme: 'z', kind: 'deliver' as const }, { id: 'z.file', title: 'File', scheme: 'z', kind: 'start' as const }] };
    expect(messageChoices(t).map((m) => m.id)).toEqual(['z.file', 'z.ack']);
    expect(messageChoices(undefined)).toEqual([]);
  });

  it('shows every refusal code through its runbook link', () => {
    const src = fs.readFileSync(path.join(here, '..', '..', 'src', 'ops', 'Upload.tsx'), 'utf8');
    expect(src).toMatch(/answer\.code && <a href=\{answer\.runbook\} data-testid="error-link">/);
  });
});

describe('the audit pages', () => {
  it('sends only the filters that are filled', () => {
    expect(auditQuery({ person: ' op-1 ', action: '', from: '2026-11-23', to: undefined })).toEqual({ person: 'op-1', from: '2026-11-23' });
  });

  it('shows a fact with who, what, why and its hash, never an action id as the action', () => {
    const r = auditRow({ subject: 'operations-console', seq: 3, kind: 'consent', code: 'requested', at: '2026-11-23T08:00:00+00:00', actor: 'operator:sup-1', hash: 'a'.repeat(64), prevHash: 'b'.repeat(64), data: { action: '0123456789abcdef0123456789abcdef', name: 'rebuild-read-model', by: 'Example supervisor', why: 'after a restore' } });
    expect(r).toMatchObject({ by: 'Example supervisor', action: 'rebuild-read-model', why: 'after a restore', hash: 'a'.repeat(64) });
    expect(auditRow({ seq: 1, kind: 'service.action', code: 'requested', at: 'x', actor: 'a', data: { action: '0123456789abcdef0123456789abcdef' } }).action).toBeUndefined();
    expect(auditRow({ seq: 1, kind: 'service.action', code: 'done', at: 'x', actor: 'a', data: { action: 'release-hold' } }).action).toBe('release-hold');
    expect(shortHash('0123456789abcdef')).toBe('0123456789ab…');
  });

  it('puts the newest first', () => {
    const rows = [1, 3, 2].map((seq) => auditRow({ seq, at: `2026-11-23T08:00:0${seq}Z`, kind: 'k', actor: 'a', hash: 'h' }));
    expect(newestFirst(rows).map((r) => r.seq)).toEqual([3, 2, 1]);
  });

  it('says whether the chain verifies, and which chain does not', () => {
    expect(chainText({ ok: true, facts: 3 })).toBe('The chain verifies (3 facts)');
    expect(chainText({ ok: false, problem: 'fact 2 was changed after it was written' })).toBe('Does not verify: fact 2 was changed after it was written');
    expect(chainText({ ok: false, chains: [{ subject: 'service-console', ok: true, facts: 4 }, { subject: 'rel-1', ok: false, facts: 2, problem: 'fact 2 does not link to the one before it' }] })).toBe('Does not verify: rel-1: fact 2 does not link to the one before it');
    expect(chainText(null)).toBe('Not checked');
  });
});

describe("the laptop's register maker (I-203)", () => {
  it('is a synthetic user whose group the operations role map makes a RegisterMaker, and no other role', () => {
    const dir = JSON.parse(fs.readFileSync(path.join(repo, 'deploy', 'laptop', 'identity', 'directory.json'), 'utf8'));
    const map = JSON.parse(fs.readFileSync(path.join(repo, 'deploy', 'laptop', 'identity', 'role-map-ops.json'), 'utf8'));
    const maker = dir.users.find((u: any) => u.sub === 'reg.maker');
    expect(maker).toMatchObject({ name: 'Example register maker', groups: ['th-register-makers'] });
    const roles = map.grants.filter((g: any) => maker.groups.includes(g.value)).flatMap((g: any) => g.roles);
    expect(roles).toEqual(['RegisterMaker']);
    // The bank approver who consents is another person.
    const approver = dir.users.find((u: any) => map.grants.some((g: any) => u.groups.includes(g.value) && g.roles.includes('BankApprover')));
    expect(approver.sub).not.toBe('reg.maker');
  });
});
