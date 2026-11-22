import { openEnv } from '../env.js';
import { connectionFromEnv, NOT_CONNECTED } from '../content/content-store.js';
import { AuthConfigError, authConfigFromEnv } from './auth.js';
import { createStudioServer } from './http.js';
import { initTelemetry, shutdownTelemetry } from '../telemetry/telemetry.js';
import { keyIdOf, primeSigningKey, signingKey } from '../export/signing.js';

// The studio server: sign-in, the API under /api and the built web app from web/dist. Its only store
// is the content branch the environment names (R-11-21): without TH_CONTENT_REPO it does not start.
// Without sign-in configured it does not start either (R-11-32): TH_OIDC_ISSUER and TH_OIDC_CLIENT_ID,
// or TH_AUTH_DEV=1 for local development, which is refused when NODE_ENV=production.

const port = Number(process.env.PORT ?? 4317);
if (!connectionFromEnv()) {
  console.error(`The studio does not start: TH_CONTENT_REPO is not set.\n${NOT_CONNECTED}`);
  process.exit(1);
}
let auth: ReturnType<typeof authConfigFromEnv>;
try {
  auth = authConfigFromEnv(process.env, port);
} catch (e: any) {
  console.error(`The studio does not start: ${e instanceof AuthConfigError ? e.message : e?.message ?? e}`);
  process.exit(1);
}
let env: Awaited<ReturnType<typeof openEnv>>;
try {
  env = await openEnv();
} catch (e: any) {
  console.error(`The studio does not start: ${e?.message ?? e}`);
  process.exit(1);
}
// Traces, metrics and logs over OTLP when OTEL_* names an endpoint (R-10-07); a request is a span, its route a dimension.
initTelemetry('transacthub-studio');
for (const sig of ['SIGTERM', 'SIGINT'] as const) process.once(sig, () => { void shutdownTelemetry().finally(() => process.exit(0)); });
// Every released package is signed (R-09-23). The studio starts without a key, but publishes no workflow release until it has one.
let signing: string;
try {
  await primeSigningKey();
  signing = `releases are signed with key ${keyIdOf(signingKey())}`;
} catch (e: any) {
  signing = `${e?.message ?? e}. Until then no workflow release is published or downloaded.`;
}

const server = createStudioServer(env, auth);
server.listen(port, () => {
  console.log(`TransactHub Designer Studio on http://localhost:${port}`);
  console.log(auth.dev ? `Sign-in: the development provider (TH_AUTH_DEV=1), at ${auth.issuer}. Not for production.` : `Sign-in: OpenID Connect, ${auth.issuer}, client ${auth.clientId}.`);
  const c = env.store.content!;
  console.log(`Content: ${c.repository} ${c.conn.branch} (${c.conn.root}/) at ${c.head.slice(0, 12)}, read into memory. ${env.store.all().length} documents, ${c.drafts.size} change set branch(es) in progress.`);
  console.log(`Signing: ${signing}`);
});
