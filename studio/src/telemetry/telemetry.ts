import { context, metrics, propagation, trace, SpanKind, SpanStatusCode, type Attributes, type Context, type Span } from '@opentelemetry/api';
import { logs, SeverityNumber } from '@opentelemetry/api-logs';
import { AsyncLocalStorageContextManager } from '@opentelemetry/context-async-hooks';
import { W3CTraceContextPropagator } from '@opentelemetry/core';
import { OTLPLogExporter } from '@opentelemetry/exporter-logs-otlp-http';
import { OTLPMetricExporter } from '@opentelemetry/exporter-metrics-otlp-http';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { defaultResource, detectResources, envDetector, resourceFromAttributes } from '@opentelemetry/resources';
import { BatchLogRecordProcessor, LoggerProvider, SimpleLogRecordProcessor, type LogRecordExporter, type ReadableLogRecord } from '@opentelemetry/sdk-logs';
import { MeterProvider, PeriodicExportingMetricReader, type PushMetricExporter } from '@opentelemetry/sdk-metrics';
import { BatchSpanProcessor, SimpleSpanProcessor, TracerProvider, type ReadableSpan, type SpanExporter } from '@opentelemetry/sdk-trace';
import { maskAttributes, maskValue, scrub } from './mask.js';

// OpenTelemetry for the studio's processes (R-10-07, I-66): the studio server, the deploy runner and the deployed
// activity workers. Traces, metrics and logs go over OTLP to the endpoint the standard variables name
// (OTEL_EXPORTER_OTLP_ENDPOINT and the per-signal ones, OTEL_SERVICE_NAME, OTEL_RESOURCE_ATTRIBUTES,
// OTEL_SDK_DISABLED); with none set nothing is installed and the API's no-op is all that runs. Every span, log
// record and metric attribute passes the masking rules first (R-10-08). A collector that is down never stops the work.

const NAME = 'transacthub-studio';
let installed: { flush(): Promise<void>; shutdown(): Promise<void> } | undefined;

export function exportConfigured(env = process.env): boolean {
  if ((env.OTEL_SDK_DISABLED ?? '').toLowerCase() === 'true') return false;
  return ['OTEL_EXPORTER_OTLP_ENDPOINT', 'OTEL_EXPORTER_OTLP_TRACES_ENDPOINT', 'OTEL_EXPORTER_OTLP_METRICS_ENDPOINT', 'OTEL_EXPORTER_OTLP_LOGS_ENDPOINT'].some((k) => !!env[k]);
}

/** The OTEL_* variables a child process needs to export as this one does, and the trace it continues. */
export function childEnv(): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(process.env)) if (k.startsWith('OTEL_') && k !== 'OTEL_SERVICE_NAME' && v !== undefined) out[k] = v;
  const carrier: Record<string, string> = {};
  propagation.inject(context.active(), carrier);
  if (carrier.traceparent) out.TRACEPARENT = carrier.traceparent;
  if (carrier.tracestate) out.TRACESTATE = carrier.tracestate;
  return out;
}

function maskSpan(s: ReadableSpan): ReadableSpan {
  const attributes = maskAttributes(s.attributes);
  const events = s.events.map((e) => ({ ...e, attributes: maskAttributes(e.attributes) }));
  const status = s.status.message ? { ...s.status, message: scrub(s.status.message) } : s.status;
  const name = scrub(s.name);
  return new Proxy(s, {
    get(t, k, r) {
      if (k === 'attributes') return attributes;
      if (k === 'events') return events;
      if (k === 'status') return status;
      if (k === 'name') return name;
      const v = Reflect.get(t, k, r);
      return typeof v === 'function' ? v.bind(t) : v;
    },
  });
}

class MaskingSpanExporter implements SpanExporter {
  constructor(private inner: SpanExporter) {}
  export(spans: ReadableSpan[], done: (r: any) => void) { this.inner.export(spans.map(maskSpan), done); }
  shutdown() { return this.inner.shutdown(); }
  forceFlush() { return this.inner.forceFlush?.() ?? Promise.resolve(); }
}

