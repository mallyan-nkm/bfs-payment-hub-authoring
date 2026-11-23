// The runtime image's one entry point (deploy/laptop, R-09-80): `<host> [args...]` starts that host of the runtime in the
// foreground, its signals passed on, so each compose service is one host. Nothing here holds a secret: every host reads its
// settings from its environment, and its secrets by reference (env:, file:).
import { spawn } from 'node:child_process';

const R = '/opt/th/runtime';
const dll = (p, name) => `${R}/${p}/bin/Release/net10.0/${name}.dll`;
const hosts = {
  api: () => ['dotnet', [dll('src/TransactHub.Api', 'TransactHub.Api')]],
  service: () => ['dotnet', [dll('src/TransactHub.Service.Api', 'TransactHub.Service.Api')]],
  ops: () => ['dotnet', [dll('src/TransactHub.Operations.Api', 'TransactHub.Operations.Api')]],
  native: () => ['dotnet', [dll('src/TransactHub.Native', 'TransactHub.Native')]],
  testbench: () => ['dotnet', [dll('tools/TransactHub.TestBench', 'testbench'), 'serve', '--urls', process.env.ASPNETCORE_URLS ?? 'http://0.0.0.0:8080']],
  // Temporal is the default engine (R-09-79); TH_ENGINE names DTFx, Elsa or Flowable for a supervisor of its own.
  enginehost: () => ['dotnet', [dll('tools/TransactHub.EngineHost', 'TransactHub.EngineHost'), 'supervise', '--engine', process.env.TH_ENGINE ?? 'temporal',
    '--environment', process.env.TH_ENVIRONMENT ?? 'test', '--plans', process.env.TH_HOST_PLANS ?? '/var/lib/transacthub/work/plans', '--urls', process.env.TH_HOST_URLS ?? 'http://127.0.0.1:7600/']],
  idp: () => ['dotnet', [dll('tools/TransactHub.DevIdentity', 'TransactHub.DevIdentity'), 'serve']],
  keys: () => ['dotnet', [dll('tools/TransactHub.DevIdentity', 'TransactHub.DevIdentity'), 'keys', '--out', process.env.TH_KEYS_DIR ?? '/var/lib/transacthub/keys']],
  migrate: () => ['dotnet', [dll('tools/TransactHub.Migrate', 'TransactHub.Migrate')]],
  // A payment at the front door, run from the distribution it is pinned to (the laptop's insert-only test, deploy/laptop/test).
  'run-pinned': () => ['/opt/th/studio/node_modules/.bin/tsx', ['/opt/th/deploy/laptop/bin/run-pinned.mts']],
};

const [name, ...rest] = process.argv.slice(2);
if (!hosts[name]) {
  console.error(`usage: <${Object.keys(hosts).join('|')}> [args...]`);
  process.exit(2);
}
const [cmd, args] = hosts[name]();
if (name === 'enginehost') {
  const fs = await import('node:fs');
  fs.mkdirSync(process.env.TH_HOST_PLANS ?? '/var/lib/transacthub/work/plans', { recursive: true });
}
const child = spawn(cmd, [...args, ...rest], { stdio: 'inherit', env: process.env, cwd: name === 'run-pinned' ? '/opt/th/studio' : '/opt/th' });
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => child.kill(s));
child.on('exit', (code, signal) => process.exit(code ?? (signal ? 1 : 0)));
