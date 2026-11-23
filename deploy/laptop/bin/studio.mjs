// The studio image's entry point (deploy/laptop, R-09-80). With TH_CONTENT_REPO (and TH_CONTENT_BRANCH, TH_GITHUB_TOKEN from
// the environment only) the studio works on that real content branch; without it, on its in-memory fake GitHub seeded from the
// image's content-configurations/ (npm run studio:dev), which the runtime's package source reads on TH_FAKE_GITHUB_PORT.
import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';

const studio = '/opt/th/studio';
const entry = process.env.TH_CONTENT_REPO ? 'src/server/main.ts' : 'src/content/dev.ts';
// The native modules built in the image's build stage are signed by the key that build made (R-09-127); the image trusts that key
// unless the installation names its own module signers.
const signers = '/opt/th/native-module-signers.json';
if (!process.env.TH_NATIVE_MODULE_SIGNERS && existsSync(signers)) process.env.TH_NATIVE_MODULE_SIGNERS = readFileSync(signers, 'utf8');
const child = spawn(`${studio}/node_modules/.bin/tsx`, [entry], { cwd: studio, stdio: 'inherit', env: process.env });
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => child.kill(s));
child.on('exit', (code, signal) => process.exit(code ?? (signal ? 1 : 0)));
