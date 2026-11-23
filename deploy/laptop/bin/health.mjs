// A container's health check (deploy/laptop): TH_HEALTH_TCP=host:port connects, else TH_HEALTH_URL (default the host's
// /health/ready on 8080) answers 2xx. No curl in the images: Node is already there.
import * as net from 'node:net';

const done = (ok) => process.exit(ok ? 0 : 1);
setTimeout(() => done(false), 4000).unref();
if (process.env.TH_HEALTH_FILE) {
  // A service whose work is a file (the tokens service): healthy once the file is there and not empty.
  const fs = await import('node:fs');
  try { done(fs.statSync(process.env.TH_HEALTH_FILE).size > 0); } catch { done(false); }
} else if (process.env.TH_HEALTH_TCP) {
  const [host, port] = process.env.TH_HEALTH_TCP.split(':');
  const s = net.connect(Number(port), host, () => { s.end(); done(true); });
  s.on('error', () => done(false));
} else {
  fetch(process.env.TH_HEALTH_URL ?? 'http://127.0.0.1:8080/health/ready').then((r) => done(r.ok), () => done(false));
}