function maskLog(r: ReadableLogRecord): ReadableLogRecord {
  const attributes = maskAttributes(r.attributes as any);
  const body = typeof r.body === 'string' ? scrub(r.body) : r.body;
  return new Proxy(r, {
    get(t, k, rr) {
      if (k === 'attributes') return attributes;
      if (k === 'body') return body;
      const v = Reflect.get(t, k, rr);
      return typeof v === 'function' ? v.bind(t) : v;
    },
  });
}

class MaskingLogExporter implements LogRecordExporter {
  constructor(private inner: LogRecordExporter) {}
  export(records: ReadableLogRecord[], done: (r: any) => void) { this.inner.export(records.map(maskLog), done); }
  shutdown() { return this.inner.shutdown(); }
  forceFlush() { return (this.inner as any).forceFlush?.() ?? Promise.resolve(); }
}

export interface TelemetryHooks {
  spanExporter?: SpanExporter;
  metricExporter?: PushMetricExporter;
  logExporter?: LogRecordExporter;
}

/**
 * Install OpenTelemetry for this process, once. Over OTLP when exportConfigured(); a test passes in-memory exporters
 * (behind the masking wrappers, so it sees what an exporter would). Returns the handle to flush and shut down.
 */
export function initTelemetry(service: string, hooks: TelemetryHooks = {}) {
  if (installed) return installed;
  const exporting = exportConfigured();
  if (!exporting && !hooks.spanExporter && !hooks.metricExporter && !hooks.logExporter) return undefined;
  const resource = defaultResource()
    .merge(resourceFromAttributes({ 'service.name': service, 'service.version': '1.0.0', 'th.host': service }))
    .merge(detectResources({ detectors: [envDetector] }));
  context.setGlobalContextManager(new AsyncLocalStorageContextManager().enable());
  propagation.setGlobalPropagator(new W3CTraceContextPropagator());
  const test = !!(hooks.spanExporter || hooks.metricExporter || hooks.logExporter);
  const spanExporters = [...(exporting ? [new OTLPTraceExporter()] : []), ...(hooks.spanExporter ? [hooks.spanExporter] : [])].map((e) => new MaskingSpanExporter(e));
  const tracer = new TracerProvider({ resource, spanProcessors: spanExporters.map((e) => (test ? new SimpleSpanProcessor({ exporter: e }) : new BatchSpanProcessor({ exporter: e }))) });
  trace.setGlobalTracerProvider(tracer);
  const metricExporters = [...(exporting ? [new OTLPMetricExporter()] : []), ...(hooks.metricExporter ? [hooks.metricExporter] : [])];
  const meter = new MeterProvider({ resource, readers: metricExporters.map((exporter) => new PeriodicExportingMetricReader({ exporter, exportIntervalMillis: test ? 3600000 : 15000 })) });
  metrics.setGlobalMeterProvider(meter);
  const logExporters = [...(exporting ? [new OTLPLogExporter()] : []), ...(hooks.logExporter ? [hooks.logExporter] : [])].map((e) => new MaskingLogExporter(e));
  const logger = new LoggerProvider({ resource, processors: logExporters.map((e) => (test ? new SimpleLogRecordProcessor({ exporter: e }) : new BatchLogRecordProcessor({ exporter: e }))) });
  logs.setGlobalLoggerProvider(logger);
  instruments = undefined;
  const quietly = async (p: () => Promise<unknown>) => { try { await p(); } catch { /* telemetry never stops the work */ } };
  installed = {
    flush: async () => { await quietly(() => tracer.forceFlush()); await quietly(() => meter.forceFlush()); await quietly(() => logger.forceFlush()); },
    shutdown: async () => {
      await installed?.flush();
      await quietly(() => tracer.shutdown()); await quietly(() => meter.shutdown()); await quietly(() => logger.shutdown());
      trace.disable(); metrics.disable(); logs.disable(); context.disable(); propagation.disable();
      installed = undefined;
      instruments = undefined;
    },
  };
  return installed;
}

