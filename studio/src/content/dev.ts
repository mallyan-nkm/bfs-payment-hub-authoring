import { LOCAL_REPO, startLocalContent } from './local.js';

// `npm run studio:dev`: the studio against an in-memory fake GitHub seeded from this repository's
// content-configurations/. Everything the studio writes stays in that fake, in memory, and is gone
// when the process ends: still no local copy (R-11-24). `TH_FAKE_GITHUB_PORT` fixes its port.

const local = await startLocalContent({ port: process.env.TH_FAKE_GITHUB_PORT ? Number(process.env.TH_FAKE_GITHUB_PORT) : undefined });
process.env.TH_GITHUB_API = local.url;
process.env.TH_CONTENT_REPO = `${LOCAL_REPO.owner}/${LOCAL_REPO.repo}`;
process.env.TH_CONTENT_BRANCH = LOCAL_REPO.branch;
process.env.TH_GITHUB_TOKEN = local.token;
console.log(`Fake GitHub on ${local.url}: ${process.env.TH_CONTENT_REPO} ${LOCAL_REPO.branch}, seeded from content-configurations/, in memory only.`);
await import('../server/main.js');
