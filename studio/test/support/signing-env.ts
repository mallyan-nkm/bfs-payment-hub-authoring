import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { generateSigningKey, trustedSignersJson } from '../../src/export/signing.js';

// Every released package is signed (R-09-23) and only verified packages are deployed (R-09-26). A test run makes its
// own P-256 key, in a folder of its own, removed when the run ends: no key is ever committed. Every process a test
// starts (the studio server, the CLI, a deploy, the runtime) inherits TH_SIGNING_KEY and TH_TRUSTED_SIGNERS.

export interface TestSigning {
  dir: string;
  keyId: string;
  keyFile: string;
  trustFile: string;
  publicKeyPem: string;
}

let made: TestSigning | undefined;

export function testSigning(): TestSigning {
  if (made) return made;
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-test-signing-'));
  const k = generateSigningKey();
  const keyFile = path.join(dir, 'signing-key.pem');
  const trustFile = path.join(dir, 'trusted-signers.json');
  fs.writeFileSync(keyFile, k.privateKeyPem, { mode: 0o600 });
  fs.writeFileSync(trustFile, trustedSignersJson([k]));
  made = { dir, keyId: k.keyId, keyFile, trustFile, publicKeyPem: k.publicKeyPem };
  process.on('exit', () => fs.rmSync(dir, { recursive: true, force: true }));
  return made;
}

if (!process.env.TH_SIGNING_KEY) {
  const s = testSigning();
  process.env.TH_SIGNING_KEY = `file:${s.keyFile}`;
  process.env.TH_TRUSTED_SIGNERS = `file:${s.trustFile}`;
}
