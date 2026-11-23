// The studio image's entry point (deploy/laptop, R-09-77). With TH_CONTENT_REPO (and TH_CONTENT_BRANCH, TH_GITHUB_TOKEN from
// the environment only) the studio works on that real content branch; without it, on its in-memory fake GitHub seeded from the
// image's content-configurations/ (npm run studio:dev), which the runtime's package source reads on TH_FAKE_GITHUB_PORT.
import { spawn } from 'node:child_process';

const studio = '/opt/th/studio';
const entry = process.env.TH_CONTENT_REPO ? 'src/server/main.ts' : 'src/content/dev.ts';
const child = spawn(`${studio}/node_modules/.bin/tsx`, [entry], { cwd: studio, stdio: 'inherit', env: process.env });
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => child.kill(s));
child.on('exit', (code, signal) => process.exit(code ?? (signal ? 1 : 0)));
