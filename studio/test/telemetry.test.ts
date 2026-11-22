import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { InMemorySpanExporter } from '@opentelemetry/sdk-trace';
import { AggregationTemporality, InMemoryMetricExporter } from '@opentelemetry/sdk-metrics';
import { InMemoryLogRecordExporter } from '@opentelemetry/sdk-logs';
import { dispatch } from '../src/server/api.js';
import type { StudioEnv } from '../src/env.js';
import { inSpan, initTelemetry, log, shutdownTelemetry, flushTelemetry } from '../src/telemetry/telemetry.js';
import { isPersonalKey, MASKED, scrub } from '../src/telemetry/mask.js';
import { freshEnv } from './helpers.js';

// Monitoring of the studio (R-13-01 to R-13-05, I-66): the WPS salary file run end to end on the studio's
// interpreter emits one trace (the payment, each port call), the port-call metrics and logs carrying the trace id,
// and nothing an exporter receives holds a value of the file's party records. In-memory exporters, behind the masking.

const TENANT = 'example-bank';
const BENCH = [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
];
// What samples/wps-sif-valid.sif holds about its parties (synthetic): the employer, the employee, the account.
const PARTY_VALUES = ['0000001234567', '00001234567890', 'AE070331234567890123456'];

const spans = new InMemorySpanExporter();
const metrics = new InMemoryMetricExporter(AggregationTemporality.CUMULATIVE);
const logs = new InMemoryLogRecordExporter();
let env: StudioEnv;

beforeAll(() => {
  initTelemetry('transacthub-studio-test', { spanExporter: spans, metricExporter: metrics, logExporter: logs });
  env = freshEnv();
});
afterAll(() => shutdownTelemetry());

function exportedStrings(): string[] {
  const out: string[] = [];
  for (const s of spans.getFinishedSpans()) {
    out.push(s.name, s.status.message ?? '');
    for (const [k, v] of Object.entries(s.attributes)) out.push(`${k}=${v}`);
    for (const e of s.events) for (const [k, v] of Object.entries(e.attributes ?? {})) out.push(`${k}=${v}`);
  }
  for (const r of logs.getFinishedLogRecords()) {
    out.push(String(r.body ?? ''));
    for (const [k, v] of Object.entries(r.attributes)) out.push(`${k}=${v}`);
  }
  for (const rm of metrics.getMetrics()) for (const sm of rm.scopeMetrics) for (const m of sm.metrics) {
    out.push(m.descriptor.name);
    for (const p of m.dataPoints) for (const [k, v] of Object.entries(p.attributes)) out.push(`${k}=${v}`);
  }
  return out;
}

describe('telemetry of the studio', () => {
  it('masks personal keys, IBANs, long numbers and emails', () => {
    expect(scrub('AE070331234567890123456')).toBe('****3456');
    expect(scrub('employee 00001234567890 paid on 2026-11-17')).toBe('employee ****7890 paid on 2026-11-17');
    expect(scrub('mail someone@example.com')).toBe('mail [masked-email]');
    expect(scrub('p.port.core-banking.v1 step 12')).toBe('p.port.core-banking.v1 step 12');
    expect(isPersonalKey('creditor.name')).toBe(true);
    expect(isPersonalKey('debtorAccount')).toBe(true);
    expect(isPersonalKey('th.activity')).toBe(false);
  });

  it('runs the WPS salary file as one trace with its port calls, the port metrics and correlated logs, and no party value', async () => {
    let traceId = '';
    const r = await inSpan('th.payment', { 'th.tenant': TENANT }, async (span) => {
      traceId = span.spanContext().traceId;
      log('info', 'Salary file for {creditorName} to {th.note} received', { creditorName: 'Employee One', 'th.note': 'AE070331234567890123456' });
      return dispatch(env, { method: 'POST', path: '/api/bench/run', query: {}, body: { target: 'p.wf.corporate.v1', tenant: TENANT, inbound: 'samples/wps-sif-valid.sif', message: 'uae.wps.sif', bench: BENCH } });
    });
    expect(r.status, JSON.stringify(r.body).slice(0, 400)).toBe(200);
    expect(r.body.outcome).toBe('settled');
    await flushTelemetry();

    const mine = spans.getFinishedSpans().filter((s) => s.spanContext().traceId === traceId);
    const names = mine.map((s) => s.name);
    expect(names).toContain('th.payment');
    // The salary file went to the scheme over its port, and the posting to core banking over its own.
    expect(names).toContain('th.port.call uae.wps.port.wps.v1');
    expect(names).toContain('th.port.call p.port.core-banking.v1');
    const wps = mine.find((s) => s.name === 'th.port.call uae.wps.port.wps.v1')!;
    expect(wps.attributes['th.port']).toBe('uae.wps.port.wps.v1');
    expect(String(wps.attributes['th.activity'])).toMatch(/^uae\.wps\./);
    expect(wps.attributes['th.payment.id']).toBeDefined();

    const metricNames = metrics.getMetrics().flatMap((rm) => rm.scopeMetrics.flatMap((sm) => sm.metrics.map((m) => m.descriptor.name)));
    expect(metricNames).toContain('th.port.call.duration');

    const record = logs.getFinishedLogRecords().find((l) => String(l.body).startsWith('Salary file for'))!;
    expect(record.spanContext?.traceId).toBe(traceId);
    expect(record.body).toBe(`Salary file for ${MASKED} to ****3456 received`);

    const leaks = exportedStrings().filter((s) => PARTY_VALUES.some((v) => s.includes(v)) || s.includes('Employee One'));
    expect(leaks).toEqual([]);
  });
});