export async function flushTelemetry() { await installed?.flush(); }
export async function shutdownTelemetry() { await installed?.shutdown(); }

export function tracer() { return trace.getTracer(NAME, '1.0.0'); }

/** Run fn in a new span; the span ends with fn, an error recorded by type only (a message may quote data). */
export async function inSpan<T>(name: string, attrs: Attributes, fn: (span: Span) => Promise<T> | T, opts: { kind?: SpanKind; parent?: Context } = {}): Promise<T> {
  return tracer().startActiveSpan(name, { kind: opts.kind ?? SpanKind.INTERNAL, attributes: attrs }, opts.parent ?? context.active(), async (span) => {
    try {
      return await fn(span);
    } catch (e: any) {
      span.setStatus({ code: SpanStatusCode.ERROR, message: e?.name ?? 'Error' });
      span.addEvent('exception', { 'exception.type': e?.name ?? 'Error' });
      throw e;
    } finally {
      span.end();
    }
  });
}

/** The context a request carries (traceparent), for a server span. */
export function contextFrom(headers: Record<string, string | string[] | undefined>): Context {
  return propagation.extract(context.active(), headers);
}

/** The headers that carry the active trace to another process. */
export function traceHeaders(): Record<string, string> {
  const carrier: Record<string, string> = {};
  propagation.inject(context.active(), carrier);
  return carrier;
}

type Instruments = ReturnType<typeof makeInstruments>;
let instruments: Instruments | undefined;

function makeInstruments() {
  const m = metrics.getMeter(NAME, '1.0.0');
  return {
    workerAttempt: m.createHistogram('th.worker.attempt.duration', { unit: 's', description: 'Wall time of one activity attempt in a deployed worker' }),
    workerErrors: m.createCounter('th.worker.attempt.errors', { description: 'Attempts a worker answered with an error' }),
    portCall: m.createHistogram('th.port.call.duration', { unit: 's', description: 'Wall time of one call on a port (a simulator, the native host or a bank connection)' }),
    portErrors: m.createCounter('th.port.call.errors', { description: 'Port calls that failed or were not answered' }),
    holds: m.createCounter('th.holds', { description: 'Holds raised (an attempt of a hold activity)' }),
    holdsOpen: m.createUpDownCounter('th.holds.open', { description: 'Holds waiting for their decision' }),
    payments: m.createCounter('th.payments', { description: 'Payments (a file is one) that ended, by outcome, scheme, tenant and engine' }),
    paymentDuration: m.createHistogram('th.payment.duration', { unit: 's', description: 'Wall time of a payment from intake to its end' }),
    httpServer: m.createHistogram('http.server.request.duration', { unit: 's', description: 'Duration of HTTP server requests' }),
  };
}

/** The studio's instruments, made on first use after initTelemetry (no-ops when nothing is installed). */
export function meters(): Instruments {
  return (instruments ??= makeInstruments());
}

/** Record with masked attributes. */
export function attrs(a: Record<string, unknown>): Attributes {
  return maskAttributes(Object.fromEntries(Object.entries(a).filter(([, v]) => v !== undefined))) as Attributes;
}

const SEVERITY: Record<string, SeverityNumber> = { debug: SeverityNumber.DEBUG, info: SeverityNumber.INFO, warn: SeverityNumber.WARN, error: SeverityNumber.ERROR };

/**
 * A structured log record with named parameters, correlated with the active span: the template's holes {name} are
 * filled from the masked values, never by string interpolation at the call site.
 */
export function log(level: 'debug' | 'info' | 'warn' | 'error', template: string, values: Record<string, unknown> = {}) {
  if (!installed) return;
  const masked: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(values)) masked[k] = maskValue(k, v);
  const body = template.replace(/\{([^{}]+)\}/g, (m, k) => (k in masked ? String(masked[k]) : m));
  logs.getLogger(NAME, '1.0.0').emit({ severityNumber: SEVERITY[level], severityText: level.toUpperCase(), body, attributes: { ...(masked as any), 'log.template': template }, context: context.active() });
}

export { SpanKind, SpanStatusCode };
