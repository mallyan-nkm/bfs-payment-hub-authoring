import * as fs from 'node:fs';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { PartyService } from '../src/conformance/party.js';
import { freshEnv } from './helpers.js';

// The salary file's intake map takes the file's name from the File API (Issue I-195, R-05-85): the runtime's front door hands the
// map, on a worker of the payment's distribution, the inbound message with the name the File API received the file under beside
// its content (runtime/src/TransactHub.FileApi/FrontDoor.cs, `fileName`). A file a bank's channel drops on the SFTP server or posts
// to POST /files has no `# file:` line; release 1.4.0 maps it, release 1.3.0 does not.

const env = freshEnv();
const SAMPLE = fs.readFileSync(path.resolve(__dirname, '..', 'fixtures', 'samples', 'wps-sif-no-file-line.sif'), 'utf8');
const NAME = '0000001234567261117083000.sif';

/** The intake map's attempt as the front door asks a worker for it: the message rides beside the context's reference. */
async function intake(release: string, fileName?: string) {
  const party = new PartyService({ store: env.store, scripts: env.scripts, evaluationDate: '2026-11-23', tenant: 'example-bank', bench: [] } as any);
  const message = { id: 'in-F-1', type: 'uae.wps.sif', payload: SAMPLE, ...(fileName ? { fileName } : {}) };
  const ctx = { header: { conversation: 'conv-P-1', paymentId: 'P-1', scheme: 'uae.wps', direction: 'outward' }, messages: { inbound: { id: message.id, type: message.type } } };
  return party.attempt({ instance: 'intake-P-1', activity: 'uae.wps.map.sif-in.v1', release, step: 'intake', call: 1, attempt: 1, now: Date.parse('2026-11-23T08:30:00Z'), ctx, messages: [message] } as any);
}

describe('the salary file\'s name from the File API (Issue I-195, R-05-85)', () => {
  it('the sample has no file line', () => {
    expect(SAMPLE).not.toMatch(/^#\s*file:/m);
  });

  it('release 1.4.0 maps a file with no file line by the name the File API gives', async () => {
    const r: any = await intake('1.4.0', NAME);
    expect(r.kind).toBe('result');
    expect(r.outcome).toBe('mapped');
    expect(r.writes['header.fileId']).toBe('0000001234567261117083000');
    expect(r.writes['batch.id']).toBe('0000001234567261117083000');
  });

  it('takes the last segment of a name with a folder', async () => {
    const r: any = await intake('1.4.0', `upload/wps/inbound/${NAME}`);
    expect(r.outcome).toBe('mapped');
    expect(r.writes['header.fileId']).toBe('0000001234567261117083000');
  });

  it('refuses the file when the name does not start with the employer\'s id, as the edge rule says', async () => {
    const r: any = await intake('1.4.0', `9999991234567261117083000.sif`);
    expect(r.outcome).toBe('unmappable');
    expect(r.reasonCode).toBe('FF01');
  });

  it('release 1.5.0 writes the CIF the channel declared at upload as the debtor\'s further organisation id, scheme CIF (R-02-22, Issue I-153)', async () => {
    const party = new PartyService({ store: env.store, scripts: env.scripts, evaluationDate: '2026-11-23', tenant: 'example-bank', bench: [] } as any);
    let calls = 0;
    const run = async (release: string, payload: string) => {
      const message = { id: `in-F-2-${calls + 1}`, type: 'uae.wps.sif', payload, fileName: NAME };
      const ctx = { header: { conversation: 'conv-P-2', paymentId: 'P-2', scheme: 'uae.wps', direction: 'outward' }, messages: { inbound: { id: message.id, type: message.type } } };
      return (await party.attempt({ instance: `intake-P-2-${release}-${++calls}`, activity: 'uae.wps.map.sif-in.v1', release, step: 'intake', call: 1, attempt: 1, now: Date.parse('2026-11-23T08:30:00Z'), ctx, messages: [message] } as any)) as any;
    };
    const r = await run('1.5.0', `# cif: CIF-0000123\n${SAMPLE}`);
    expect(r.outcome).toBe('mapped');
    expect(r.writes['batch.debtor.otherOrganisationIds']).toEqual([{ id: 'CIF-0000123', scheme: 'CIF' }]);
    expect([r.writes['batch.debtor.organisationId.id'], r.writes['batch.debtor.organisationId.scheme']]).toEqual(['0000001234567', 'MOL']);
    // No CIF declared: none written. And 1.4.0, pinned by a payment in flight, writes none.
    expect(JSON.stringify((await run('1.5.0', SAMPLE)).writes)).not.toContain('otherOrganisationIds');
    expect(JSON.stringify((await run('1.4.0', `# cif: CIF-0000123\n${SAMPLE}`)).writes)).not.toContain('otherOrganisationIds');
  });

  it('release 1.3.0, pinned by a payment in flight, still reads only the file line', async () => {
    const r: any = await intake('1.3.0', NAME);
    expect(r.outcome).toBe('unmappable');
  });
});
