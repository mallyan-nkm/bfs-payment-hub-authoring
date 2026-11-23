// The laptop's machine tokens (deploy/laptop, R-09-105): a host that calls the File API with a bearer token by reference (the native
// host's outbox, TH_FILE_API_TOKEN_REF; the consoles' resend, Operations:FileApi:TokenRef and Service:FileApi:TokenRef) reads it
// from a file in the `auth-files` volume, `file:/var/lib/transacthub/tokens/<client>.jwt`, which this service fills with a fresh
// client-credentials token from the bank's development identity provider every TH_TOKENS_EVERY seconds (default 600), well within a
// token's hour. A host's secret resolver reads the file again after its cache (TH_SECRET_CACHE_SECONDS). A bank's installation
// gives its hosts tokens from its own identity provider instead. The client secrets come from the environment only.
//
//   TH_MACHINE_CLIENTS='{"th-front-door":"env:TH_LAPTOP_FRONTDOOR_SECRET","th-operations":"env:TH_LAPTOP_OPERATIONS_SECRET"}'
import * as fs from 'node:fs';
import * as path from 'node:path';

const dir = process.env.TH_TOKENS_DIR ?? '/var/lib/transacthub/tokens';
const url = process.env.TH_IDP_TOKEN_URL ?? 'http://bank-idp:8080/token';
const every = Number(process.env.TH_TOKENS_EVERY ?? '600') * 1000;
const clients = JSON.parse(process.env.TH_MACHINE_CLIENTS ?? '{}');
const secret = (ref) => {
  if (!String(ref).startsWith('env:')) throw new Error(`${ref}: only env: references here`);
  const v = process.env[String(ref).slice(4)];
  if (!v) throw new Error(`${ref} is not set`);
  return v;
};
fs.mkdirSync(dir, { recursive: true });

async function once() {
  let ok = 0;
  for (const [client, ref] of Object.entries(clients)) {
    try {
      const r = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams({ grant_type: 'client_credentials', client_id: client, client_secret: secret(ref) }) });
      if (!r.ok) throw new Error(`${r.status}`);
      const t = (await r.json()).access_token;
      const f = path.join(dir, `${client}.jwt`);
      fs.writeFileSync(`${f}.part`, t, { mode: 0o640 });
      fs.renameSync(`${f}.part`, f);
      ok++;
    } catch (e) {
      console.error(`tokens: ${client}: ${e?.message ?? e}`);
    }
  }
  fs.writeFileSync(path.join(dir, '.ready'), `${ok}/${Object.keys(clients).length} ${new Date().toISOString()}\n`);
}

for (const s of ['SIGTERM', 'SIGINT']) process.once(s, () => process.exit(0));
for (;;) {
  await once();
  await new Promise((r) => setTimeout(r, every));
}
