import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { LOCAL_REPO, startLocalContent } from './local.js';
import { generateSigningKey, trustedSignersJson } from '../export/signing.js';

// `npm run studio:dev`: the studio against an in-memory fake GitHub seeded from this repository's
// content-configurations/. Everything the studio writes stays in that fake, in memory, and is gone
// when the process ends: still no local copy (R-11-24). `TH_FAKE_GITHUB_PORT` fixes its port.

// TH_FAKE_GITHUB_HOST: the interface it listens on (loopback by default; a laptop stack's runtime reads it from another container).
const local = await startLocalContent({ port: process.env.TH_FAKE_GITHUB_PORT ? Number(process.env.TH_FAKE_GITHUB_PORT) : undefined, host: process.env.TH_FAKE_GITHUB_HOST || undefined });
process.env.TH_GITHUB_API = local.url;
process.env.TH_CONTENT_REPO = `${LOCAL_REPO.owner}/${LOCAL_REPO.repo}`;
process.env.TH_CONTENT_BRANCH = LOCAL_REPO.branch;
process.env.TH_GITHUB_TOKEN = local.token;
// Local development signs in with the built-in development provider unless an identity provider is named (R-11-32).
if (!process.env.TH_OIDC_ISSUER && process.env.TH_AUTH_DEV === undefined) process.env.TH_AUTH_DEV = '1';
console.log(`Fake GitHub on ${local.url}: ${process.env.TH_CONTENT_REPO} ${LOCAL_REPO.branch}, seeded from content-configurations/, in memory only.`);
// Releases are signed (R-09-23). Without a key of its own, the dev studio makes one for this run: the private key stays in
// this process's memory; only the public key is written, as the trusted signers a local deploy checks against.
if (!process.env.TH_SIGNING_KEY) {
  const k = generateSigningKey();
  process.env.TH_DEV_SIGNING_KEY_PEM = k.privateKeyPem;
  process.env.TH_SIGNING_KEY = 'env:TH_DEV_SIGNING_KEY_PEM';
  if (!process.env.TH_TRUSTED_SIGNERS) {
    const f = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'th-dev-trust-')), 'trusted-signers.json');
    fs.writeFileSync(f, trustedSignersJson([k]));
    process.env.TH_TRUSTED_SIGNERS = `file:${f}`;
    console.log(`Signing key ${k.keyId} made for this run; its trusted signers: ${f}`);
  }
}
await import('../server/main.js');
