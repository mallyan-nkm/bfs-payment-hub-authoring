import * as readline from 'node:readline';
import { openReadEnv } from '../env.js';
import { build, loadKit } from './kit.js';
import { PartyService } from './party.js';

// The party service over JSON lines on stdin and stdout, for an engine harness in another
// language (DTFx in C#, Flowable in Java). One request per line, one response per line:
//   {"op":"open","scenario":"CK-RETRY-01"}                  -> {"ok":true,"built":{...}}
//   {"op":"start","now":<ms>}                               -> {"deliveries":[...]}
//   {"op":"attempt", ...AttemptRequest}                     -> AttemptResponse plus "deliveries"
//   {"op":"sent"}                                           -> {"messages":[...]}

// The content: the connected branch, or an in-memory fake seeded from content-configurations/ (R-11-21).
const { env } = await openReadEnv();
const kit = loadKit(env);
let party: PartyService | undefined;
const rl = readline.createInterface({ input: process.stdin });
const write = (o: any) => process.stdout.write(JSON.stringify(o) + '\n');

rl.on('line', async (line) => {
  try {
    const req = JSON.parse(line);
    if (req.op === 'open') {
      const sc = kit.scenarios.scenarios.find((s: any) => s.id === req.scenario);
      const exp = kit.manifest.scenarios.find((s: any) => s.id === req.scenario);
      if (!sc) return write({ error: `No scenario ${req.scenario}` });
      const b = build(env, kit, sc, exp);
      party = PartyService.forScenario(env, b);
      return write({ ok: true, built: b });
    }
    if (!party) return write({ error: 'open a scenario first' });
    if (req.op === 'start') return write({ deliveries: party.start(req.now, req.conversation) });
    if (req.op === 'attempt') {
      const r = await party.attempt(req);
      return write({ ...r, deliveries: party.takeDeliveries() });
    }
    if (req.op === 'sent') return write({ messages: party.sent() });
    write({ error: `Unknown op ${req.op}` });
  } catch (e: any) {
    write({ error: String(e?.message ?? e) });
  }
});
