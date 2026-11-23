'use strict';
// A stand-in for a script that has broken out of its context (R-08-20, Issue I-119): it runs as the TypeScript script worker
// runs, with the same command, flags and environment, and tries what such a script would. It reports what it reached on stdout.
const out = {};
const attempt = (name, f) => {
  try {
    out[name] = { reached: true, value: f() };
  } catch (e) {
    out[name] = { reached: false, code: e && e.code };
  }
};
const fs = require('node:fs');
const [studioFile, ppid, port] = process.argv.slice(2);
out.env = Object.keys(process.env);
attempt('readStudioFile', () => fs.readFileSync(studioFile, 'utf8').length);
attempt('readParentEnviron', () => fs.readFileSync(`/proc/${ppid}/environ`, 'utf8').length);
attempt('writeFile', () => fs.writeFileSync(require('node:path').join(require('node:os').tmpdir(), 'th-escape-probe.txt'), 'x'));
attempt('childProcess', () => require('node:child_process').spawnSync('sh', ['-c', 'true']).status);
attempt('workerThread', () => new (require('node:worker_threads').Worker)('0', { eval: true }) && 'started');
const sock = require('node:net').connect(Number(port), '127.0.0.1');
let done = false;
const finish = (connected, code) => {
  if (done) return;
  done = true;
  out.network = { reached: connected, code };
  sock.destroy();
  process.stdout.write(JSON.stringify(out) + '\n');
  process.exit(0);
};
sock.on('connect', () => finish(true));
sock.on('error', (e) => finish(false, e.code));
setTimeout(() => finish(false, 'timeout'), 3000);
